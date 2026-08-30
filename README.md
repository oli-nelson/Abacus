# Abacus

Abacus is a small Unix-oriented C# orchestrator for [Beads](https://github.com/gastownhall/beads), Git, [OpenCode](https://github.com/anomalyco/opencode), and tmux. The product contract and implementation sequence live in [SPEC.md](SPEC.md) and [PLAN.md](PLAN.md).

## Minimum supported tools

Abacus targets macOS and Linux and requires these commands on `PATH`:

| Tool | Minimum supported version |
| --- | --- |
| .NET SDK | 10.0.101 |
| Beads (`bd`) | 1.2.2 |
| Git | 2.55.0 |
| OpenCode | 1.17.10 |
| tmux | 3.6a |

The command contracts were captured against exactly these versions. See [the CLI contract notes](docs/contracts/cli-contracts.md) for the supported invocations, JSON shapes, and exit behavior.
