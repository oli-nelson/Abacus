# Abacus Agent Guidance

Before making changes, read the project documents in this order:

1. [SPEC.md](SPEC.md) — product requirements and required agent workflow.
2. [PLAN.md](PLAN.md) — phased implementation plan and simplicity constraints.

Use the upstream GitHub documentation for the external command-line tools:

- [Beads documentation](https://github.com/gastownhall/beads/tree/main/docs)
- [OpenCode documentation](https://github.com/anomalyco/opencode/tree/dev/packages/web/src/content/docs)

Keep the implementation shell-first and simple: C# should orchestrate the existing `bd`, `git`, `opencode`, and `tmux` command-line tools rather than integrating with their APIs or protocols.

Just before committing, update the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md)
with concise, user-facing entries for noteworthy changes (features, fixes,
breaking changes, or meaningful operational/documentation changes). Skip routine
internal churn and avoid duplicate entries. Keep previously released sections
unchanged; the release flow promotes `Unreleased` into a dated version section
and creates a new empty `Unreleased` section.
