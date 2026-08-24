# Contributing to Kiln

Kiln is a pure-managed, SIMD-accelerated H.264 baseline-profile encoder.
It exists specifically so it can be embedded under Apache-2.0 in products
where GPL/LGPL codec linkage is a problem. Contributions are held to that
bar — see Originality and citations below.

## Getting set up

You need the **.NET 10 SDK** and nothing else to build and run the test
suite. `ffmpeg` on `PATH` is an **optional** dependency used by a handful of
decode-oracle tests (see below); those tests detect its absence and pass
without asserting, so a clean checkout with no `ffmpeg` installed still
passes `dotnet test`.

```bash
git clone https://github.com/Proxeno/kiln.git
cd kiln

dotnet restore
dotnet build -c Release
dotnet test -c Release
```

`dotnet build` must report zero warnings — see [Style](#style) below.

## Repository layout

- [`src/Kiln`](src/Kiln) — the library. `Kiln`, `Kiln.RateControl`,
  `Kiln.Recovery` are the public namespaces; `Kiln.Internal.H264` is
  internal (accessible to `Kiln.Tests` and `Kiln.Benchmarks` via
  `InternalsVisibleTo`) and holds the actual codec implementation, including
  the experimental `Internal/H264/Adaptation` and `Internal/H264/Queue`
  subsystems.
- [`tests/Kiln.Tests`](tests/Kiln.Tests) — xunit test project.
- [`bench/Kiln.Benchmarks`](bench/Kiln.Benchmarks) — BenchmarkDotNet
  benchmarks used by the perf gate (below) and for ad hoc profiling.
- [`samples/Kiln.Capture`](samples/Kiln.Capture) — console sample that records
  a camera to `.m4v`. It is the only project with a third-party runtime
  dependency (FlashCap, for camera access); the library itself must stay
  dependency-free. Its MP4 muxer is sample code, not public API.
- [`docs/architecture.md`](docs/architecture.md) — pipeline stages, SIMD
  kernel structure, and which subsystems are production vs. experimental.
  Read this before making non-trivial changes to the encoder.

## Test layout

`tests/Kiln.Tests` is organized by what's under test, not by test type; a
few conventions to know:

- **SIMD/scalar parity tests** — assert a SIMD kernel produces bit-identical
  output to its scalar counterpart. Named
  `<Thing>_simd_matches_scalar_when_intrinsics_available` and self-skip via
  an `IsSupported` check on hosts/CI runners lacking the target ISA. Every
  `IH264KernelSet` member needs one of these; see
  [docs/architecture.md](docs/architecture.md#simd-kernel-structure).
- **Spec-roundtrip / CAVLC decode tests** (`H264CavlcSpecDecode.cs`,
  `H264CavlcSpecRoundtripTests.cs`, `H264SpecInverse4x4ParityTests.cs`, …) —
  decode Kiln's own bitstream output against an independent
  spec-clause-derived reference implementation in the test project, not
  against any external decoder library.
- **Golden-frame regression** (`H264GoldenFrameRegressionTests.cs`, fixtures
  under `tests/Kiln.Tests/Fixtures/H264Golden`) — byte-for-byte or
  PSNR-floor comparisons against committed reference output. If you
  intentionally change encoder output (new RD heuristic, new default), you
  will need to regenerate and review these fixtures — do not update them
  reflexively to make a failing test pass.
- **Decode-oracle / ffmpeg smoke tests** (`H264FfmpegDecodeSmokeTests.cs`,
  `H264ChromaDcEncoderFfmpegParityTests.cs`) — shell out to an `ffmpeg`
  binary on `PATH` to decode Kiln's Annex B output as an independent
  correctness oracle. These exit early (pass) when `ffmpeg` isn't found;
  install ffmpeg locally if you're touching bitstream syntax or
  reconstruction and want this coverage to actually run.
- **Adaptive rate control** (`AdaptiveRateControlTests/Phase1_InterfacesTests.cs`
  through `Phase5_AdaptationTests.cs`) — cover the experimental
  `Adaptation`/`Queue` subsystems and the public `RateControl`/`Recovery`
  layer they build on.

Test collections run non-parallel within the project
(`tests/Kiln.Tests/xunit.runner.json`); several tests exercise process-wide
state (env-var-gated diagnostics, static kernel dispatch) that isn't safe
under concurrent test execution.

## Benchmarks and the perf gate

`bench/Kiln.Benchmarks` holds BenchmarkDotNet benchmarks for the SIMD hot
paths (SAD/SATD kernels, motion search, P-inter end-to-end). Changes to
`Kiln.Internal.H264` kernel code, motion estimation, or the P-inter Phase 2b
path should be checked against the committed performance baseline before
merging:

```bash
scripts/h264-simd-perf-gate.sh
```

See [docs/perf-gate.md](docs/perf-gate.md) for exactly what this runs, the
regression thresholds, and how to refresh
`perf/h264-simd-perf-baseline-latest.json` when a change intentionally moves
the numbers. Get SIMD/scalar parity tests green first — the perf gate only
measures speed, not correctness.

## Style

The build is strict on purpose, project-wide (`Directory.Build.props`):

- `TreatWarningsAsErrors` is on, at `AnalysisLevel=latest`. No warnings are
  tolerated — `dotnet build` must report zero warnings, zero errors.
- `EnforceCodeStyleInBuild` is on — style violations fail the build.
- `Nullable` is enabled project-wide.
- `InvariantGlobalization` is on — comparisons must be ordinal.
- `AllowUnsafeBlocks` is on in `src/Kiln` and the test/benchmark projects,
  because the SIMD kernels use hardware intrinsics and pointer-based span
  reconstruction for the multi-slice fast path. Keep `unsafe` scoped as
  tightly as the existing code does; don't spread it beyond where it's
  actually needed for intrinsics or pinning.

## Originality and citations

Kiln is original work written against the published ITU-T H.264
(ISO/IEC 14496-10) specification. Two rules keep it that way:

- **Implement from the spec, and cite it.** When you implement a spec clause,
  cite it (`§8.5.9`, `Table 9-4`, etc.) in the doc comment or inline comment
  next to the code it justifies. Numeric tables that reproduce specification
  data (CAVLC coefficient tables, chroma QP tables, normAdjust4x4,
  coded_block_pattern mappings, etc.) must carry a clause/table citation so
  they're identifiable as spec data.
- **Contributions must be your own original work.** Write code from the
  specification text and your own design. If you're unsure whether an approach
  is appropriate, ask in the PR description before writing the code — it's
  easier to redirect up front than to rework afterward.

## License of contributions

Kiln is licensed under Apache-2.0, and contributions are accepted under the
same license: when you open a pull request, you agree that your contribution
is licensed under Apache-2.0 (inbound = outbound). You keep the copyright to
your work — there is no copyright assignment.

We use the [Developer Certificate of Origin](https://developercertificate.org/)
(DCO). Sign off every commit:

```
git commit -s
```

That appends a `Signed-off-by: Your Name <you@example.com>` line certifying
that you wrote the contribution (or otherwise have the right to submit it) and
that it may be distributed under the project's license.

## Continuous integration

CI (`.github/workflows/ci.yml`) builds and tests on `ubuntu-latest`,
`windows-latest`, and `macos-latest` on every push and PR to `main`. This
matrix exists specifically to exercise both SIMD ISA families: Linux and
Windows runners are x64 (AVX2 and SSSE3 kernel sets), macOS runners are
arm64 (NEON). A change that only builds/tests clean on one architecture is
not done — if you can't test arm64 locally, say so in the PR and rely on the
macOS CI leg.
