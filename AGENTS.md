# Abacus Agent Guidance

Before making changes, read the project documents in this order:

1. [SPEC.md](SPEC.md) — product requirements and required agent workflow.
2. [PLAN.md](PLAN.md) — phased implementation plan and simplicity constraints.

Use the upstream GitHub documentation for the external command-line tools:

- [Beads documentation](https://github.com/gastownhall/beads/tree/main/docs)
- [OpenCode documentation](https://github.com/anomalyco/opencode/tree/dev/packages/web/src/content/docs)

Keep the implementation shell-first and simple: C# should orchestrate the existing `bd`, `git`, `opencode`, and `tmux` command-line tools rather than integrating with their APIs or protocols.
