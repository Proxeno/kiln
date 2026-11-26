#!/usr/bin/env python3
"""Paired A/B report for bench/ab/run-ab.sh output.

Reads <outdir>/A-r<N>.jsonl and <outdir>/B-r<N>.jsonl, pairs each cell by round, and reports
the per-cell median B/A ms-per-frame ratio with a 95% bootstrap confidence interval.

The ratio is B/A ms per frame: below 1 means B is faster.

Usage: ab-report.py <outdir>
"""

import glob
import json
import math
import os
import random
import re
import statistics
import sys

RESAMPLES = 2000
SEED = 20261126

CELL_RE = re.compile(
    r"^(?P<content>.+?)-(?P<width>\d+)x(?P<height>\d+)-(?P<preset>[A-Za-z]+)-s(?P<slices>\d+)$"
)


def load_rounds(outdir, side):
    """Return [(round_number, {cell_id: record}), ...] sorted by round number."""
    found = []
    for path in glob.glob(os.path.join(outdir, side + "-r*.jsonl")):
        name = os.path.basename(path)
        match = re.fullmatch(re.escape(side) + r"-r(\d+)\.jsonl", name)
        if not match:
            continue
        records = {}
        with open(path, encoding="utf-8-sig") as handle:
            for line in handle:
                line = line.strip()
                if line:
                    record = json.loads(line)
                    records[record["cell"]] = record
        found.append((int(match.group(1)), records))
    found.sort(key=lambda item: item[0])
    return found


def median(values):
    return statistics.median(values)


def bootstrap_ci(values, rng):
    """Percentile bootstrap CI of the median over resampled values."""
    count = len(values)
    medians = []
    for _ in range(RESAMPLES):
        sample = [values[rng.randrange(count)] for _ in range(count)]
        medians.append(statistics.median(sample))
    medians.sort()
    low = medians[int(0.025 * (RESAMPLES - 1))]
    high = medians[int(0.975 * (RESAMPLES - 1))]
    return low, high


def geomean(values):
    if not values:
        return float("nan")
    return math.exp(sum(math.log(value) for value in values) / len(values))


def sort_key(cell_id):
    match = CELL_RE.match(cell_id)
    if not match:
        return (99, cell_id)
    content_rank = {"static": 0, "moving": 1, "divergent": 2}.get(match.group("content"), 9)
    preset_rank = {"HighQuality": 0, "Balanced": 1, "Fast": 2, "VeryFast": 3}.get(
        match.group("preset"), 9
    )
    return (
        content_rank,
        int(match.group("width")),
        int(match.group("height")),
        preset_rank,
        int(match.group("slices")),
        cell_id,
    )


def preset_of(cell_id):
    match = CELL_RE.match(cell_id)
    if match:
        return match.group("preset")
    return "unknown"


def main(argv):
    if len(argv) != 2:
        print("usage: ab-report.py <outdir>", file=sys.stderr)
        return 2

    outdir = argv[1]
    rounds_a = load_rounds(outdir, "A")
    rounds_b = load_rounds(outdir, "B")
    if not rounds_a or not rounds_b:
        print("no A/B jsonl files found in " + outdir, file=sys.stderr)
        return 1

    by_round_a = dict(rounds_a)
    by_round_b = dict(rounds_b)
    common_rounds = sorted(set(by_round_a) & set(by_round_b))
    if not common_rounds:
        print("no rounds present for both A and B", file=sys.stderr)
        return 1

    cells = set()
    for round_number in common_rounds:
        cells |= set(by_round_a[round_number]) & set(by_round_b[round_number])

    rng = random.Random(SEED)

    rows = []
    ratios_by_preset = {}
    for cell_id in sorted(cells, key=sort_key):
        ratios = []
        a_ms = []
        b_ms = []
        sha_identical = True
        for round_number in common_rounds:
            a = by_round_a[round_number].get(cell_id)
            b = by_round_b[round_number].get(cell_id)
            if a is None or b is None:
                continue
            a_ms.append(a["msPerFrame"])
            b_ms.append(b["msPerFrame"])
            if a["msPerFrame"] > 0:
                ratios.append(b["msPerFrame"] / a["msPerFrame"])
            sha_identical = sha_identical and (a["sha256"] == b["sha256"])

        if not ratios:
            continue

        ratio = median(ratios)
        low, high = bootstrap_ci(ratios, rng)
        rows.append(
            {
                "cell": cell_id,
                "a_ms": median(a_ms),
                "b_ms": median(b_ms),
                "ratio": ratio,
                "low": low,
                "high": high,
                "sha": sha_identical,
                "rounds": len(ratios),
            }
        )
        ratios_by_preset.setdefault(preset_of(cell_id), []).append(ratio)

    print("# A/B encode report")
    print()
    print("Ratio is **B/A ms per frame; below 1 means B is faster**.")
    print()
    print(f"Rounds paired: {len(common_rounds)} ({', '.join(str(r) for r in common_rounds)})")
    print()
    print(f"Cells: {len(rows)}")
    print()
    print("| cell | A ms/frame | B ms/frame | B/A | 95% CI | sha256 identical |")
    print("|:---|---:|---:|---:|---:|:---:|")
    for row in rows:
        sha = "yes" if row["sha"] else "NO"
        print(
            f"| {row['cell']} | {row['a_ms']:.3f} | {row['b_ms']:.3f} | "
            f"{row['ratio']:.4f} | {row['low']:.4f} - {row['high']:.4f} | {sha} |"
        )

    print()
    print("| grouping | geomean B/A | cells |")
    print("|:---|---:|---:|")
    for preset in sorted(ratios_by_preset, key=lambda name: sort_key(f"x-1x1-{name}-s1")):
        values = ratios_by_preset[preset]
        print(f"| {preset} | {geomean(values):.4f} | {len(values)} |")
    overall = [row["ratio"] for row in rows]
    print(f"| **overall** | **{geomean(overall):.4f}** | {len(overall)} |")

    bad_sha = [row["cell"] for row in rows if not row["sha"]]
    if bad_sha:
        print()
        print("WARNING: bitstreams differ between A and B for: " + ", ".join(bad_sha))

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
