# Abacus Documentation

Welcome to the Abacus documentation. Start with the task you are trying to
complete rather than reading every page in order.

## Use Abacus

| Guide | Best for |
| --- | --- |
| [Quick start](quick-start.html) | A visual, end-to-end tour of setup and daily use |
| [Getting started](getting-started.md) | Copyable setup paths for new and existing repositories |
| [CLI reference](cli-reference.md) | Looking up commands, modes, and option behavior |
| [Operations guide](operations.md) | Running, observing, pausing, recovering, and stopping agents |
| [Shared Dolt operations](shared-dolt.md) | Creating, migrating, troubleshooting, backing up, and rolling back shared Beads storage |
| [Architecture and boundaries](architecture.md) | Understanding how Abacus stays safe and what it deliberately leaves to other tools |
| [Agent-loop flow](agent-loop-flow.html) | A visual explanation of the runtime state machine |

## Develop and release Abacus

| Document | Role |
| --- | --- |
| [Product specification](../SPEC.md) | Normative product behavior and exact built-in agent prompt |
| [Implementation plan](../PLAN.md) | Design constraints, implementation phases, and definition of done |
| [External CLI contracts](contracts/cli-contracts.md) | Captured assumptions for Beads, Git, tmux, and agent-harness subprocesses |
| [Release smoke-test record](smoke-test.md) | Manual evidence from the latest recorded release exercise |
| [Engineering backlog](../TODO.md) | Small follow-up work not yet represented elsewhere |

## Bundled agent skills

Abacus can install four optional skills into another repository with
`abacus --install-skills`:

- [Beads Planner](../skills/abacus-beads-planner/SKILL.md) — design a reviewed issue graph
- [Beads Doctor](../skills/abacus-beads-doctor/SKILL.md) — audit and repair issue quality
- [Beads Attention](../skills/abacus-beads-attention/SKILL.md) — summarize issues needing human input
- [Git Check](../skills/abacus-git-check/SKILL.md) — audit agent-facing Git restrictions

The skills are distributable product assets, so their `SKILL.md` files are
instruction contracts rather than general user guides.

## Source of truth

When documents differ, use this order:

1. [`SPEC.md`](../SPEC.md) for intended product behavior.
2. Current source and tests for implemented behavior.
3. [`docs/contracts/cli-contracts.md`](contracts/cli-contracts.md) for external
   tool behavior captured at the supported versions.
4. User guides for explanation and examples.

Update all affected pages when a behavior change makes the guides and
implementation disagree.
