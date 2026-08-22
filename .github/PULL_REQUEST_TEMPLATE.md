<!--
Thanks for contributing to Kiln! Please read CONTRIBUTING.md first.
Keep PRs focused; unrelated changes are easier to review split apart.
-->

## Summary

<!-- What does this change and why? -->

## Related issues

<!-- e.g. Closes #123 -->

## Checklist

- [ ] `dotnet build` and `dotnet test` pass locally (see CONTRIBUTING.md)
- [ ] SIMD changes keep scalar/SIMD parity tests green (NEON and AVX2/SSSE3 as applicable)
- [ ] Perf-sensitive changes were checked against the committed baseline (`perf/`, see docs/perf-gate.md)
- [ ] Spec-derived tables/constants carry their ITU-T H.264 clause/table citation
- [ ] Public API changes are documented (README / XML docs)
- [ ] Commits are scoped and messages explain the "why"

## Notes for reviewers

<!-- Anything non-obvious: tradeoffs, follow-ups, areas you'd like extra eyes on. -->
