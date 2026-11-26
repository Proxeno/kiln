# Contributing to Kiln

Kiln is an H.264 Baseline encoder written entirely in C#. It exists so that
it can be embedded under Apache-2.0 in products where GPL or LGPL codec
linkage is a problem, and contributions are held to that bar. See
[Originality and citations](#originality-and-citations).

## Getting set up

You need the **.NET 10 SDK** to build and to run the tests. Nothing else is
required.

`ffmpeg` on `PATH` is optional. A few tests use it as an independent decoder.
Without it those tests pass without asserting anything, so a clean checkout
still passes `dotnet test`.

```bash
git clone https://github.com/Proxeno/kiln.git
cd kiln

dotnet restore
dotnet build -c Release
dotnet test -c Release
```

`dotnet build` must report zero warnings. See [Style](#style).

## Repository layout

- [`src/Kiln`](src/Kiln) is the library. The public namespaces are `Kiln`,
  `Kiln.RateControl` and `Kiln.Recovery`. The codec itself lives in the
  internal namespace `Kiln.Internal.H264`, which `Kiln.Tests` and
  `Kiln.Benchmarks` reach through `InternalsVisibleTo`.
- [`tests/Kiln.Tests`](tests/Kiln.Tests) is the xunit test project.
- [`bench/Kiln.Benchmarks`](bench/Kiln.Benchmarks) holds the BenchmarkDotNet
  benchmarks behind the performance gate, plus stopwatch harnesses for ad hoc
  measurement.
- [`samples/Kiln.Capture`](samples/Kiln.Capture) is a console sample that
  records a camera to `.m4v`. Its MP4 muxer is sample code, not public API.
- [`docs/architecture.md`](docs/architecture.md) describes the encode
  pipeline, slice threading, the SIMD kernel sets and the rate controller.
  Read it before making a non-trivial change to the encoder.

**Dependencies.** The library must have no native dependencies. Its only
package dependency is `Microsoft.Extensions.Logging.Abstractions`. Do not add
another without discussing it first. The capture sample is the one project
that uses a third-party package (FlashCap, for camera access).

## Test layout

`tests/Kiln.Tests` is organised by what is under test. The kinds of test you
will meet:

- **SIMD and scalar parity tests** assert that a SIMD kernel produces output
  bit-identical to its scalar counterpart. They check `IsSupported` and pass
  without asserting on a CPU that lacks the instruction set. Every
  `IH264KernelSet` member needs one. See
  [docs/architecture.md](docs/architecture.md#parity-tests).
- **Spec round-trip tests** (`H264CavlcSpecDecode.cs`,
  `H264CavlcSpecRoundtripTests.cs`, `H264SpecInverse4x4ParityTests.cs` and
  others) decode Kiln's output with a reference implementation that lives in
  the test project and was written from the spec clauses. They use no
  external decoder.
- **Golden-frame regression tests** (`H264GoldenFrameRegressionTests.cs`,
  with fixtures under `tests/Kiln.Tests/Fixtures/H264Golden`) compare output
  against committed reference data, byte for byte or against a PSNR floor.
  If you change encoder output on purpose, regenerate the fixtures and review
  the difference. Never update them just to make a failing test pass.
- **ffmpeg decode tests** (`H264FfmpegDecodeSmokeTests.cs`,
  `H264ChromaDcEncoderFfmpegParityTests.cs` and others) run an `ffmpeg`
  binary from `PATH` to decode Kiln's Annex B output as an independent check.
  They pass without asserting when `ffmpeg` is not found. Install ffmpeg if
  you are touching bitstream syntax or reconstruction, so that this coverage
  actually runs.
- **Rate-control tests** (`AdaptiveRateControlTests/`) cover the public
  `Kiln.RateControl` and `Kiln.Recovery` types and the internal adaptation
  layer built on them.

Test collections do not run in parallel
(`tests/Kiln.Tests/xunit.runner.json`). Several tests touch process-wide
state, such as the diagnostics switches, and are not safe to run
concurrently.

## Benchmarks and the perf gate

`bench/Kiln.Benchmarks` holds BenchmarkDotNet benchmarks for the SIMD hot
paths: the SAD and SATD kernels, motion search, and a full P-picture encode.
If you change kernel code, motion estimation or the P-macroblock path, check
the change against the committed baseline before you open a pull request:

```bash
scripts/h264-simd-perf-gate.sh
```

The committed baseline was captured on an arm64 Mac, so the comparison only
means something on similar hardware. On another machine, capture your own
baseline from `main` first. [docs/perf-gate.md](docs/perf-gate.md) explains
what the gate runs, its thresholds, and how to refresh the baseline when a
change moves the numbers on purpose.

Get the parity tests green first. The gate measures speed, not correctness.

## Style

The build is strict on purpose. These settings apply to every project
(`Directory.Build.props`):

- `TreatWarningsAsErrors` is on, with `AnalysisLevel` set to `latest`. The
  build must report zero warnings and zero errors.
- `EnforceCodeStyleInBuild` is on, so style violations fail the build.
- `Nullable` is enabled.
- `InvariantGlobalization` is on.

`AllowUnsafeBlocks` is on where SIMD code needs it. Keep `unsafe` as tightly
scoped as the existing code does, and use it only where intrinsics or pinning
require it.

Comments and XML documentation describe the code as it is. Leave out history,
and leave out references to notes or documents that are not in the
repository.

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
`windows-latest` and `macos-latest` for every push and pull request to
`main`. The matrix exists to cover both instruction-set families: the Linux
and Windows runners are x64 and use the AVX2 kernel set, and the macOS
runners are arm64 and use NEON.

A change that builds and passes on only one architecture is not finished. If
you cannot test the other one locally, say so in the pull request and rely on
CI for it.
