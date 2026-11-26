# A/B encode harness

Measures the **end-to-end speed** of whole-file H.264 baseline encoding between two commits and
reports a paired ratio that survives a shared, noisy host.

## What it measures

`bench/Kiln.AbHarness` encodes a deterministic synthetic matrix through kiln's public API only
(`H264BaselineEncoder`, `H264BaselineEncoderOptions`, `EncoderSpeedMode`), so the same source
builds against both commits under test. Each cell is one clip encoded from scratch:

- **Content:** `static` (textured, no motion), `moving` (background panned 3 px/frame plus a
  moving object), `divergent` (two halves scrolling in opposite directions plus two objects).
  Frames are generated in-process from fixed seeds — no external files.
- **Resolutions:** 1280x720 and 1920x1080.
- **Presets:** every `EncoderSpeedMode` both commits ship (`HighQuality`, `Balanced`, `Fast`,
  `VeryFast`).
- **Slices:** 1 and 8.
- **60 frames per clip**, IDR every 60 (so only frame 0 is an IDR).

Per cell the harness does one discarded warm-up pass, then a timed pass, and writes one JSON line:
`{cell, msPerFrame, framesEncoded, bytes, sha256}`, where `sha256` is the hash of the concatenated
Annex-B bitstream.

## How to run it

Both trees must contain `bench/Kiln.AbHarness` (e.g. two worktrees of the commits under test).

```bash
bench/ab/run-ab.sh <dirA> <dirB> <rounds> <outdir>
python3 bench/ab/ab-report.py <outdir>
```

`run-ab.sh` builds the harness in each tree, then runs `rounds` pairs. Odd rounds run A then B;
even rounds run B then A, so ambient drift hits both sides equally. It appends the host load
average, sampled before every run, to `<outdir>/loadavg.log`.

For a fast smoke test (720p, HighQuality, 10 frames):

```bash
KILN_AB_QUICK=1 bench/ab/run-ab.sh . . 2 /tmp/ab-quick
python3 bench/ab/ab-report.py /tmp/ab-quick
```

List the cell ids of the current matrix:

```bash
dotnet run --project bench/Kiln.AbHarness -c Release -- --list
```

## How to read it

The report's ratio is **B/A ms per frame; below 1 means B is faster**. Per cell it reports the
median of the per-round paired ratios (not the ratio of medians), a 95% bootstrap confidence
interval over 2000 resamples with a fixed seed, and whether A and B produced the same `sha256` in
every round.

A speed change should leave `sha256 identical` at `yes` for every cell — encoding cost must not
change decisions. Geometric-mean ratios are reported per preset and overall.

Timing on a shared machine is evidence, not a claim: read the ratio next to the load average in
`loadavg.log`, and prefer the confidence interval over any single round.
