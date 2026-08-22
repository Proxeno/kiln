![Kiln](https://raw.githubusercontent.com/Proxeno/kiln/main/docs/assets/hero.png)

# Kiln

A pure-managed, SIMD-accelerated **H.264 baseline-profile encoder** for .NET, built for real-time game streaming.

[![CI](https://github.com/Proxeno/kiln/actions/workflows/ci.yml/badge.svg)](https://github.com/Proxeno/kiln/actions/workflows/ci.yml)
[![NuGet version](https://img.shields.io/nuget/v/Proxeno.Kiln)](https://www.nuget.org/packages/Proxeno.Kiln)
[![NuGet downloads](https://img.shields.io/nuget/dt/Proxeno.Kiln)](https://www.nuget.org/packages/Proxeno.Kiln)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-e8912d)](https://github.com/Proxeno/kiln/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-e8912d)](https://dotnet.microsoft.com)

---

Kiln is a pure-managed **H.264 encoder for .NET / C#** with **no native dependencies** —
the entire codec is C# on .NET 10 using hardware intrinsics: NEON/AdvSimd on arm64, AVX2
and SSSE3 on x64, with a scalar fallback everywhere else. It targets **real-time**,
low-latency use — game streaming, screen capture, and **WebRTC**/RTP pipelines — where a
managed, dependency-free encoder beats bundling a native codec. Kernel selection happens
at runtime; every SIMD path is covered by parity tests against the scalar reference, and
CI runs the full suite on Linux, Windows and macOS so both architectures stay green.

## Spec-cited and clean-licensed

Kiln is original work, written against the ITU-T H.264 (ISO/IEC 14496-10)
specification. Every numeric table that originates in the spec carries its
clause/table citation in the source (`Table 9-4`, `§8.5.9`, `§9.2.1`, …). It has
no codec dependencies and links no copyleft code, so it embeds cleanly under
**Apache-2.0** in commercial products where GPL/LGPL codec linkage is a problem —
which is exactly why it exists.

## What it is

- **Real-time first**: constrained baseline profile, IDR + P-frames, CAVLC, Annex B
  output that streams straight into WebRTC or an RTP packetizer. Predictable
  per-frame latency over exhaustive search.
- **A real encoder**: Intra_4x4 / Intra_16x16 with RD mode selection, P-slice inter
  prediction with sub-pel SATD motion search, P_Skip, multi-slice frames (parallel
  slice encoding), multiple reference frames, in-loop deblocking, optional greedy
  trellis quantization, spatial adaptive QP, per-frame rate control.
- **Operations-grade**: `Kiln.RateControl` (low-latency rate controller with network
  feedback) and `Kiln.Recovery` (IDR budgeting / keyframe recovery policy) are public
  companion namespaces, production-used in a streaming server.
- **Verified**: 2,174 tests — spec-roundtrip decoding, SIMD/scalar parity on NEON and
  AVX2/SSSE3, golden-frame regression, PSNR fidelity floors, adversarial
  neighbour-availability sweeps, and smoke tests that decode every produced stream
  with an independent reference decoder as an oracle.

## What it is not

Not an x264 competitor. No B-frames, no CABAC, no 8x8 transform, no interlace;
4:2:0 8-bit only; dimensions must be multiples of 16. If you need maximum
compression at any CPU cost, use a full-profile encoder. If you need clean-licensed,
dependency-free, low-latency encoding inside a .NET process, you are in the right
place.

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
| `SubPartitionRangeCap` | 16 | Sub-partition ME radius cap (per-frame complexity budget applies). |
| `ProfileIdc` / `LevelIdc` | 66 / 0x1F | Signalled profile (baseline) and level. |
| `ChromaDcRdLambda`, `Intra4x4SadLambda` | derived | Expert RD-lambda overrides; leave null. |

## Performance

Measured on Apple M5 Max (arm64, NEON/AdvSimd), .NET 10, BenchmarkDotNet, quiet
machine — these are the committed perf-gate baseline numbers (`perf/`):

| Benchmark | Mean | Min |
|---|---:|---:|
| SATD 4x4 kernel (`Satd4x4_Once`) | 11.2 ns | 11.1 ns |
| SATD 4x4 x 9 intra modes (`SatdMany4x4_9Modes`) | 99.3 ns | 99.2 ns |
| SAD 8x8 dispatch | 3.5 ns | 3.4 ns |
| SAD 16x16 dispatch (stride 720) | 6.4 ns | 6.4 ns |
| Full-MB 16x16 ME search, range 8 | 76.4 us | 76.1 us |
| Steady P-frame encode, 1280x720, 1 slice | 2.24 ms | 2.20 ms |

A ~2.2 ms steady-state P-frame at 720p on one slice leaves comfortable headroom
for 60 fps game streaming; `SliceCount > 1` parallelizes further. During
extraction the motion-estimation path also got a measured targeted win: two
structurally dead caches (0.0% hit rate under production instrumentation) were
removed, making the textured-content sub-partition search ~5% faster (paired
A/B, median of 6 rounds, faster in 6/6) with bit-identical output.

Perf discipline is part of the repo: `bench/Kiln.Benchmarks` (BenchmarkDotNet),
`scripts/h264-simd-capture-baseline.sh` and `scripts/h264-simd-perf-gate.sh` gate
changes against the committed baseline in `perf/`. See
[docs/perf-gate.md](docs/perf-gate.md).

## Experimental subsystems

`Adaptation` (resolution/fps ladders) and `Queue` (latest-frame dropping) ship inside
the library, fully tested but not wired into the encoder — **experimental**, APIs may
change or move without notice. See [docs/architecture.md](docs/architecture.md).

## Installing

Published on [nuget.org](https://www.nuget.org/packages/Proxeno.Kiln) as `Proxeno.Kiln`.
The package id is `Proxeno.Kiln`; the assembly and namespace stay `Kiln`, so code uses
`using Kiln;`.

```
dotnet add package Proxeno.Kiln
```

## Documentation

- [docs/architecture.md](docs/architecture.md) — pipeline stages, SIMD kernel structure, subsystems
- [docs/perf-gate.md](docs/perf-gate.md) — benchmark baseline + regression gate workflow
- [CONTRIBUTING.md](CONTRIBUTING.md) — build, test, and contribution rules
- [SECURITY.md](SECURITY.md) — reporting vulnerabilities

## License

Apache-2.0 — see [LICENSE](LICENSE). Copyright the Kiln contributors.
