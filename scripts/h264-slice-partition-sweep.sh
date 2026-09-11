#!/usr/bin/env bash
#
# Price slice tilings against the live cost-balanced partitioner.
#
# The frame wall is the SLOWEST slice, so which MB rows each slice covers is a first-order
# decision. `KILN_H264_SLICE_ROWS` pins the tiling instead of letting the partitioner choose,
# and this script drives that knob as a sitting: every arm gets its OWN PROCESS, because the
# knob is read once at type init (like the other KILN_H264_* gates), so one process cannot
# carry two arms.
#
# Protocol, matching the perf-gate convention: arms interleave round by round, the visit order
# reverses on alternate rounds, and `live` is listed twice (a second tag over the same config)
# so the run reports its own A/A floor. A result inside that floor is not a result.
#
# Usage:
#   scripts/h264-slice-partition-sweep.sh [--frames N] [--rounds N] [--w W] [--h H]
#                                         [--arm TAG:ROWROWS]... [--outdir DIR]
#
# --arm ROWS is a comma-separated list of MB row boundaries for the tiling: strictly
# increasing, first 0, last = coded MB height (ceil(h/16)). Omit ROWS (or pass "TAG:") to run
# the live partitioner. Defaults are the 1080p arms from the slice-imbalance investigation:
#
#   live:                 the cost-balanced partitioner (equal-height on frame 0, then balanced)
#   live2:                the same config again -- the A/A control
#   eq:0,17,34,51,68      equal-height, i.e. the balancer disabled
#   opt:0,31,34,37,68     the tiling the per-row wall-time DP picks (fitted to this content)
#
# ROWS values are geometry-specific: a tiling whose length is not sliceCount+1, or whose last
# boundary is not the coded height, is ignored by the encoder and falls back to the partitioner.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

FRAMES="${FRAMES:-200}"
ROUNDS="${ROUNDS:-8}"
SLICES="${SLICES:-4}"
W="${W:-1920}"
H="${H:-1080}"
OUTDIR="${OUTDIR:-artifacts/slice-partition-sweep}"

ARMS=(
  "live:"
  "live2:"
  "eq:0,17,34,51,68"
  "opt:0,31,34,37,68"
)
ARMS_ARE_DEFAULT=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --frames) FRAMES="$2"; shift 2 ;;
    --rounds) ROUNDS="$2"; shift 2 ;;
    --slices) SLICES="$2"; shift 2 ;;
    --w) W="$2"; shift 2 ;;
    --h) H="$2"; shift 2 ;;
    --outdir) OUTDIR="$2"; shift 2 ;;
    --arm)
      if [[ "$ARMS_ARE_DEFAULT" == "1" ]]; then ARMS=(); ARMS_ARE_DEFAULT=0; fi
      ARMS+=("$2"); shift 2 ;;
    -h|--help) sed -n '2,32p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

echo "building (one build; every arm then runs the same binary)" >&2
dotnet build -c Release --nologo -v q bench/Kiln.Benchmarks/Kiln.Benchmarks.csproj >/dev/null

BENCH="bench/Kiln.Benchmarks/bin/Release/net10.0/Kiln.Benchmarks.dll"
[[ -f "$BENCH" ]] || { echo "build produced no $BENCH" >&2; exit 2; }

mkdir -p "$OUTDIR"
echo "arms=${#ARMS[@]} rounds=$ROUNDS frames=$FRAMES slices=$SLICES ${W}x${H} out=$OUTDIR" >&2

run_one() { # $1 tag, $2 rows (empty = live), $3 round
  local tag="$1" rows="$2" round="$3" f="$OUTDIR/${tag}-r${round}.txt"
  if [[ -n "$rows" ]]; then
    KILN_H264_SLICE_ROWS="$rows" dotnet "$BENCH" --perf-probe phases default "$SLICES" "$FRAMES" >"$f" 2>&1
  else
    dotnet "$BENCH" --perf-probe phases default "$SLICES" "$FRAMES" >"$f" 2>&1
  fi
  grep -q "perSlice" "$f" || { echo "arm $tag round $round produced no perSlice line ($f)" >&2; return 1; }
}

for round in $(seq 1 "$ROUNDS"); do
  echo "-- round $round load=$(uptime | sed 's/.*averages: //')" >&2
  order=()
  for i in "${!ARMS[@]}"; do order+=("$i"); done
  if [[ $((round % 2)) -eq 0 ]]; then
    rev=(); for ((i=${#order[@]}-1; i>=0; i--)); do rev+=("${order[i]}"); done; order=("${rev[@]}")
  fi
  for i in "${order[@]}"; do
    a="${ARMS[$i]}"
    run_one "${a%%:*}" "${a#*:}" "$round"
  done
done

python3 - "$OUTDIR" <<'PY'
import glob, os, re, statistics as st, sys
from collections import defaultdict

outdir = sys.argv[1]
arms = defaultdict(list)
part = {}
for path in sorted(glob.glob(os.path.join(outdir, "*-r*.txt"))):
    tag = re.match(r"(.+)-r\d+\.txt$", os.path.basename(path)).group(1)
    text = open(path).read()
    wall = re.search(r"wall=([\d.]+) ms/frame", text)
    if not wall:
        continue
    arms[tag].append(float(wall.group(1)))
    pt = re.search(r"partition: last=\(([\d. ]+)\)", text)
    if pt:
        part[tag] = pt.group(1)

if not arms:
    sys.exit("no arm produced a wall time")

print(f"{'arm':8s} {'n':>3s} {'median':>8s} {'min':>8s} {'max':>8s} {'CV%':>6s} {'vs live':>8s}  partition")
base = "live" if "live" in arms else next(iter(arms))
live = st.median(arms[base])
for tag in sorted(arms, key=lambda t: st.median(arms[t])):
    w = arms[tag]
    cv = 100 * st.stdev(w) / st.mean(w) if len(w) > 1 else 0.0
    print(f"{tag:8s} {len(w):3d} {st.median(w):8.2f} {min(w):8.2f} {max(w):8.2f} {cv:6.2f} "
          f"{st.median(w)/live:8.4f}  {part.get(tag,'?')}")

# A/A floor: the caller lists one config twice ("live" and "live2") to measure the run's own
# spread. That pair, not the best-vs-worst arm, is the smallest difference worth reading.
aa = [t for t in ("live", "live2") if t in arms]
if len(aa) == 2:
    a, b = arms[aa[0]], arms[aa[1]]
    print(f"\nA/A floor ({aa[0]} vs {aa[1]}): median {st.median(a):.2f} vs {st.median(b):.2f}, "
          f"{100*abs(st.median(a)-st.median(b))/st.median(b):.2f}% apart")
    print("a difference smaller than that floor is not a result")
else:
    print("\nno A/A pair present (list one config twice as 'live' and 'live2')")
PY
