## Summary
<!-- What does this PR do and why? 1–3 bullet points. -->

## Checklist

### Every PR
- [ ] Commit messages describe *why*, not just what
- [ ] No dead code, commented-out blocks, or speculative additions

### If this PR adds new behaviour (code)
- [ ] A failing test was written first (TDD: red → green → refactor)
- [ ] All new behaviour has test coverage
- [ ] No production code exists without a corresponding test

### If this PR adds or changes a diagram in `docs/architecture/`
- [ ] A `.puml` source file is created or updated alongside the `.md`
- [ ] The `.md` file references the `.puml` source

### If this PR makes an architectural decision
- [ ] An ADR is created in `docs/adr/` following the standard format
- [ ] The ADR status is set to `Accepted`

### If this PR introduces new conventions or standards
- [ ] The relevant `CLAUDE.md` file is updated
