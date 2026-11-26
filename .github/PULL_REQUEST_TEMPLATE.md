<!--
Thanks for contributing to Kiln! Please read CONTRIBUTING.md first.
Keep PRs focused; unrelated changes are easier to review split apart.
-->

## Summary

<!-- What does this change and why? -->

## Related issues

<!-- e.g. Closes #123 -->

## Checklist

- [ ] `dotnet build -c Release` reports zero warnings and `dotnet test -c Release` passes locally (see CONTRIBUTING.md)
- [ ] SIMD changes keep the SIMD and scalar parity tests green, and new kernels come with a parity test
- [ ] Performance-sensitive changes were checked with `scripts/h264-simd-perf-gate.sh` (see docs/perf-gate.md)
- [ ] Tables and constants taken from the spec cite their ITU-T H.264 clause or table
- [ ] Public API changes are reflected in the XML docs and, where relevant, the README
- [ ] Commits are signed off (`git commit -s`), focused, and explain why the change is needed

## Notes for reviewers

<!-- Anything non-obvious: tradeoffs, follow-ups, areas you'd like extra eyes on. -->
