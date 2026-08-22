# H.264 SIMD Perf Gate Runbook

This runbook defines the benchmark set and regression policy that guards
Kiln's SIMD hot paths (motion search, SAD/SATD kernels, and the P-inter
Phase 2b ablation) against silent slowdowns. It documents what
[`scripts/h264-simd-capture-baseline.sh`](../scripts/h264-simd-capture-baseline.sh)
and [`scripts/h264-simd-perf-gate.sh`](../scripts/h264-simd-perf-gate.sh)
actually do — read the scripts if this drifts, they are the source of truth.

## Required benchmark suites

All three live in [`bench/Kiln.Benchmarks`](../bench/Kiln.Benchmarks)
(BenchmarkDotNet):

1. `H264SadKernelMicroBenchmarks` — only `Satd4x4_Once` and
   `SatdMany4x4_9Modes` are gated.
2. `H264MotionEstimatorBenchmarks` — only `Sad8x8_Dispatch`,
   `Sad16x16_Dispatch_Stride720`, and `SearchMb16x16_SearchRange8` are gated.
3. `H264PInterPhase2bAblationBenchmarks` — only the `Encode_primmed_P*`
   benchmark family is gated (end-to-end encode, both Phase 2b on/off rows
   are captured but only the production-on row is gated — see below).

## Commands

Capture (or refresh) the committed baseline:

```bash
scripts/h264-simd-capture-baseline.sh [baseline-path]
# default baseline-path: perf/h264-simd-perf-baseline-latest.json
```

Run the gate against the committed baseline:

```bash
scripts/h264-simd-perf-gate.sh [baseline-path]
```

Both scripts `cd` to the repo root and shell out to `dotnet run -c Release
--project bench/Kiln.Benchmarks`, e.g. (abridged, see the scripts for exact
filters and job settings):

```bash
dotnet run -c Release --project bench/Kiln.Benchmarks -- \
  --filter "*H264SadKernelMicroBenchmarks.Satd4x4_Once*" "*H264SadKernelMicroBenchmarks.SatdMany4x4_9Modes*" \
  --job short --iterationCount 3 --warmupCount 1 --exporters json

dotnet run -c Release --project bench/Kiln.Benchmarks -- \
  --filter "*H264MotionEstimatorBenchmarks.Sad8x8_Dispatch*" "*H264MotionEstimatorBenchmarks.Sad16x16_Dispatch_Stride720*" "*H264MotionEstimatorBenchmarks.SearchMb16x16_SearchRange8*" \
  --job short --iterationCount 8 --warmupCount 3 --exporters json

dotnet run -c Release --project bench/Kiln.Benchmarks -- \
  --filter "*H264PInterPhase2bAblationBenchmarks.Encode_primmed_P*" \
  --job short --iterationCount 8 --warmupCount 3 --exporters json
```

Both scripts then read BenchmarkDotNet's compressed JSON report from
`BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.<SuiteClass>-report-full-compressed.json`
for each of the three suites and fail immediately (`h264-simd-perf-gate.sh`)
or (`h264-simd-capture-baseline.sh`) if any expected artifact is missing.

## Baseline format

`perf/h264-simd-perf-baseline-latest.json`:

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

Each row is extracted from BenchmarkDotNet's `.Benchmarks[]` array via `jq`
(`suite`, `method`, `parameters`, `full_name`, `Statistics.Mean` as
`mean_ns`, `Statistics.StandardDeviation` as `stddev_ns`).

## Gate policy (exactly what `h264-simd-perf-gate.sh` implements)

For every current-run row, matched against the baseline by exact
`(suite, method, parameters)`:

1. **Skip scalar rows** — any row whose `parameters` contains
   `PreferHardwareIntrinsics=False` is skipped. The SIMD gate is about SIMD
   paths; scalar timings are captured for visibility only.
2. **Skip ablation-off rows** — any row whose `parameters` contains
   `DisablePhase2bManual=True` is skipped. Both Phase 2b on/off rows are
   benchmarked for visibility, but the gate only cares about the
   production-on path.
3. **No baseline for a row** — warn and skip (does not fail the gate). This
   is expected when a new benchmark method is added before the baseline is
   refreshed.
4. **Threshold** — `SIMD_THRESHOLD_PCT` (env override, default `3.0`) for
   `H264SadKernelMicroBenchmarks` and `H264MotionEstimatorBenchmarks`;
   `E2E_THRESHOLD_PCT` (env override, default `2.0`) for
   `H264PInterPhase2bAblationBenchmarks` (end-to-end encode rows get a
   tighter budget than isolated kernel micro-benchmarks).
5. **Noise padding** — `noise_pad_pct = min(max(baseline_cv, current_cv), NOISE_PAD_CAP_PCT)`
   where `cv = stddev/mean * 100` and `NOISE_PAD_CAP_PCT` defaults to `12.0`
   (env override). This is added to the base threshold to get
   `effective_threshold`, so a noisy host doesn't manufacture false-positive
   regressions, capped so a truly noisy run still fails on a real
   regression.
6. **Regression test** — `delta_pct = (current_mean - baseline_mean) / baseline_mean * 100`.
   If `delta_pct > effective_threshold`, that row is a `FAIL`.
7. The script exits `1` if `fail_count > 0` across all rows, `2` if the
   baseline file doesn't exist, `0` (with `perf gate passed`) otherwise.
   Every row's comparison (`baseline=`, `current=`, `delta_pct=`,
   `threshold=`, `noise_pad_pct=`, `effective_threshold=`) is printed, so a
   failing CI run shows exactly which benchmark regressed and by how much.

For new kernels (a new `IH264KernelSet` implementation or a new dispatch
path), get the SIMD/scalar parity tests green first (see
[docs/architecture.md](architecture.md#simd-kernel-structure)) — the perf
gate only tells you about speed, not correctness.

## Baseline handling

- Baselines are host/ISA-specific in practice: capture separately on an x64
  host (AVX2/SSSE3 path) and an arm64 host (NEON path) if you maintain gates
  for both; `perf/h264-simd-perf-baseline-latest.json` reflects whichever
  host last ran the capture script (`machine` field records `uname -a`).
- Refresh the baseline only when an intentional algorithmic change moves the
  numbers — after parity tests and any relevant golden/quality tests pass —
  and note why in the commit/PR that updates
  `perf/h264-simd-perf-baseline-latest.json`.
- Keep CI-run benchmark JSON artifacts (the `BenchmarkDotNet.Artifacts/results/*.json`
  files) attached to the CI run for any gated build, so a regression can be
  diagnosed after the fact without re-running the benchmark.
