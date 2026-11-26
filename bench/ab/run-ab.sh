#!/usr/bin/env bash
#
# Paired A/B encode run: builds bench/Kiln.AbHarness in each tree, then alternates A/B (and
# B/A on even rounds) so ambient drift on a shared host hits both sides equally.
#
# Usage: run-ab.sh <dirA> <dirB> <rounds> <outdir>
#
# Writes <outdir>/A-r<N>.jsonl and <outdir>/B-r<N>.jsonl, one JSON line per cell, plus
# <outdir>/loadavg.log with the host load average sampled before every run.
#
# Set KILN_AB_QUICK=1 for the reduced matrix (720p, HighQuality, 10 frames).
#
set -euo pipefail

if [ "$#" -ne 4 ]; then
    echo "usage: $0 <dirA> <dirB> <rounds> <outdir>" >&2
    exit 2
fi

dirA=$1
dirB=$2
rounds=$3
outdir=$4

case $rounds in
    ''|*[!0-9]*)
        echo "rounds must be a positive integer" >&2
        exit 2
        ;;
esac

if [ "$rounds" -lt 1 ]; then
    echo "rounds must be a positive integer" >&2
    exit 2
fi

# The host may only run a newer runtime than the project targets.
export DOTNET_ROLL_FORWARD=Major

loadavg() {
    if [ -r /proc/loadavg ]; then
        cat /proc/loadavg
    else
        sysctl -n vm.loadavg
    fi
}

mkdir -p "$outdir"

echo "building A: $dirA/bench/Kiln.AbHarness" >&2
dotnet build -c Release "$dirA/bench/Kiln.AbHarness" >&2

echo "building B: $dirB/bench/Kiln.AbHarness" >&2
dotnet build -c Release "$dirB/bench/Kiln.AbHarness" >&2

run_one() {
    side=$1
    dir=$2
    round=$3
    out="$outdir/$side-r$round.jsonl"

    : > "$out"
    printf '%s side=%s round=%s load=%s\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$side" "$round" "$(loadavg)" >> "$outdir/loadavg.log"

    dotnet run --project "$dir/bench/Kiln.AbHarness" -c Release --no-build -- \
        --run all --rounds 1 --out "$out" >&2
}

r=1
while [ "$r" -le "$rounds" ]; do
    if [ $((r % 2)) -eq 1 ]; then
        run_one A "$dirA" "$r"
        run_one B "$dirB" "$r"
    else
        run_one B "$dirB" "$r"
        run_one A "$dirA" "$r"
    fi
    r=$((r + 1))
done

echo "wrote $outdir" >&2
echo "report: python3 bench/ab/ab-report.py $outdir" >&2
