# Security policy

## Supported versions

Kiln is pre-1.0. Security fixes land on `main` and in the latest published
minor version only; there are no long-term support branches yet. If you're
running an older version, update before reporting — we won't backport fixes
to versions prior to latest.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub Security Advisories:

- Go to <https://github.com/Proxeno/kiln/security/advisories/new>
  (Security tab → Report a vulnerability), or
- Use the **Report a vulnerability** button on the repository's Security tab.

That creates a private thread visible only to the maintainers and to you.

Please include, as far as you can:

- what the issue is, and what an attacker or a malformed input gets you;
- the Kiln version (or commit) and target platform/architecture (x64 AVX2,
  x64 SSSE3, arm64 NEON, or scalar fallback) — SIMD-specific bugs often
  don't reproduce across kernel sets;
- a minimal reproduction: input dimensions, options
  (`H264BaselineEncoderOptions`), and frame data if the issue depends on
  content rather than just size;
- whether `PreferHardwareIntrinsics` being `true` vs. `false` changes the
  behavior, if you've checked.

We will acknowledge the report, keep you updated while we investigate, and
credit you in the advisory when it is published, unless you would rather
stay anonymous.

## Scope

Kiln is a library, not a service: it processes frame buffers you pass to
`H264BaselineEncoder.EncodeFrame` and produces an Annex B byte stream. The
encoder's input path is designed for a trusted producer — the caller
supplying raw frames (e.g. a game-streaming pipeline capturing its own
render output) — not for arbitrary untrusted frame data from a network peer.
That said, memory-safety issues are always in scope regardless of how
"trusted" the input is expected to be, because:

- Kiln has **no native dependencies** and does its own pixel-format and
  buffer-size validation entirely in managed/unsafe C#; a bounds-check gap
  here is a bug in Kiln, not a wrapped native library.
- Several of the encoder's hot paths (`Kiln.Internal.H264` SIMD kernel sets,
  the multi-slice pointer-pinning path in `H264BaselineEncoder`) use
  `unsafe` code and raw hardware intrinsics. Buffer-safety issues in those
  paths — out-of-bounds reads/writes, uninitialized-memory disclosure into
  the output bitstream, or SIMD-vs-scalar divergence that produces an
  invalid or exploitable reconstruction — are exactly the class of bug this
  policy most wants reported, and they're the ones parity tests
  (see [CONTRIBUTING.md](CONTRIBUTING.md)) are designed to catch before
  release.
- Crashes, panics, or hangs on malformed or adversarial `width`/`height`/
  stride/option combinations (not just "normal" resolutions) are in scope.

Out of scope:

- Denial of service from simply feeding the encoder more/larger frames than
  your system can handle — that's a capacity-planning problem for the
  embedding application, not a Kiln vulnerability.
- Issues that require an attacker to already control the process embedding
  Kiln (e.g. arbitrary code already running in the same address space).
- Findings from automated scanners with no demonstrated impact specific to
  this codebase.

If you're unsure whether something is in scope, report it privately anyway
— worst case we tell you it's out of scope, which costs you nothing and
costs us little.
