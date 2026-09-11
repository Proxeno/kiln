# Scalar residue — where Kiln's vectorized paths fall back, and what each is worth

This is a measured inventory, not a fix list. It answers two questions that look
answerable by reading the code and are not:

1. **Where can a vectorized path fall back to scalar, and where is there no vector
   path at all?**
2. **What does each of those places cost in encoder wall?**

Question 2 is the one that matters, because a scalar branch that runs a million times
in a cheap loop and one that runs a thousand times on the critical path look identical
in a grep.

## Verdict

**The wall is not in the scalar residue.** The vectorized kernel set accounts for
**~67% of encoder wall**, and that ~67% is owned almost entirely by two families whose
vectorization is already **complete** — luma (qpel) interpolation at ~46% and the motion
SADs at ~45%. Neither has a fallback site on its hot path. Every other family measures
at or below the harness's own noise floor.

**Read the per-family numbers as lower bounds, not ceilings.** A one-family-at-a-time
ablation cannot see work that the four slices' load imbalance absorbs — 27.4% of the
fully-scalar excess is invisible to it (§ *What the per-family numbers cannot see*).
That does not change the ranking: `sad-motion` and `interp-luma` clear the slack by a
wide margin and everything else hides inside it. What it does mean is that a family
reading "at the floor" has been shown to be *smaller than the imbalance it sits in*, not
shown to be free — so this document does not put a number on the total residue.

## Method

### The instrument

`H264ScalarAblation` (`src/Kiln/Internal/H264/H264ScalarAblation.cs`) wraps the resolved
`IH264KernelSet` and routes one named family to the corresponding `ScalarKernelSet`
method. Because every kernel has a bit-identical scalar twin, an arm changes speed and
nothing else — the probe prints `bytesPerFrame` per arm and it is identical across every
arm in every round.

Driven by `bench/Kiln.Benchmarks`:

```bash
# whole-layer: shipped vs ScalarKernelSet vs the intrinsics preference, interleaved
dotnet run -c Release --project bench/Kiln.Benchmarks -- --scalar-residue 1920 1080 10 5

# one family per process (the ablation is per-process, via the environment)
KILN_H264_SCALAR_ABLATE=interp-luma dotnet … --scalar-residue single 1920 1080 10 5
```

`KILN_H264_SCALAR_ABLATE` is unset by default, and **unset means the wrapper is not in
the dispatch path at all** — `H264KernelSet.CreateBest()` returns what it always
returned. That is verifiable from the probe's own `tier=` field: `Neon64KernelSet`
unset, `AblatingKernelSet` when set.

### The denominator

Total encoder wall for a **1920×1080 P-frame encode, `SliceCount=4`, QP 28,
`UseMotionSatd=true`, `MaxRefFrames=2`** — Kiln's `default-s4` settings. 14.06 ms/frame
on the host below. Every share in this document is a fraction of that number, measured
as `1 − control/arm` on the same box in the same sitting.

Where that wall sits, from Kiln's own `--perf-probe phases` (same settings, 150 frames):

| phase | ms/frame | share |
|---|---:|---:|
| `parallelWall` (slice encoding) | 13.704 | **97.4%** |
| `padRotate` | 0.168 | 1.2% |
| `beginFrame` | 0.013 | 0.1% |
| `nalGather` | 0.007 | 0.0% |
| of which `sliceDeblockSum` | 1.268 (summed over 4 slices) | 2.3% |

and from `--perf-probe mb` (P-inter Phase 2, 104,420 MB-timings):

| Phase 2 stage | share |
|---|---:|
| **ME (motion estimation)** | **88.1%** |
| chroma | 5.2% |
| luma | 3.5% |
| other | 2.6% |
| write | 0.6% |
| pred | 0.0% |

## Controls

A number from a contended box is not a measurement, so the packet carries three
controls and reports all of them.

| control | reading | meaning |
|---|---|---|
| **A/A** — shipped configuration as two arms | 1.037 | the floor under contention; anything under ~4% is not a result |
| **null arm** `gather-chroma` | 0.985 | `GatherChroma8x8` has no vectorized variant in the repo — the two kernel sets call the identical method — so this arm *must* measure zero. It does. |
| **decorator control** `ablate=none` | 1.000 | prices the wrapper itself, so arms are read against a control carrying the same dispatch layer |

The quietest sitting (the sub-family sweep, 8 processes/family) put the null arm at
0.985 and per-arm CV at 6.5–7.8%. **All ratios below are `med-of-min`** — the median
across 8 independent processes of each process's fastest of 5 internal rounds. Plain
medians were discarded: they tracked the box's dips rather than the code.

The probe warms up for 30 frames before timing. That is load-bearing, not politeness —
the first version primed with four frames and produced 11.6–50 ms spreads on identical
work. The spread was RyuJIT tier-up, not the codec.

## The measurement

### Whole layer

8 processes per arm, interleaved, order reversed on alternate rounds, 1920×1080:

| arm | med-of-min ms/frame | vs shipped |
|---|---:|---:|
| shipped | 13.596 | 1.000 |
| **`noHardwareKernels`** (`PreferHardwareIntrinsics=false`) | 41.895 | **3.081** |
| `allScalar` (that, plus `PreferIntrinsics=false`) | 42.007 | 3.090 |
| `noIntrinsics` alone (`PreferIntrinsics=false`) | 15.626 | 1.149 |

**Kiln's vectorized kernel set owns ~67.5% of encoder wall** (`1 − 13.596/41.895`).

The `PreferIntrinsics` axis is a separate and much smaller question, and this run does
not resolve it: it read 0.97 in an earlier sitting and 1.149 here, against an A/A floor
of 4%. What is clear is that it is worth **nothing once the kernel set is scalar**
(`allScalar` 42.007 vs `noHardwareKernels` 41.895 — equal), which is what the code says
too: of the eleven `H264IntrinsicsPreference.Use*` flags, six are dead and the five that
are honoured gate legacy static entry points rather than the resolved `IH264KernelSet`
tier that the shipped path actually calls.

### Per family

8 processes per family, same sitting:

| family | members | med-of-min ms/frame | ratio | **wall owned** | verdict |
|---|---|---:|---:|---:|---|
| `interp-luma` | `InterpolateLuma` | 22.884 | 1.855 | **46.1%** | fully vectorized; no residue |
| `sad-motion` | `Sad16x16/16x8/8x16/8x8` | 22.488 | 1.823 | **45.2%** | fully vectorized; no residue |
| `satd` | `Satd16x16/16x8/8x16/8x8/Many4x4` | 12.618 | 1.023 | ~2.3% | one real ARM residue (below) |
| `interp-chroma` | `InterpolateChroma` | 12.619 | 1.023 | ~2.3% | several residues (below) |
| `sad-chroma` | `SadChromaPair` | 12.229 | 0.991 | — | ≤1.02 |
| `sad-intra` | `SadIntra16x16`, `SadMany4x4` | 12.134 | 0.984 | — | ≤1.02 |
| `gather-chroma` | `GatherChroma8x8` | 12.154 | 0.985 | — | **null arm** |
| *(control)* | `ablate=none` | 12.334 | 1.000 | — | |

The first sitting on the same instrument measured the remaining families. On
`med-of-min` every one of them landed between 0.99 and 1.02 — inside the floor. For
scale, the same sitting's `min-of-min` ratios, which are the deliberately pessimistic
statistic (the null arm read 0.921 under it, so it overstates every family):
`ssd` ≤3.2%, `deblock` ≤1.7%, `residual` ≤0.7%, `quant` ≤0.9%, `dct` ≤0.6%,
`intrapred` ≤0.1%, `variance` ≤0%, `gather-luma` ≤0%. Those are the largest numbers any
reading produces for these families, and they are still the size of the imbalance
described below.

**The two families that own the wall are the two with nothing to fix.** Their ratios are
not a residue measurement — they are the value of a vectorization that is already
complete at every call shape the encoder actually produces.

### What the per-family numbers cannot see — read them as lower bounds

The per-family figures do not add up to the whole-layer figure, and the gap is a real
limit on what a one-family-at-a-time ablation can measure:

| | ms/frame above control |
|---|---:|
| whole layer, everything scalar (`noHardwareKernels`) | **29.56** |
| sum of the positive per-family excesses | 21.45 |
| **invisible to any single-family ablation** | **8.11 (27.4%)** |

The mechanism is **slice load imbalance**. A frame's wall is gated by its slowest slice,
and the four slices are not equal — `--perf-probe phases` reports `perSlice:
9.34 / 9.87 / 11.67 / 11.80` against `sliceMax=13.614` of a `sliceSum=42.679`. Scalarising
one family adds work across all four slices, and the slices with spare capacity absorb
it until they catch up to the slowest one. Only when the added work exceeds that slack
does it reach the wall.

So a family that measures at the floor has not been shown to be free; it has been shown
to be **smaller than the imbalance it hides inside**. `sad-motion` and `interp-luma`
clear that slack by a wide margin, which is why they dominate; everything else is
measured through it. The ranking is sound — those two are far larger than the rest
under either reading — but the absolute smallness of the rest is a floor, not a ceiling.

## The ranked inventory

Ordered by the family each site sits in, largest first. The last column is that
family's **measured** wall delta — a lower bound on what its vectorization currently
saves, and therefore an upper bound on nothing. No per-site number exists here and none
is claimed: isolating one site requires changing it, which would be a fix packet.

| # | site | why it is scalar | reachable on | family delta |
|---|---|---|---|---:|
| 1 | `H264BilinearChromaInterp.InterpolateSimd` — `if (!SimdWindowFits(...))` → scalar `Interpolate` | **declined at runtime**: wide loads touch up to 5 bytes/row, so near the right/bottom edge of the padded plane every chroma block drops to scalar | all tiers | 1.023 |
| 2 | `H264MotionSatd.Diff4x4StridedSimd` — the `AdvSimd.IsSupported` branch | **no vector path written**: the branch body is a plain `for row / for col` scalar loop under a `Simd` name | **arm64 only** (x64 takes the vectorized `Sse41` branch) | 1.023 |
| 3 | `H264BilinearChromaInterp.InterpolateSimd` — ARM per-element store (`else` of `if (Sse2.IsSupported)`) | **no vector path written**: four scalar byte stores per vector iteration | arm64 only | 1.023 |
| 4 | `H264BilinearChromaInterp.InterpolateSimd` — `if (xFrac == 0 && yFrac == 0)` integer-pel shortcut | **declined by shape**: a per-row `CopyTo`, not the interpolator | all tiers | 1.023 |
| 5 | `H264Intra4X4DirectionalSimd` — modes 3 and 7 bodies | **no vector path written**: scalar loops under `Simd` names | all tiers | ≤1.02 |
| 6 | `H264DeblockingFilter` — `if (!(anyNormal && anyStrong))` → scalar per-segment `FilterLumaEdgeOne` | **declined at runtime**: the SIMD path fires only when a macroblock's luma edges mix weak (bs 1–3) and strong (bs 4) values | all tiers | ≤1.02 |
| 7 | `H264DeblockingFilter` — chroma deblocking (`FilterChromaEdgeOne`) | **no vector path written**: chroma is scalar in every tier | all tiers | ≤1.02 |
| 8 | `H264SrcGather.GatherChroma8x8` | **no vector path exists anywhere**: both kernel sets call the same scalar loop | all tiers | ≤1.02 |
| 9 | `H264Intra4X4DirectionalSimd` / `FillSimd` — `!topAvail` / `!leftAvail` boundary declines | **declined by shape**: fall to the scalar DC predictor | boundary MBs only | ≤1.02 |
| 10 | `H264TransformBundle` — `int.MinValue` sentinel guards (`EncodeResidual4x4Simd`, `DequantIdct4x4Simd`) | **declined at runtime**: dequant-escape sentinel only | all tiers | ≤1.02 |
| 11 | `H264QpelLumaInterp` — `for (; j < bw; j++)` scalar tails | **declined by shape**: widths not a multiple of 8 | unreachable — the qpel call mix is 8- and 16-wide only | ≤1.02 |

## Cross-ISA reachability

Many plausible-looking scalar sites are not reachable on any ISA a real part selects,
and reporting them as gaps would be wrong:

- **`NeonKernelSet.SadMany4x4` → `SadManyAdvSimd` is a fully scalar loop** despite the
  name. It is **unreachable on arm64 hardware**: `Neon64KernelSet` overrides
  `SadMany4x4` to the vectorized `SadManyAdvSimdArm64`, and the ISA ladder tests
  `AdvSimd.Arm64` before `AdvSimd`. It is reachable only on 32-bit ARM.
- **`Diff4x4StridedSimd`'s scalar `AdvSimd` branch is arm64-only** (x64 takes `Sse41`),
  so the SATD finding above does not transfer to x64 — measured here on arm64.
- The **`Satd16x16NeonScalarReduce` / `ReduceUshortAccumulator` scalar lane-sums** need
  `AdvSimd` without `AdvSimd.Arm64` — not a modern part of any tier in scope.
- The **six dead `H264IntrinsicsPreference` flags** (`UseIntraSadSimd`, `UseDequantSimd`,
  `UseIntraPredictSimd`, `UseI16x16SadSimd`, `UseMbKernelSimd`,
  `UseTransformBundleSimd`) have zero consult sites. They are documentation that
  describes a dispatch that does not exist; a reader pricing a SIMD/scalar trade from
  them is reading a lever that is not connected.

## What this does not claim

- **Not that the residue is worth fixing, and not that it is negligible either.** No
  total is claimed: the per-family method can only bound each family from below, and
  27.4% of the fully-scalar excess is invisible to it. What the packet does support is
  the *ranking* — nothing in the residue is within an order of magnitude of the two
  families that carry the encoder. Whether the rest repays a change is a product call,
  not a measurement.
- **Not that these two families need work.** They are the opposite: they are what a
  complete vectorization looks like on this workload, and they are why the encoder is
  fast.
- **Nothing about x64 or Intel/AMD parts.** Measured on arm64 (Apple M4) only; no quiet
  x64 host was available. The tier-reachability notes above are static reading, not
  measurement.
- **Not that the code is free of scalar work.** Motion estimation is 88.1% of the
  P-inter phase and is where the encoder spends its time; it is also fully vectorized.
  A reader looking for "the scalar part" should not conclude the encoder is mostly
  scalar — it is not.
- **Nothing about VideoToolbox or any other encoder.** This is kiln-scalar vs
  kiln-vector, a within-implementation question. It says nothing about kiln vs anything
  else, and in particular it does not contradict any comparison between two vectorized
  implementations — the gap measured here, between a filter and its own scalar
  reference, is a much larger quantity than the gap between two vector implementations
  of the same filter.

## Reproducing

Host: `MacMini.local` — Apple M4, 10 cores, macOS, .NET SDK 10.0.400, kernel set
`Neon64KernelSet`, 1920×1080, `default-s4`, 8 processes per arm, 5 internal rounds each.

```bash
dotnet build -c Release bench/Kiln.Benchmarks
B=bench/Kiln.Benchmarks/bin/Release/net10.0/Kiln.Benchmarks.dll

# whole layer + A/A, interleaved
for i in $(seq 1 8); do dotnet $B --scalar-residue 1920 1080 10 5; done

# one family per process
for a in unset none gather-chroma satd sad-motion interp-luma interp-chroma; do
  [ "$a" = unset ] && env -u KILN_H264_SCALAR_ABLATE dotnet $B --scalar-residue single 1920 1080 10 5 \
                   || KILN_H264_SCALAR_ABLATE=$a dotnet $B --scalar-residue single 1920 1080 10 5
done
```

`--scalar-residue --verify-flow` is the negative control for the `PreferIntrinsics`
arm: that flag is an `AsyncLocal` read on `Parallel.For` workers, so an ablation that
silently declined to apply would look exactly like a path that costs nothing. It
prints `flow=OK` / `flow=BROKEN`.
