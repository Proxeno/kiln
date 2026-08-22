#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BASELINE_PATH="${1:-perf/h264-simd-perf-baseline-latest.json}"
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

SAD_JSON="BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.H264SadKernelMicroBenchmarks-report-full-compressed.json"
ME_JSON="BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.H264MotionEstimatorBenchmarks-report-full-compressed.json"
PINTER_JSON="BenchmarkDotNet.Artifacts/results/Kiln.Benchmarks.H264PInterPhase2bAblationBenchmarks-report-full-compressed.json"

for f in "$SAD_JSON" "$ME_JSON" "$PINTER_JSON"; do
  if [[ ! -f "$f" ]]; then
    echo "missing expected benchmark artifact: $f" >&2
    exit 1
  fi
done

TMP_BENCH="$(mktemp)"
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

extract_rows "H264SadKernelMicroBenchmarks" "$SAD_JSON" >> "$TMP_BENCH"
extract_rows "H264MotionEstimatorBenchmarks" "$ME_JSON" >> "$TMP_BENCH"
extract_rows "H264PInterPhase2bAblationBenchmarks" "$PINTER_JSON" >> "$TMP_BENCH"

mkdir -p "$(dirname "$BASELINE_PATH")"

jq -n \
  --arg capturedAt "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" \
  --arg machine "$(uname -a)" \
  --slurpfile benches "$TMP_BENCH" \
  '{
    capturedAtUtc: $capturedAt,
    machine: $machine,
    benchmarks: $benches
  }' > "$BASELINE_PATH"

rm -f "$TMP_BENCH"
echo "wrote baseline: $BASELINE_PATH"
