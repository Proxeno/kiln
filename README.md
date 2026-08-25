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

var encoder = new H264BaselineEncoder(1280, 720, new H264BaselineEncoderOptions
{
    QuantizationParameter = 28,
    KeyframeIntervalFrames = 120,
    SliceCount = 4,               // parallel slice encoding
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
- **Parallelism built in.** Multi-slice frames encode their slices in parallel and bound the region a
  lost packet can damage.
- **SIMD with a safety net.** NEON/AdvSimd, AVX2 and SSSE3 kernels selected at runtime, each covered
  by parity tests against a scalar reference; CI runs the full suite on Linux, Windows and macOS so
  both architectures stay green.
- **Streaming companions.** `Kiln.RateControl` (a low-latency rate controller with network feedback)
  and `Kiln.Recovery` (IDR budgeting / keyframe recovery policy) are public companion namespaces for
  server use.
- **Verified.** 2,174 tests — spec-roundtrip decoding, SIMD/scalar parity, golden-frame regression,
  PSNR fidelity floors, adversarial neighbour-availability sweeps, and independent-decoder smoke
  tests over every produced stream.

## Options reference

| Option | Default | What it does |
|---|---|---|
| `QuantizationParameter` | 28 | Base QP, 0–51. |
| `KeyframeIntervalFrames` | 60 | IDR every N coded frames (frame 0 is always IDR). `EncodeFrame(forceKeyframe: true)` overrides. |
| `SliceCount` | 1 | Slices per frame; >1 encodes slices in parallel and bounds loss regions. |
| `MaxReferenceFrames` | 2 | 1 = single-ref (WebRTC / hardware-decoder safe), 2 = multi-reference P. |
| `TargetBitsPerFrame` | 0 (off) | Per-MB QP adaptation toward a per-frame bit budget. |
| `FastSearch` | true | Hex/diamond integer ME + qpel refinement; false = exhaustive integer search. |
| `UseMotionSatd` | true | SATD scoring for integer-pel ME candidates (SAD for fractional refinement). |
| `EnableIntraInPFallback` | true | Allows I16x16/I4x4 macroblocks inside P-frames when inter prediction fails. |
| `TrellisLevel` | 0 | 1 = greedy per-coefficient trellis quantization (better RD, ~5% CPU). |
| `AdaptiveQuantStrength` | 0.0 | Variance-based spatial AQ; 1.0 = standard, typical 0.5–1.5. |
| `PreferRealtimeLatencyTuning` | false | Speed-biased P-frame ME / chroma-DC handling. |
| `LightweightDeblocking` | false | Disables in-loop deblocking (bitstream-signalled) to cut CPU. |
| `PreferHardwareIntrinsics` | true | Runtime SIMD kernel selection; false forces scalar. |
| `SubPartitionRangeCap` | 16 | Sub-partition ME radius cap (per-frame complexity budget applies). A speed knob: 8 is ~20% faster per frame and 4 ~30% faster. Quality-neutral (±0.01 dB) on coherent motion; on divergent motion 8 costs about −0.17 dB / +13.5% bits at QP 24. |
| `ProfileIdc` / `LevelIdc` | 66 / 0 (auto) | Signalled profile (baseline) and level. `LevelIdc = 0` auto-selects the lowest level whose MaxFS admits the frame, floored at 3.1; set explicitly to pin a level. |
| `ChromaDcRdLambda`, `Intra4x4SadLambda` | derived | Expert RD-lambda overrides; leave null. |

## Performance

Measured on Apple M5 Max (arm64, NEON/AdvSimd), .NET 10, BenchmarkDotNet, quiet machine — the
committed perf-gate baseline numbers (`perf/`).

| Benchmark | Mean | Min |
|---|---:|---:|
| SATD 4x4 kernel (`Satd4x4_Once`) | 11.2 ns | 11.1 ns |
| SATD 4x4 x 9 intra modes (`SatdMany4x4_9Modes`) | 99.3 ns | 99.2 ns |
| SAD 8x8 dispatch | 3.5 ns | 3.4 ns |
| SAD 16x16 dispatch (stride 720) | 6.4 ns | 6.4 ns |
| Full-MB 16x16 ME search, range 8 | 76.4 us | 76.1 us |
| Steady P-frame encode, 1280x720, 1 slice | 2.24 ms | 2.20 ms |

That 2.24 ms figure is measured on the committed benchmark's **near-static synthetic content**, and
it is not what a camera or a game scene costs. Textured, moving content is roughly an order of
magnitude more expensive per frame. On a deterministic scroll-plus-noise source, steady P-frames
measure:

| Resolution | 1 slice | 4 slices |
|---|---:|---:|
| 640x480 | 19.8 ms | 13.0 ms |
| 1280x720 | 26.9 ms | 15.6 ms |
| 1920x1080 | 32.8 ms | 18.8 ms |

Size your deployment from those numbers, not from the kernel microbenchmarks above. Note also that
slices do not divide the work cleanly — most of what remains after motion estimation (skip
evaluation, deblocking, CAVLC) does not parallelize, so 4 slices buys roughly 1.75x at 1080p rather
than 4x. Perf discipline is part of the repo:
`bench/Kiln.Benchmarks` (BenchmarkDotNet) plus `scripts/h264-simd-capture-baseline.sh` and
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

The `Adaptation` (resolution/fps ladders) and `Queue` (latest-frame dropping) namespaces ship inside
the library, fully tested but not yet wired into the encoder — **experimental**, and their APIs may
change or move without notice. See [docs/architecture.md](docs/architecture.md).

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
