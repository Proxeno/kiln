# Architecture

This document describes how Kiln turns raw I420 frames into an Annex B H.264
baseline-profile bitstream, how its SIMD kernels are structured and selected,
and which subsystems are production-used versus experimental. It is written
for contributors; it does not restate the public API (see the README).

## Encode pipeline

### Frame orchestration — `H264BaselineEncoder`

[`src/Kiln/H264BaselineEncoder.cs`](../src/Kiln/H264BaselineEncoder.cs) is the
public entry point (`H264BaselineEncoder.EncodeFrame`). Per encoder instance
it owns:

- One `H264FrameSharedState` — the picture-sized reconstruction, reference,
  and per-MB neighbour caches shared by every slice.
- A pool of up to 8 `H264BaselineSliceEncoder` instances (one per parallel
  slice), each with independent RBSP buffer, rate control, and slice-header
  state.
- The SPS/PPS RBSP, built once at construction by `H264ParameterSets` from
  width/height/profile/level/`MaxReferenceFrames`.

**Coded vs display dimensions.** The public API speaks *display* size
(`Width`/`Height`, what the caller passed and what decoders output); everything
below the orchestrator speaks *coded* size (`CodedWidth`/`CodedHeight`, the
display size rounded up to the 16×16 macroblock grid). When they differ, the
constructor sizes all internal state from the coded dimensions and the SPS
carries a frame-cropping block (§7.3.2.1.1) signalling the display size —
right/bottom offsets only, in 2-luma-sample crop units (§7.4.2.1.1, Table 6-1),
which is why dimensions must be even. Per `EncodeFrame`,
`H264SourcePlaneExtender` extends the caller's display-sized I420 planes to
coded size by replicating the last real column/row (not zero-fill: the padding
is coded, deblocked, and stored in the DPB, and the §8.7 loop filter reads it
into activity tests that update *visible* samples across the crop edge;
replication also lets the padding MBs collapse to CBP=0/P_Skip). The extension
step is skipped entirely for aligned dimensions, so those streams remain
byte-identical to earlier releases. Cropping is display-stage-only in H.264:
reconstruction, ME, deblocking, and the DPB all operate on the uncropped coded
picture — exactly as a conformant decoder does — so `LastReconstructedY/U/V`
return the coded plane (stride `CodedWidth`);
`CopyLastReconstructedTo` provides a display-sized crop.

Per `EncodeFrame` call it decides whether the picture is IDR — frame 0,
`forceKeyframe`, or `codedFrameIndex % KeyframeIntervalFrames == 0` — resets
`h264FrameNum` on IDR, emits SPS+PPS ahead of IDR NALs only (not every P
picture — repeating parameter sets in-band before every P-frame is
non-conventional and some hardware decoders mishandle it), and dispatches to
either the single-slice path (`_sliceEncoders[0].EncodeSliceRbsp`, kept
byte-identical to the pre-multi-slice encoder) or `EncodeFrameMultiSlice`.

**Slice parallelism.** When `SliceCount` (or its auto-derived value,
`min(mbRows, max(1, ProcessorCount-1), 8)`) is greater than 1,
`EncodeFrameMultiSlice` pins the caller's Y/U/V spans, then runs
`Parallel.For` over slices on a dedicated `LimitedConcurrencyLevelTaskScheduler`
— a small fixed pool of background threads private to slice encoding, used
instead of the shared `ThreadPool` to avoid injection-delay jitter. Each
slice writes only its own MB-row range of the shared reconstruction/reference
arrays, so the writes are disjoint and race-free; the once-per-frame reset
(`BeginFrame`) and reference padding (`PadReconstructedReference`) happen
outside the parallel region under a single-threaded fence. Multi-slice
pictures set `disable_deblocking_filter_idc=2` (filter within slice only).

### Per-slice macroblock loop — `H264BaselineSliceEncoder`

[`src/Kiln/Internal/H264/H264BaselineSliceEncoder.cs`](../src/Kiln/Internal/H264/H264BaselineSliceEncoder.cs)
(~4,200 lines) does the real work, in `EncodeSliceRbsp`. Per macroblock, in
raster order within the slice's row range:

1. **P-inter attempt** (P-slices with a valid reference only):
   `TryEncodePInterMacroblock` calls into `H264MotionEstimator` for
   integer-pel search (hex/diamond by default, exhaustive when
   `FastSearch=false`) plus qpel refinement, SAD or SATD-scored
   (`UseMotionSatd`), MV median prediction per §8.4.1.3.1, and 16×16 /
   16×8 / 8×16 / 8×8 partition selection (`SearchMbSubPartitions`). Skip
   (`P_Skip`) is decided here too. When `EnableIntraInPFallback` is set
   (default), a Phase-2b step re-scores the inter winner against its best
   Intra_16×16 candidate and lets the cheaper one win — this rescues
   leading-edge scroll/occlusion/scene-change content invisible in every
   reference.
2. **Intra fallback** — `WriteMacroblock` for I-slice MBs and any P-slice MB
   that didn't take the inter path: Intra_4×4 (9 modes, RD-selected via SAD
   or SATD + λ) and Intra_16×16 (4 modes; `EncodeI16x16Macroblock` handles
   the Hadamard-transformed luma DC path). Mode-candidate availability
   follows §8.3.1.1/§8.3.1.2 neighbour rules.
3. **Transform/quant** — `H264BlockTransform` (scalar) and its SIMD
   counterparts `H264BlockTransformSimd` / `H264Dct4x4Simd` /
   `H264BlockTransformDequantSimd` do forward 4×4 DCT, quantization, and
   the luma-DC Hadamard for I16×16 (`H264LumaDcHadamard`) / chroma DC
   (`H264ChromaDcScale`). `TrellisLevel=1` runs `H264TrellisQuant4x4`, a
   greedy per-coefficient RD trellis pass.
4. **Entropy coding (CAVLC)** — `H264CavlcResidual` walks each 4×4 (and
   chroma-DC) block using tables from `H264CavlcTables`, writing directly
   into the slice's bit buffer.
5. **Bitstream assembly** — `H264RbspBitBuffer` is the MSB-first bit
   accumulator; `H264RbspEmulation` applies emulation-prevention (EBSP);
   `H264AnnexB.AppendNal` wraps each NAL with a start code.
6. **Reconstruction & deblocking** — every MB's residual is added back into
   the shared reconstruction planes as it's coded (needed as intra
   prediction input for later MBs in the same slice/frame). After the MB
   loop, `ApplyInLoopDeblock` (single-slice) or `ApplyInLoopDeblockScoped`
   (multi-slice, row-range-bounded) runs `H264DeblockingFilter` /
   `H264DeblockingFilterSimd`, skipped entirely when
   `LightweightDeblocking=true`.
7. **Reference handling** — `RotateDpbAndPad` (single-slice) or the
   orchestrator's `PadReconstructedReference` call (multi-slice) rotates the
   2-entry DPB (`H264FrameSharedState.MaxDpbSize`) and rebuilds the padded
   reference halo via `H264ReferencePicturePadder` (§8.4.2.1 border
   replication — 16px luma halo, 8px chroma halo, sized for the 6-tap qpel
   and bilinear chroma filters).

Per-MB QP comes from an internal, encoder-private `H264RateControl`
([`Internal/H264/H264RateControl.cs`](../src/Kiln/Internal/H264/H264RateControl.cs)),
a proportional controller active only when a per-frame bit budget is in
effect — the constructor's `TargetBitsPerFrame`, or a positive per-picture
override passed to `EncodeFrame(targetBitsPerFrame:)` (how the streaming
session drives a live bitrate target); constant-QP otherwise. This is a
different, lower-level thing than the public `Kiln.RateControl` namespace
described below — see that section for how the two relate.

## SIMD kernel structure

Every hot per-block/per-MB operation (SAD, SATD, intra prediction, DCT/quant,
deblocking, qpel/bilinear interpolation, source gather) is abstracted behind
[`IH264KernelSet`](../src/Kiln/Internal/H264/IH264KernelSet.cs), an internal
interface. `H264BaselineSliceEncoder` holds one resolved `IH264KernelSet`
per encoder instance — **no per-call ISA dispatch**, the kernel set is picked
once at construction:

```csharp
// H264KernelSet.CreateBest()
if (Avx2.IsSupported)         return new Avx2KernelSet();
if (Ssse3.IsSupported)        return new Ssse3KernelSet();
if (AdvSimd.Arm64.IsSupported) return new Neon64KernelSet();
if (AdvSimd.IsSupported)      return new NeonKernelSet();
return new ScalarKernelSet();
```
([`Internal/H264/H264KernelSet.cs`](../src/Kiln/Internal/H264/H264KernelSet.cs))

`H264BaselineEncoderOptions.PreferHardwareIntrinsics` (default `true`)
gates this: when `false`, the encoder always uses `ScalarKernelSet`
regardless of what the CPU supports.

`SimdKernelSetBase` ([`Internal/H264/SimdKernelSetBase.cs`](../src/Kiln/Internal/H264/SimdKernelSetBase.cs))
is an abstract base shared by every intrinsics-backed set: it implements
SATD, intra prediction, transform/quant, deblocking, interpolation, and
source gather identically (delegating to shared SIMD helper types like
`H264MotionSatd`, `H264Intra4X4Prediction`, `H264QpelLumaInterp`), and leaves
only the SAD family (`Sad16x16`/`Sad16x8`/`Sad8x16`/`Sad8x8`/`SadMany4x4`/
`SadIntra16x16`/`SadChromaPair`) abstract for each platform tier to override
with its own intrinsics. Concrete sets:

| Kernel set | ISA | File |
|---|---|---|
| `Avx2KernelSet` | x64 AVX2 | [`Internal/H264/Avx2KernelSet.cs`](../src/Kiln/Internal/H264/Avx2KernelSet.cs) |
| `Ssse3KernelSet` | x64 SSSE3 | [`Internal/H264/Ssse3KernelSet.cs`](../src/Kiln/Internal/H264/Ssse3KernelSet.cs) |
| `Neon64KernelSet` | arm64 AdvSimd.Arm64 | [`Internal/H264/Neon64KernelSet.cs`](../src/Kiln/Internal/H264/Neon64KernelSet.cs) |
| `NeonKernelSet` | arm AdvSimd | [`Internal/H264/NeonKernelSet.cs`](../src/Kiln/Internal/H264/NeonKernelSet.cs) |
| `ScalarKernelSet` | none (pure C#) | [`Internal/H264/ScalarKernelSet.cs`](../src/Kiln/Internal/H264/ScalarKernelSet.cs) |

**Parity-test philosophy.** Every SIMD code path has a corresponding scalar
implementation, and every kernel has a test asserting they produce identical
output — not just similar, bit-identical, since the encoder's reconstruction
must stay in sync with what the CAVLC/bitstream layer commits to. Tests
follow the pattern `<Thing>_simd_matches_scalar_when_intrinsics_available`
and self-skip via an `IsSupported` check when the host CPU (or CI runner)
lacks the target ISA. See `tests/Kiln.Tests/H264SimdParityTests.cs`,
`H264KernelSetPredictParityTests.cs`, `H264KernelSetTransformParityTests.cs`,
`H264DeblockingFilterSimdParityTests.cs`, `H264QpelLumaInterpSimdParityTests.cs`,
`H264BilinearChromaInterpSimdParityTests.cs`, `H264MotionSadAvx2ParityTests.cs`,
`H264MotionSatdParityTests.cs`, and the `H264Intra4x4*`/`H264Intra16x16*` SIMD
test files. CI runs the full suite on `ubuntu-latest` and `windows-latest`
(x64: AVX2 and SSSE3) and `macos-latest` (arm64: NEON), so both ISA families
are exercised on every push — see [`.github/workflows/ci.yml`](../.github/workflows/ci.yml).

## Rate control & recovery (public, production-used)

`Kiln.RateControl` ([`src/Kiln/RateControl/`](../src/Kiln/RateControl/)) and
`Kiln.Recovery` ([`src/Kiln/Recovery/`](../src/Kiln/Recovery/)) are a
frame-level decision layer above the encoder, and
**`Kiln.H264StreamingSession`**
([`src/Kiln/H264StreamingSession.cs`](../src/Kiln/H264StreamingSession.cs))
is the feedback path connecting the two: it owns one `H264BaselineEncoder`
plus one adaptive controller, and per frame turns `EncoderNetworkFeedback`
into applied encoder settings — slice QP, a per-picture bit budget
(`TargetBitrateBps / TargetFps`), recovery IDRs, and live speed-mode
changes. A streaming server calls
`session.EncodeFrame(y, u, v, …, annexB, feedback)` and gets adaptive
encoding out without writing the glue itself. Callers who want to run their
own loop can still drive `LowLatencyRateController.Decide(...)` directly
and apply decisions with the same primitives the session uses
(`EncodeFrame(sliceLumaQp:, targetBitsPerFrame:, forceKeyframe:)` and
`ApplySpeedMode`/`ApplySpeedKnobs`).

- `LowLatencyRateController` ([`RateControl/LowLatencyRateController.cs`](../src/Kiln/RateControl/LowLatencyRateController.cs))
  is the per-frame decision core (bitrate up/downshift, QP tracking ~+6 per
  halving of the target bitrate, max-frame-bytes budget). **Ownership
  contract:** it owns exactly one `H264RecoveryPolicy` instance and invokes
  `DecideRecovery` exactly once per `Decide()` call, folding the result in.
  Composing code must not call the recovery policy a second time or
  construct a second instance — doing so double-advances the stateful IDR
  cooldown and halves keyframe-storm protection (a regression this codebase
  has hit before; see the class doc comment). Composing layers report what
  they actually applied via `SyncAppliedState(width, height, fps, mode)`;
  without it the controller's internal geometry state stays at its
  constructor defaults and ladder-based adaptation walks from fiction.
- `RateControlConfig` / `RateControlState` hold tunables (bitrate bounds, QP
  bounds, burst allowance, downshift factor, `SupportedWidths`/`Heights`/
  `Fps` ladder rungs) and mutable per-session state.
- `Kiln.Recovery.H264RecoveryPolicy` ([`Recovery/H264RecoveryPolicy.cs`](../src/Kiln/Recovery/H264RecoveryPolicy.cs))
  turns client PLI/FIR feedback into `ForceIdr`/`EnableIntraRefresh`
  decisions, with an IDR cooldown to prevent keyframe storms (FIR takes
  priority over PLI).
- `IdrBudget` ([`Recovery/IdrBudget.cs`](../src/Kiln/Recovery/IdrBudget.cs))
  computes the larger byte budget IDR frames are allowed (2× the normal
  per-frame budget by default).
- `IIntraRefreshPolicy` / `IntraRefreshPolicyStub`: the interface is defined
  and consumed by the decision types, but the only implementation shipped
  today is an explicit no-op stub — gradual slice-level intra refresh
  (as opposed to full IDR) is **not implemented**. The session does not
  pretend otherwise: a recovery decision that asks for intra refresh (a
  PLI/FIR during IDR cooldown) is surfaced on the per-frame result as
  `IntraRefreshRequested` while a normal frame is encoded.

### What can change when: the three adaptation tiers

Every adaptation input falls into one of three tiers, and the tiering is
what an integrator must understand to change anything mid-stream safely:

1. **Free per frame** — inputs that touch no bitstream-structural state.
   Slice luma QP (`EncodeFrame(sliceLumaQp:)`, coded as `slice_qp_delta`),
   the per-picture bit budget (`EncodeFrame(targetBitsPerFrame:)`, driving
   the per-MB `mb_qp_delta` chain), forced IDRs
   (`EncodeFrame(forceKeyframe: true)`), and the three search-only speed
   knobs (`UseMotionSatd`, `SubPartitionRangeCap`,
   `MotionSearchEffortCapPerMb`, reassigned live via
   `H264BaselineEncoder.ApplySpeedMode`/`ApplySpeedKnobs`). Search knobs
   change which prediction the encoder *chooses*, never how a choice is
   coded, so any change at a frame boundary yields a stream a decoder reads
   exactly as if those values had been set from the start.
2. **Bounded by the SPS: the reference-frame count.** `max_num_ref_frames`
   is signalled once in the SPS and a decoder sizes its DPB from it
   (§7.4.2.1) — but it is an *upper bound*, not a per-slice requirement.
   Below it, the operating reference count is a per-frame decision: each P
   slice signals its own active count (`num_ref_idx_active_override_flag`,
   §7.3.3), derived here from the encoder-side DPB occupancy. Lowering the
   cap takes effect immediately (the occupancy is clamped); raising it takes
   effect one frame later, after the reference rotation refills the retired
   DPB slot from fresh reconstructions — the decoder's sliding window
   (§8.2.5.3) retained both pictures throughout, so **no IDR is needed in
   either direction**. This is why `SpeedMode` swaps freely mid-GOP.
   The one hard rule: the cap can never exceed the signalled maximum, so
   `ApplySpeedKnobs` clamps to it. The session therefore reserves the full
   DPB in the SPS (signalling 2) unless the caller explicitly set
   `MaxReferenceFrames` — an explicit value (e.g. 1 for strict hardware
   decoders) is a compatibility contract that caps every mode for the whole
   session. The v0.3.0 assessment that the DPB is "constructor-baked" was
   true only of the signalled maximum, not the operating count.
3. **Requires a new encoder: resolution.** A resolution change means a new
   SPS and new buffers. `H264StreamingSession.ChangeResolution` handles it
   by recreating the encoder transparently — the next frame is an IDR
   carrying the new parameter sets, and decoders (ffmpeg verified)
   reconfigure at that boundary. It cannot be automatic because Kiln has no
   scaler: the controller's resolution recommendations are surfaced
   (`ResolutionChangeRecommended`) until the caller can supply rescaled
   frames and calls `ChangeResolution`.

Frame rate is not on the ladder at all: the SPS carries no VUI timing
(`timing_info_present_flag = 0`), so `TargetFps` has zero bitstream effect.
It is a pacing contract — the session budgets per-frame bits at the decided
fps and the caller is expected to pace capture accordingly (pin
`RateControlConfig.SupportedFps` to a single rung to keep it fixed).

Determinism holds through all three tiers: adaptation inputs are ordinary
inputs, applied between frames, so identical frames plus identical feedback
produce identical bitstreams. The session derives `EncoderPipelineStats`
from the bitstream itself (bytes/QP/IDR of the previous frame) and reports
wall-clock fields as zero unless the caller passes `EncoderPipelineTimings`
explicitly — timings are then inputs too, and replaying them replays the
stream.

Tests: `tests/Kiln.Tests/H264StreamingSessionTests.cs` (adaptation taking
effect + ffmpeg oracle across every tier transition),
`H264DynamicReconfigurationTests.cs` (mid-GOP knob/reference changes,
byte-exact reconstruction parity), and
`AdaptiveRateControlTests/Phase1..5` (controller-level behaviour).

## Experimental subsystems

- **`Internal/H264/Adaptation/`** — resolution/fps ladder-based adaptation.
  `ResolutionLadder` and `FpsLadder` (built from
  `RateControlConfig.SupportedWidths`/`Heights`/`Fps`; defaults
  1080p→900p→720p→540p→360p and 60→30→15) step one rung at a time;
  `AdaptationPolicy` cascades down speed-mode → fps → resolution under
  sustained congestion and walks back up under sustained stability, with a
  cooldown to prevent flapping. `H264AdaptiveRateController` composes
  `LowLatencyRateController` (bitrate/QP/recovery) with `AdaptationPolicy`
  into one `EncoderAdaptationDecision` per frame. **This layer is now
  consumed in production by `H264StreamingSession`** (it is the session's
  controller); the types themselves remain internal and may change shape.
- **`Internal/H264/Queue/`** — still unwired, preview only:
  `LatestFrameQueue<T>`, a thread-safe depth-≤1 "newest frame wins" queue
  (older pending frame is dropped when a newer one arrives), and
  `FrameDropPolicy`, which decides whether the frame currently being
  encoded is stale enough (default 50 ms / ~3 frame periods at 60fps) and
  has newer frames pending to justify dropping it. Nothing in the encoder
  or the session references them; a server wanting latest-frame semantics
  composes them around its own capture loop.

## Diagnostics (`KILN_*` environment variables)

All diagnostics are opt-in, read once at type initialization (not per-frame
or per-MB), and off by default. Grep for `GetEnvironmentVariable` under
`src/Kiln` to re-derive this list if it drifts.

| Variable | Effect |
|---|---|
| `KILN_H264_GRC_INSTRUMENT=1` | Enables candidate-ranking collection in `H264MotionGraphResidualDiagnostics` — shadow instrumentation comparing graph-residual-cost motion-search ranking against SAD ranking (top-1/2/4 winner agreement, margin buckets). |
| `KILN_H264_SATD_DAG_INSTRUMENT=1` | Enables `H264MotionSatdDagDiagnostics` — SATD atom-DAG and cache-reuse counters for motion estimation (atom computes/hits/misses by shape, partition composition/early-exit counts, candidate cache and ring-break stats, search-depth histogram). |
| `KILN_H264_DIAG_DISABLE_P_INTER_PHASE2B=1` | Disables the Phase 2b intra-in-P rescoring step in `H264PInterDiagnostics`/`H264BaselineSliceEncoder` for A/B measurement (same effect as `H264PInterDiagnostics.DisablePhase2bManual` set from code, e.g. in benchmarks). |
| `KILN_H264_P_INTER_TIMING=1` | Enables Phase 2 timing breakdown collection in `H264PInterDiagnostics` (ME / prediction / luma / chroma / write tick counters). |
| `KILN_H264_DIAG_TRACE_MB=<frameNum>,<mbX>,<mbY>[;...]` | Enables per-macroblock trace line collection for the listed `(frameNum, mbX, mbY)` targets (use `frameNum=-1` to match every frame); multiple targets are `;`-separated. Parsed once by `H264PInterDiagnostics.ParseTraceMbTargets`. |

`H264MotionGraphResidualDiagnostics.CollectCandidateRankings` and
`H264PInterDiagnostics.DisablePhase2bManual` / trace-target registration also
have code-level (non-env) toggles for use from benchmarks and tests without
setting process environment variables.

