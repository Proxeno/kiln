![Kiln](https://raw.githubusercontent.com/Proxeno/kiln/main/docs/assets/hero.png)

# Kiln

[![CI](https://github.com/Proxeno/kiln/actions/workflows/ci.yml/badge.svg)](https://github.com/Proxeno/kiln/actions/workflows/ci.yml)
[![NuGet version](https://img.shields.io/nuget/v/Proxeno.Kiln)](https://www.nuget.org/packages/Proxeno.Kiln)
[![NuGet downloads](https://img.shields.io/nuget/dt/Proxeno.Kiln)](https://www.nuget.org/packages/Proxeno.Kiln)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-e8912d)](https://github.com/Proxeno/kiln/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-e8912d)](https://dotnet.microsoft.com)

**A from-scratch H.264 encoder for .NET.** Pure managed, SIMD-accelerated C#, zero native
dependencies, Apache-2.0. Feed it raw frames and it hands back a standards-compliant H.264 baseline
bitstream — the whole codec (bitstream, transforms, intra/inter prediction, motion search, entropy
coding, deblocking) is implemented here, in this repository, against the ITU-T H.264 specification.
No native codec to cross-compile, ship, or keep patched.

> **Status: pre-release (0.x).** APIs will change. Every capability listed here is backed by a test
> in this repository — including smoke tests that decode every produced stream with an independent
> reference decoder as an oracle — and the [What Kiln is not](#what-kiln-is-not) section says plainly
> what isn't here.

## What you can build

Kiln is the **encode** step: one .NET process turns rendered or captured frames into an H.264 stream
you can send anywhere, with no native runtime on the box. It's built for low-latency, real-time
video, such as:

- **Game & cloud-gaming streaming** — render and encode on a server, play in a browser or thin
  client with well-under-a-second glass-to-glass latency.
- **Screen capture & remote desktop** — a headless host encoding its own output frame by frame.
- **Camera & robotics video** — live feeds from cameras, drones, or robots to an operator's screen.
- **A source for a WebRTC / RTP pipeline** — Kiln emits Annex B access units that drop straight into
  a stack like [Keryx](https://www.nuget.org/packages/Proxeno.Keryx) or any RTP packetizer.
- **Anywhere** you need low-latency managed H.264 inside a .NET process without bundling a native
  codec or taking on GPL/LGPL linkage.

## Goals

- **Pure managed.** 100% C# on .NET 10 with hardware intrinsics — NEON/AdvSimd on arm64, AVX2 and
  SSSE3 on x64, scalar fallback everywhere else. No native library to cross-compile, ship, or patch.
- **Genuinely open.** Apache-2.0, original work that links no copyleft code — embed it in commercial
  or proprietary products where GPL/LGPL codec linkage is a problem. That's exactly why it exists.
- **Faithful to the spec.** Written against ITU-T H.264 (ISO/IEC 14496-10); every numeric table that
  originates in the spec carries its clause/table citation in the source (`Table 9-4`, `§8.5.9`,
  `§9.2.1`, …).
- **Real-time first.** Predictable per-frame latency over maximum compression: a steady stream of
  low-latency frames beats a slow exhaustive search.
- **Honest about scope.** A 0.x that tells you exactly what is proven and what isn't here yet.

## Quick start

```csharp
using Kiln;
using Kiln.RateControl;

var encoder = new H264BaselineEncoder(1280, 720, new H264BaselineEncoderOptions
{
    QuantizationParameter = 28,
    KeyframeIntervalFrames = 120,
    SliceCount = 4,                         // parallel slice encoding
    SpeedMode = EncoderSpeedMode.Balanced,  // measured speed/quality preset
});

var annexB = new byte[1280 * 720 * 2];

// Planar I420 input; u/v are half-resolution planes.
var written = encoder.EncodeFrame(y, u, v, strideY: 1280, strideUv: 640, annexB);
var wasIdr = encoder.LastFrameWasIdr;
// annexB[0..written] is a complete Annex B access unit (SPS/PPS included on IDR).
```

## What you get

A real encoder, not a toy — the parts a low-latency streaming server actually needs:

- **Baseline bitstream that just plays.** Constrained baseline profile, IDR + P-frames, CAVLC
  entropy coding, Annex B output (SPS/PPS carried on every IDR) that streams straight into a WebRTC
  or RTP packetizer and decodes on browsers and hardware decoders.
- **Genuine coding tools.** Intra 4×4 / 16×16 with RD mode selection, P-slice inter prediction with
  sub-pel SATD motion search, P_Skip, and multiple reference frames.
- **Quality knobs.** In-loop deblocking, optional greedy trellis quantization, variance-based
  spatial adaptive QP, and per-frame rate control.
- **A measured speed ladder.** Four `SpeedMode` presets over the motion-search knobs, each a
  measured point on the speed/quality curve, plus a deterministic per-frame motion-search work
  budget that bounds the worst-case frame time without making bitstreams timing-dependent.
- **Parallelism built in.** Multi-slice frames encode their slices in parallel and bound the region a
  lost packet can damage.
- **SIMD with a safety net.** NEON/AdvSimd, AVX2 and SSSE3 kernels selected at runtime, each covered
  by parity tests against a scalar reference; CI runs the full suite on Linux, Windows and macOS so
  both architectures stay green.
- **A wired adaptation loop.** `H264StreamingSession` connects `Kiln.RateControl` (low-latency
  rate controller with network feedback) and `Kiln.Recovery` (IDR budgeting / keyframe recovery)
  to the encoder: feed it network feedback per frame — loss, RTT, queue depth, PLI/FIR, a
  transport bandwidth estimate that hard-caps the target bitrate, jitter as a queueing early
  warning, and client decode delay for complexity (not bitrate) relief — and get adaptive QP,
  bitrate, IDRs, and live speed-mode changes out, deterministically, with no glue code on your
  side.
- **Verified.** 2,292 tests — spec-roundtrip decoding, SIMD/scalar parity, golden-frame regression,
  PSNR fidelity floors, adversarial neighbour-availability sweeps, byte-exact
  reconstruction-vs-ffmpeg conformance oracles, and independent-decoder smoke tests over every
  produced stream.

## Options reference

| Option | Default | What it does |
|---|---|---|
| `QuantizationParameter` | 28 | Base QP, 0–51. |
| `SpeedMode` | `HighQuality` | Measured speed/quality preset ladder (`HighQuality`/`Balanced`/`Fast`/`VeryFast`) over `MaxReferenceFrames`, `UseMotionSatd`, `SubPartitionRangeCap` and `MotionSearchEffortCapPerMb`. Any of those four you assign explicitly wins over the mode; other options are never touched. See [Performance](#performance) for what each rung buys and costs. |
| `KeyframeIntervalFrames` | 60 | IDR every N coded frames (frame 0 is always IDR). `EncodeFrame(forceKeyframe: true)` overrides. |
| `SliceCount` | 1 | Slices per frame; >1 encodes slices in parallel and bounds loss regions. |
| `MaxReferenceFrames` | 2 | 1 = single-ref (WebRTC / hardware-decoder safe), 2 = multi-reference P. Sets the SPS ceiling; below it the operating count follows live `ApplySpeedMode`/`ApplySpeedKnobs` calls, mid-GOP, no IDR needed. |
| `TargetBitsPerFrame` | 0 (off) | Per-MB QP adaptation toward a per-frame bit budget. Overridable per picture via `EncodeFrame(targetBitsPerFrame:)` — how the streaming session drives a live bitrate target. |
| `FastSearch` | true | Hex/diamond integer ME + qpel refinement; false = exhaustive integer search. |
| `UseMotionSatd` | true | SATD scoring for integer-pel ME candidates (SAD for fractional refinement). |
| `EnableIntraInPFallback` | true | Allows I16x16/I4x4 macroblocks inside P-frames when inter prediction fails. |
| `IntraRefreshPeriodFrames` | 0 (off) | Gradual intra refresh: N > 0 enables it (sets `constrained_intra_pred_flag` in the PPS) and spreads each `RequestIntraRefresh()` wave over up to N frames — an intra MB-column band sweeps the picture with motion vectors of refreshed MBs restricted to the refreshed reference region, so a decoder joining at the wave start converges byte-exactly without an IDR. Measured at 640×480 QP 30 on motion content: recovery costs a max of ~2.7× a typical P-frame per frame for N frames instead of one ~14× IDR spike; the standing cost of the flag with no wave running is ≈ +0.3–1.1% bits, ±0.1 dB. |
| `TrellisLevel` | 0 | 1 = greedy per-coefficient trellis quantization (better RD, ~5% CPU). |
| `AdaptiveQuantStrength` | 0.0 | Variance-based spatial AQ; 1.0 = standard, typical 0.5–1.5. |
| `PreferRealtimeLatencyTuning` | false | Skips chroma-DC RD refinement for inter-coded chroma. Does not bound motion-search cost — that's `MotionSearchEffortCapPerMb`. |
| `LightweightDeblocking` | false | Disables in-loop deblocking (bitstream-signalled) to cut CPU. |
| `PreferHardwareIntrinsics` | true | Runtime SIMD kernel selection; false forces scalar. |
| `SubPartitionRangeCap` | 16 | Sub-partition ME radius cap (per-frame complexity budget applies). A speed knob: 8 is ~20% faster per frame and 4 ~30% faster. Quality-neutral (±0.01 dB) on coherent motion; on divergent motion 8 costs about −0.17 dB / +13.5% bits at QP 24. |
| `MotionSearchEffortCapPerMb` | 0 (off) | Deterministic worst-case frame-time bound: caps motion-search work per frame (units/MB; each slice gets an equal share) and degrades the search in steps as the budget depletes, paying bitrate/PSNR only when the cap binds. Bitstreams stay reproducible — the count is algorithmic work, not wall clock. Set by the non-default `SpeedMode` presets; see [Performance](#performance) for measured bounds and prices. |
| `ProfileIdc` / `LevelIdc` | 66 / 0 (auto) | Signalled profile (baseline) and level. `LevelIdc = 0` auto-selects the lowest level whose MaxFS admits the frame, floored at 3.1; set explicitly to pin a level. |
| `ChromaDcRdLambda`, `Intra4x4SadLambda` | derived | Expert RD-lambda overrides; leave null. |

## Performance

Measured on Apple M5 Max (arm64, NEON/AdvSimd), .NET 10, quiet machine, QP 28, steady P-frames
over deterministic synthetic content. Every number is reproducible from the harnesses in
`bench/Kiln.Benchmarks` (`--slice-quick`, `--speed-modes`, `--speed-modes-timing`,
`--speed-modes-tiers`), with competing configurations interleaved in one process so scheduling
drift hits every arm equally. Three questions, in the order a deployment asks them: what does the
default cost, what can a speed mode buy, and what is the worst case.

### Default quality on realistic content

Steady P-frame medians on textured scroll-plus-noise content ("coherent motion" — a camera pan or
game scroll; neither a best case nor a stress case), default options:

| Resolution | 1 slice | 2 slices | 4 slices | 8 slices |
|---|---:|---:|---:|---:|
| 640x480 | 17.3 ms | 8.6 ms | 5.6 ms | 4.0 ms |
| 1280x720 | 19.9 ms | 16.0 ms | 9.4 ms | 7.5 ms |
| 1920x1080 | 24.4 ms | 16.9 ms | 10.9 ms | 10.8 ms |

Slices do not divide the work cleanly. Four slices buy about 2.2x at 1080p — not 4x — and eight
buy essentially nothing beyond four: per-slice motion cost doesn't balance perfectly, and the
frame still pays serial per-frame work. Slices also cost bits, because slice boundaries reset
MV/skip prediction: measured +5% to +26% bitrate at QP 28 going from 1 to 4 slices depending on
content (up to ~+40% on cheap coherent frames at QP 23). Choose `SliceCount` for latency and
packet-loss confinement and budget for both costs; do not expect linear returns.

### The speed-mode ladder: best achievable and its price

`H264BaselineEncoderOptions.SpeedMode` selects a measured preset over four motion-search knobs
(`MaxReferenceFrames`, `UseMotionSatd`, `SubPartitionRangeCap`, `MotionSearchEffortCapPerMb`); any
of those you assign explicitly wins over the mode. All rungs keep bitstreams deterministic — the
effort budgets count algorithmic work, never wall clock. 1080p steady-P medians:

| Mode | Sets | Coherent, s=1 / s=4 | Divergent worst case, s=1 / s=4 |
|---|---|---:|---:|
| `HighQuality` (default) | 2 refs, SATD ME, full sub-partition range, no cap | 26.6 / 10.0 ms | 205 / 70 ms |
| `Balanced` | 1 ref, effort cap 512/MB | 18.8 / 8.2 ms | 80 / 30 ms |
| `Fast` | + sub-partition radius 8, cap 256 | 15.6 / 7.7 ms | 64 / 24 ms |
| `VeryFast` | + SAD-scored ME, cap 128 | 8.9 / 4.0 ms | 20 / 7.8 ms |

And the quality price, 1080p QP 28, PSNR / bitrate versus `HighQuality` by content class:

| Mode | Static | Coherent motion | High motion | Scene cut | Divergent motion |
|---|---|---|---|---|---|
| `Balanced` | 0 | −0.1 dB, +1% | −1.6 dB, +12% | +0.3 dB, −3% | −0.4 dB, +40% |
| `Fast` | 0 | −0.1 dB, +1% | −1.7 dB, +11% | +0.4 dB, −4% | −0.5 dB, +55% |
| `VeryFast` | 0 | −1.2 dB, +7% | −3.5 dB, +21% | −0.9 dB, +2% | −1.5 dB, +64% |

Read it as: on static and coherent content `Balanced` and `Fast` are near-free and `VeryFast`
costs about a decibel; when content turns violent, the capped modes hold their frame time and pay
in quality and bits exactly there. The price concentrates at low QP, where frames that genuinely
need a wide search are cut off hardest — at QP 23 `Balanced` measures −6.8 dB on the high-motion
generator and −3.7 dB on scene cuts. If you run QP ≤ 23 on demanding content, prefer
`HighQuality`, or compose your own point on the curve (e.g. `SpeedMode = Balanced` with an
explicit, higher `MotionSearchEffortCapPerMb` — the explicit knob wins).

### Worst case

The number that breaks a real-time deployment is not the mean but the hostile frame. On the
divergent-motion stress generator (opposing half-screen scrolls plus counter-moving blocks) the
default configuration measures **205 ms/frame** at 1080p single-slice (70 ms at 4 slices) against
27/10 ms on coherent content in the same run — a ~7x content-dependent swing with no bound. The bound is
`MotionSearchEffortCapPerMb` (set by every non-default speed mode): a deterministic per-frame
work budget that degrades the search in steps as it depletes, cutting that worst case to 80 ms
(`Balanced`) / 64 ms (`Fast`) / 20 ms (`VeryFast`) single-slice, while charging quality only when
it actually binds. If your content is unusual, size the cap yourself — the
`--speed-modes-tiers` harness shows how hard a given cap binds per content class.

**For most real-time use, bound the tail without taking the quality trade.** The default
`HighQuality` is the only mode that leaves `MotionSearchEffortCapPerMb` at 0, so it is also the only
one with no worst-case bound — 10 ms typical at 1080p 4-slice, but 70 ms (14 fps) on a hostile
frame. Because an explicitly-set option always beats the mode, you can keep every `HighQuality`
coding decision and add just the bound:

```csharp
var encoder = new H264BaselineEncoder(1920, 1080, new H264BaselineEncoderOptions
{
    SpeedMode = EncoderSpeedMode.HighQuality,   // 2 refs, SATD ME, full sub-partition range
    MotionSearchEffortCapPerMb = 512,           // explicit assignment overrides the mode's 0
});
```

That costs about −0.01 dB on coherent content — the cap charges quality only where it binds — and
pulls the hostile case in to roughly 35-40 ms at 4 slices. Prefer it over `Balanced` when you want
the bound but not the single-reference trade, which is what costs `Balanced` its −0.7 to −2.9 dB on
high-motion content at QP ≤ 28.

### Microbenchmarks and the perf gate

The committed perf-gate baselines (`perf/`, BenchmarkDotNet):

| Benchmark | Mean | Min |
|---|---:|---:|
| SATD 4x4 kernel (`Satd4x4_Once`) | 11.2 ns | 11.1 ns |
| SATD 4x4 x 9 intra modes (`SatdMany4x4_9Modes`) | 99.3 ns | 99.2 ns |
| SAD 8x8 dispatch | 3.5 ns | 3.4 ns |
| SAD 16x16 dispatch (stride 720) | 6.4 ns | 6.4 ns |
| Full-MB 16x16 ME search, range 8 | 76.4 us | 76.1 us |
| Steady P-frame encode, 1280x720, 1 slice | 2.24 ms | 2.20 ms |

That 2.24 ms row is measured on **near-static synthetic content** — size deployments from the
realistic-content tables above, not from it. Perf discipline is part of the repo:
`bench/Kiln.Benchmarks` plus `scripts/h264-simd-capture-baseline.sh` and
`scripts/h264-simd-perf-gate.sh` gate changes against the committed baseline in `perf/`. See
[docs/perf-gate.md](docs/perf-gate.md).

## What Kiln is not

Not an x264 competitor. No B-frames, no CABAC, no 8×8 transform, no interlace; 4:2:0 8-bit only.
If you need maximum compression at any CPU cost, use a full-profile encoder. If you need
clean-licensed, dependency-free, low-latency H.264 inside a .NET process, you are in the right place.

Frame dimensions need not be multiples of 16: sizes like 1920×1080 or 1366×768 are supported via
SPS frame cropping — the encoder pads to the 16×16 macroblock grid internally and signals the true
display size, which is what decoders output. Dimensions must be **even** (4:2:0 chroma is
subsampled 2×2, so odd extents are unrepresentable). By default the encoder signals the lowest
H.264 level whose frame-size limit (MaxFS, Annex A Table A-1) admits the *padded* picture, floored
at Level 3.1 — 1920×1080 codes as 1920×1088 (8160 macroblocks) and signals Level 4.0 automatically.
Set `H264BaselineEncoderOptions.LevelIdc` explicitly to pin a level; an explicit level that is too
small for the frame throws, naming the lowest sufficient level. The chosen level is readable from
`H264BaselineEncoder.LevelIdc`.

> **0.x behavioural change:** `LevelIdc` previously defaulted to 31 (Level 3.1) and the constructor
> threw for frames above 1280×720. The default is now 0 = auto-select. Streams ≤720p with default
> options are byte-identical to before (the auto floor is still 3.1); larger frames now construct
> and encode instead of throwing. `EncodeFrame`'s output span now has a documented recommended size,
> `H264BaselineEncoder.RecommendedOutputBufferSize`.

> **0.x conformance fix:** two deblocking bugs made the encoder's reconstruction drift from every
> conformant decoder's — the luma filter ignored the §8.7.2.2 QP average across macroblock edges,
> and boundary-strength derivation inverted §8.7.2.1's precedence of coded coefficients over MV
> differences. Streams using per-MB QP (`TargetBitsPerFrame`, `AdaptiveQuantStrength`) and
> constant-QP streams at QPs where Table 8-17 distinguishes bS 1 from 2 (e.g. 31, 32, 35, 36)
> produce different — now correct — bytes. Default-option streams and QP 23/28/33/34 constant-QP
> streams are unaffected. Verified byte-exact against both ffmpeg and VideoToolbox.

On the real-time story, be precise about what is wired and what is not. The adaptation loop is
wired: `H264StreamingSession` owns an encoder plus the rate controller, and per frame turns
`EncoderNetworkFeedback` — loss, RTT, queue depth, PLI/FIR, and, when the transport supplies
them, a bandwidth estimate (a hard ceiling on the target bitrate), jitter (a queueing early
warning that tempers upshifts), and client decode delay (complexity relief via the speed/fps/
resolution cascade, never a bitrate cut) — into applied settings — slice QP, a
per-picture bit budget, recovery IDRs, and live `SpeedMode` changes, including the reference-count
component, which swaps mid-GOP without an IDR because the SPS-signalled DPB size is an upper
bound the operating count may sit below:

```csharp
using var session = new H264StreamingSession(1280, 720);
var buffer = new byte[session.RecommendedOutputBufferSize];
// Each frame: pass the latest transport snapshot, get adaptive encoding out.
var result = session.EncodeFrame(y, u, v, strideY, strideUv, buffer, feedback);
// result.WasIdr, result.AppliedSliceQp, result.Decision.TargetBitrateBps, …
```

The session is deterministic (identical frames + feedback → identical bytes; wall-clock load
figures participate only if you pass them in), and it is honest about its limits: resolution
decisions are surfaced as recommendations until you can supply rescaled frames and call
`ChangeResolution` (Kiln has no scaler; the session recreates the encoder and the next frame is an
IDR with the new SPS), and `TargetFps` is a pacing contract for your capture loop (the SPS carries
no timing info). Gradual intra refresh is implemented: construct with
`IntraRefreshPeriodFrames = N` and a recovery request that lands in the IDR cooldown starts a
refresh wave — a band of intra MB columns sweeping the picture over up to N frames, with
`constrained_intra_pred_flag` and motion vectors restricted to the refreshed region, so a decoder
joining at the wave start (SPS/PPS and a recovery-point SEI are repeated there) reconstructs
byte-exactly once the wave completes, for a bounded per-frame premium instead of an IDR-sized
spike. With the option at its default 0 the flag is off and streams are byte-identical to previous
releases. Frame pacing and input dropping stay in your capture loop by design (the capture sample
shows a latest-frame-wins hand-off; report your drops via `EncoderPipelineTimings` and the
controller sees them). The full taxonomy of what may change mid-stream at which boundary is in
[docs/architecture.md](docs/architecture.md).

## Installing

Published on [nuget.org](https://www.nuget.org/packages/Proxeno.Kiln) as `Proxeno.Kiln`. The package
id is `Proxeno.Kiln`; the assembly and namespace stay `Kiln`, so code uses `using Kiln;`.

```
dotnet add package Proxeno.Kiln
```

## Try it

[`samples/Kiln.Capture`](samples/Kiln.Capture) records your camera to a playable `.m4v` — capture,
colour conversion, H.264 encode and MP4 muxing, all managed, no native binaries anywhere in the
pipeline:

```
dotnet run --project samples/Kiln.Capture -- list
dotnet run --project samples/Kiln.Capture -- record --seconds 10 --output capture.m4v
```

## Documentation

- [samples/Kiln.Capture](samples/Kiln.Capture) — camera → `.m4v` sample, and how the MP4 muxing works
- [docs/architecture.md](docs/architecture.md) — pipeline stages, SIMD kernel structure, subsystems
- [docs/perf-gate.md](docs/perf-gate.md) — benchmark baseline + regression gate workflow
- [CONTRIBUTING.md](CONTRIBUTING.md) — build, test, and contribution rules
- [SECURITY.md](SECURITY.md) — reporting vulnerabilities

## License

Apache-2.0 — see [LICENSE](LICENSE). © Kiln contributors.
