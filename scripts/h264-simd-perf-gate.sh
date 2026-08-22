#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BASELINE_PATH="${1:-perf/h264-simd-perf-baseline-latest.json}"
if [[ ! -f "$BASELINE_PATH" ]]; then
  echo "baseline file not found: $BASELINE_PATH" >&2
  exit 2
fi

SIMD_THRESHOLD_PCT="${SIMD_THRESHOLD_PCT:-3.0}"
E2E_THRESHOLD_PCT="${E2E_THRESHOLD_PCT:-2.0}"
NOISE_PAD_CAP_PCT="${NOISE_PAD_CAP_PCT:-12.0}"
JOB_ARGS=(--job short --iterationCount 3 --warmupCount 1 --exporters json)
ME_JOB_ARGS=(--job short --iterationCount 8 --warmupCount 3 --exporters json)
PINTER_JOB_ARGS=(--job short --iterationCount 8 --warmupCount 3 --exporters json)

run_suite() {
  local filters=("$@")
  dotnet run -c Release --project bench/Kiln.Benchmarks -- --filter "${filters[@]}" "${JOB_ARGS[@]}"
}

run_pinter_suite() {
  dotnet run -c Release --project bench/Kiln.Benchmarks -- --filter "*H264PInterPhase2bAblationBenchmarks.Encode_primmed_P*" "${PINTER_JOB_ARGS[@]}"
}

run_me_suite() {
  dotnet run -c Release --project bench/Kiln.Benchmarks -- --filter "*H264MotionEstimatorBenchmarks.Sad8x8_Dispatch*" "*H264MotionEstimatorBenchmarks.Sad16x16_Dispatch_Stride720*" "*H264MotionEstimatorBenchmarks.SearchMb16x16_SearchRange8*" "${ME_JOB_ARGS[@]}"
}

run_suite "*H264SadKernelMicroBenchmarks.Satd4x4_Once*" "*H264SadKernelMicroBenchmarks.SatdMany4x4_9Modes*"
run_me_suite
run_pinter_suite

CURRENT_TMP="$(mktemp)"
extract_rows() {
  local suite="$1"
  local json="$2"
  jq -c --arg suite "$suite" '.Benchmarks[] | {
    suite:$suite,
    method:.Method,
    parameters:(.Parameters // ""),
    full_name:(.FullName // ""),
    mean_ns:.Statistics.Mean,
    stddev_ns:(.Statistics.StandardDeviation // 0)
  }' "$json"
}

extract_rows "H264SadKernelMicroBenchmarks" "BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.H264SadKernelMicroBenchmarks-report-full-compressed.json" >> "$CURRENT_TMP"
extract_rows "H264MotionEstimatorBenchmarks" "BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.H264MotionEstimatorBenchmarks-report-full-compressed.json" >> "$CURRENT_TMP"
extract_rows "H264PInterPhase2bAblationBenchmarks" "BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.H264PInterPhase2bAblationBenchmarks-report-full-compressed.json" >> "$CURRENT_TMP"

fail_count=0
while IFS= read -r row; do
  suite="$(jq -r '.suite' <<<"$row")"
  method="$(jq -r '.method' <<<"$row")"
  parameters="$(jq -r '.parameters' <<<"$row")"
  full_name="$(jq -r '.full_name' <<<"$row")"
  current_mean="$(jq -r '.mean_ns' <<<"$row")"
  current_stddev="$(jq -r '.stddev_ns' <<<"$row")"

  # SIMD perf gate excludes scalar-parameterized rows by design.
  if [[ "$parameters" == *"PreferHardwareIntrinsics=False"* ]]; then
    echo "SKIP scalar row $suite::$method [$parameters]"
    continue
  fi

  # P-inter phase2b ablation keeps both rows for visibility, but gating targets the production-on path.
  if [[ "$parameters" == *"DisablePhase2bManual=True"* ]]; then
    echo "SKIP ablation-off row $suite::$method [$parameters]"
    continue
  fi

  baseline_mean="$(jq -r --arg s "$suite" --arg m "$method" --arg p "$parameters" '.benchmarks[] | select(.suite == $s and .method == $m and (.parameters // "") == $p) | .mean_ns' "$BASELINE_PATH" | head -n 1)"
  baseline_stddev="$(jq -r --arg s "$suite" --arg m "$method" --arg p "$parameters" '.benchmarks[] | select(.suite == $s and .method == $m and (.parameters // "") == $p) | (.stddev_ns // 0)' "$BASELINE_PATH" | head -n 1)"

  if [[ -z "$baseline_mean" ]]; then
    echo "WARN no baseline for $suite::$method [$parameters]; skipping gate"
    continue
  fi

  threshold="$SIMD_THRESHOLD_PCT"
  if [[ "$suite" == "H264PInterPhase2bAblationBenchmarks" ]]; then
    threshold="$E2E_THRESHOLD_PCT"
  fi

  baseline_noise_pct="$(awk -v sd="$baseline_stddev" -v mean="$baseline_mean" 'BEGIN { if (mean <= 0) { print 0 } else { printf "%.4f", (sd/mean)*100.0 } }')"
  current_noise_pct="$(awk -v sd="$current_stddev" -v mean="$current_mean" 'BEGIN { if (mean <= 0) { print 0 } else { printf "%.4f", (sd/mean)*100.0 } }')"
  noise_pad_pct="$(awk -v b="$baseline_noise_pct" -v c="$current_noise_pct" -v cap="$NOISE_PAD_CAP_PCT" 'BEGIN { n = (b > c) ? b : c; if (n > cap) n = cap; printf "%.4f", n }')"
  effective_threshold="$(awk -v base="$threshold" -v pad="$noise_pad_pct" 'BEGIN { printf "%.4f", base + pad }')"

  delta_pct="$(awk -v c="$current_mean" -v b="$baseline_mean" 'BEGIN { printf "%.4f", ((c-b)/b)*100.0 }')"
  echo "$suite::$method [$parameters] baseline=$baseline_mean current=$current_mean delta_pct=$delta_pct threshold=$threshold noise_pad_pct=$noise_pad_pct effective_threshold=$effective_threshold"

  is_regress="$(awk -v d="$delta_pct" -v t="$effective_threshold" 'BEGIN { print (d > t) ? 1 : 0 }')"
  if [[ "$is_regress" == "1" ]]; then
    echo "FAIL regression above threshold for $suite::$method [$parameters] ($full_name)"
    fail_count=$((fail_count + 1))
  fi
done < "$CURRENT_TMP"

rm -f "$CURRENT_TMP"

if [[ "$fail_count" -gt 0 ]]; then
  echo "perf gate failed: regressions=$fail_count"
  exit 1
fi

echo "perf gate passed"
