# Performance gate

The performance gate guards Kiln's SIMD hot paths against silent slowdowns. It runs a fixed set of
BenchmarkDotNet benchmarks and compares each mean against a baseline committed in `perf/`.

Two scripts implement it, and they are the source of truth if this page drifts:

- [`scripts/h264-simd-capture-baseline.sh`](../scripts/h264-simd-capture-baseline.sh) records a
  baseline.
- [`scripts/h264-simd-perf-gate.sh`](../scripts/h264-simd-perf-gate.sh) compares a fresh run
  against it.

The gate is a local tool. CI does not run it.

## Running it

Both scripts need the .NET SDK, `jq` and `awk`. Run them from anywhere inside the repository, on a
quiet machine.

```bash
# Compare against the committed baseline.
scripts/h264-simd-perf-gate.sh [baseline-path]

# Record a new baseline.
scripts/h264-simd-capture-baseline.sh [baseline-path]

# Default baseline-path: perf/h264-simd-perf-baseline-latest.json
```

Exit codes from the gate:

| Code | Meaning |
|---|---|
| 0 | Passed. The last line printed is `perf gate passed`. |
| 1 | At least one benchmark regressed beyond its threshold. |
| 2 | The baseline file does not exist. |

The gate prints one line per benchmark with `baseline=`, `current=`, `delta_pct=`, `threshold=`,
`noise_pad_pct=` and `effective_threshold=`, so a failure shows which benchmark moved and by how
much.

## What is measured

All benchmarks live in [`bench/Kiln.Benchmarks`](../bench/Kiln.Benchmarks). Three suites are run,
each restricted to the methods listed:

| Suite | Methods | Parameters | Job |
|---|---|---|---|
| `H264SadKernelMicroBenchmarks` | `Satd4x4_Once`, `SatdMany4x4_9Modes` | `PreferHardwareIntrinsics` = true, false | 3 iterations, 1 warmup |
| `H264MotionEstimatorBenchmarks` | `Sad8x8_Dispatch`, `Sad16x16_Dispatch_Stride720`, `SearchMb16x16_SearchRange8` | none | 8 iterations, 3 warmups |
| `H264PInterPhase2bAblationBenchmarks` | `Encode_primmed_P` (a steady P picture at 1280×720, one slice) | `EnableIntraInPFallback` = true, false | 8 iterations, 3 warmups |

Every run uses BenchmarkDotNet's short job with the JSON exporter. The equivalent command for the
first suite is:

```bash
dotnet run -c Release --project bench/Kiln.Benchmarks -- \
  --filter "*H264SadKernelMicroBenchmarks.Satd4x4_Once*" "*H264SadKernelMicroBenchmarks.SatdMany4x4_9Modes*" \
  --job short --iterationCount 3 --warmupCount 1 --exporters json
```

The scripts then read
`BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.<Suite>-report-full-compressed.json` for each
suite. The capture script stops with an error if one is missing.

## How a row is judged

Each row of the current run is matched to the baseline by its exact suite, method and parameter
string. Then:

1. **Scalar rows are skipped.** A row whose parameters contain `PreferHardwareIntrinsics=False` is
   recorded for reference but not gated.
2. **A row with no baseline is skipped with a warning.** This is expected when a benchmark is
   added before the baseline is refreshed.
3. **The base threshold depends on the suite.** It is 3.0% for the two kernel suites
   (`SIMD_THRESHOLD_PCT`) and 2.0% for the end-to-end encode suite (`E2E_THRESHOLD_PCT`).
4. **Noise widens the threshold.** For each row the script takes the coefficient of variation
   (standard deviation ÷ mean, as a percentage) of the baseline and of the current run, uses the
   larger of the two, and caps it at 12.0% (`NOISE_PAD_CAP_PCT`). The result is added to the base
   threshold. A noisy run is therefore less likely to fail falsely, and the cap keeps a very noisy
   run from hiding a real regression.
5. **The row fails if it is slower by more than that.** The test is
   `(current_mean − baseline_mean) ÷ baseline_mean × 100 > effective_threshold`.

All three settings can be overridden through environment variables of the same name.

Both `EnableIntraInPFallback` rows of the encode suite are gated. The gate script also contains a
skip for rows whose parameters contain `DisablePhase2bManual=True`, but no current benchmark
produces that parameter, so the skip never applies.

A passing gate says nothing about correctness. For a new kernel or a new `IH264KernelSet`
implementation, get the SIMD and scalar parity tests green first. See
[SIMD kernel sets](architecture.md#simd-kernel-sets).

## Baseline file

`perf/h264-simd-perf-baseline-latest.json` has this shape:

```json
{
  "capturedAtUtc": "2026-01-01T00:00:00Z",
  "machine": "<uname -a output>",
  "benchmarks": [
    {
      "suite": "H264SadKernelMicroBenchmarks",
      "method": "Satd4x4_Once",
      "parameters": "PreferHardwareIntrinsics=True",
      "full_name": "...",
      "mean_ns": 12.34,
      "stddev_ns": 0.56
    }
  ]
}
```

`mean_ns` and `stddev_ns` are BenchmarkDotNet's `Statistics.Mean` and
`Statistics.StandardDeviation`.

Rules for the baseline:

- **It belongs to one machine.** Timings do not transfer between hosts or instruction sets. The
  committed baseline was captured on an arm64 Mac, so it gates the NEON path. To gate AVX2 or
  SSSE3, capture a separate baseline on an x64 host and pass its path to both scripts. The
  `machine` field records where a baseline came from. It is the output of `uname -a`, which
  includes the host name.
- **Refresh it only for an intended change.** When a deliberate algorithmic change moves the
  numbers, re-capture after the parity and quality tests pass, and say why in the commit that
  updates the file.
