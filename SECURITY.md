# Security policy

## Supported versions

Kiln is pre-1.0. Security fixes land on `main` and in the latest published
version only. There are no long-term support branches, and fixes are not
backported. If you are on an older version, please check whether the problem
still exists in the latest one before you report it.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub Security Advisories:

- go to <https://github.com/Proxeno/kiln/security/advisories/new>, or
- use the **Report a vulnerability** button on the repository's Security tab.

Either route opens a private thread that only the maintainers and you can
see.

Please include, as far as you can:

- what the issue is, and what an attacker or a malformed input can achieve;
- the Kiln version or commit, and the CPU it ran on (x64 with AVX2, x64 with
  SSSE3 only, arm64, or the scalar fallback), because a SIMD bug often
  reproduces on one kernel set only;
- a minimal reproduction: the frame dimensions, the
  `H264BaselineEncoderOptions` used, and the frame data if the issue depends
  on content;
- whether setting `PreferHardwareIntrinsics` to `false` changes the
  behaviour, if you have checked.

We will acknowledge the report, keep you updated while we investigate, and
credit you in the advisory when it is published, unless you would rather stay
anonymous.

## Scope

Kiln is a library, not a service. It encodes the frame buffers you pass to
`H264BaselineEncoder.EncodeFrame` and returns an Annex B byte stream. It is
designed for a trusted producer, such as a streaming server encoding its own
rendered output, and not for frame data that arrives from an untrusted peer.

Memory safety is in scope all the same, however trusted the input:

- Kiln has no native dependencies. Every buffer access is Kiln's own code, so
  a missing bounds check is a bug in Kiln.
- Some hot paths use `unsafe` code and hardware intrinsics: the SIMD kernel
  sets in `Kiln.Internal.H264`, and the multi-slice path in
  `H264BaselineEncoder` that pins the input planes. Out-of-bounds reads or
  writes there, uninitialised memory reaching the output stream, and SIMD
  output that differs from scalar output are the reports this policy most
  wants.
- Crashes or hangs caused by unusual or hostile combinations of width,
  height, stride and options are in scope.

Out of scope:

- Denial of service from feeding the encoder more or larger frames than the
  machine can handle. That is capacity planning for the embedding
  application.
- Issues that require the attacker to already control the process that
  embeds Kiln.
- Automated scanner findings with no demonstrated impact on this codebase.

If you are unsure whether something is in scope, report it privately anyway.
