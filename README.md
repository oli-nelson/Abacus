# Abacus

**Turn a Beads backlog into safe, observable parallel agent work.**

Abacus is a small Unix-oriented orchestrator for coding agents. It finds ready
[Beads](https://github.com/gastownhall/beads) issues, assigns each one atomically,
prepares an isolated Git workspace, and supervises an interactive OpenCode,
Codex, or Claude Code session until the issue reaches a terminal state.

```text
Beads backlog ──► atomic claim ──► abacus/<issue-id> ──► coding agent
      ▲                                                       │
      └──────── status, comments, recovery, and summary ◄─────┘
```

Abacus stays deliberately thin: it coordinates the command-line tools you
already use rather than replacing Git, Beads, tmux, or your agent harness.

> [!IMPORTANT]
> Agent workspaces are disposable. Before every claim, Abacus resets tracked
> changes and removes untracked, non-ignored files. Never assign a workspace
> that contains work you have not committed or moved elsewhere.

## Why Abacus?

- **Parallel without double work.** Beads provides atomic claims; every agent
  receives a distinct Git worktree or clone.
- **Interactive agents, not hidden jobs.** OpenCode, Codex, and Claude Code run
  in real tmux panes. OpenCode Server clients can instead run as supervised
  child processes.
- **Visible and steerable.** A live terminal dashboard shows agents, tickets,
  timing, comments, warnings, and user-attention requests.
- **Failure-aware.** Unexpected exits and timeouts safely reopen work instead of
  pretending it completed.
- **Automation-friendly.** `--once`, `--drain`, and `--check` make the same
  workflow useful in scripts and CI.
- **Small by design.** One dependency-free .NET console application shells out
  to documented CLI contracts.

## Start here

Choose the path that matches your repository:

| I want to… | Start with |
| --- | --- |
| See the complete setup visually | [Interactive quick-start guide](docs/quick-start.html) |
| Create a new multi-agent project | [Generated project walkthrough](docs/getting-started.md#path-a-create-a-new-multi-agent-project) |
| Add Abacus to an existing repository | [Existing repository walkthrough](docs/getting-started.md#path-b-use-an-existing-repository) |
| Understand every option | [CLI reference](docs/cli-reference.md) |
| Run and troubleshoot agent pools | [Operations guide](docs/operations.md) |
| Manage or recover shared Beads storage | [Shared Dolt operations](docs/shared-dolt.md) |
| Understand the safety model | [Architecture and boundaries](docs/architecture.md) |

The [documentation index](docs/README.md) maps the rest of the project docs.

## Quickest path: a new multi-agent project

First [build Abacus](#build-and-install), then run the standalone initializer
from the directory that should contain your new project:

```sh
abacus --init-new-multi-agent-repo my-project 4
```

It creates a Git repository, a shared-server Beads database, four detached
worktrees, the bundled skills, and ready-to-use launch scripts:

```text
my-project/
├── repo/                     # main checkout and shared Beads project
├── worktrees/{0,1,2,3}/      # disposable agent workspaces
├── run_abacus_opencode.sh
├── run_abacus_codex.sh
└── run_abacus_claude.sh
```

Create some ready Beads issues, start tmux, and launch the pool:

```sh
cd my-project/repo
bd create "Add the first feature" \
  --description "Describe the work and relevant context." \
  --acceptance "State the observable definition of done." \
  --json

cd ..
tmux new-session -d -s my-project
./run_abacus_codex.sh gpt-5.6-sol high
```

The initializer is the **only** Abacus operation that creates repositories,
worktrees, or Beads configuration. Normal orchestration expects those resources
to exist already. See the [generated project walkthrough](docs/getting-started.md#path-a-create-a-new-multi-agent-project)
for launcher overrides and the complete setup contract.

## Agent modes

Abacus supports exactly four execution modes:

| Mode | Hosting | Model format | Notes |
| --- | --- | --- | --- |
| `opencode` | Interactive tmux pane | `provider/model` | Default mode; uses OpenCode's configured or selected variant. |
| `codex` | Interactive tmux pane | Native Codex ID or alias | Receives `--effort` through Codex configuration. |
| `claude` | Interactive tmux pane | Native Claude ID or alias | Supports `--remote` for Claude Remote Control. |
| `opencode-server` | Direct process or tmux pane | `provider/model` | Attaches to an existing server and passes `--variant`. |

Example with two existing worktrees:

```sh
abacus --mode codex \
  --tmux-session work \
  --tmux-window agents \
  --tmux-layout tiled \
  --model gpt-5.6-terra \
  --effort high \
  -a alice /work/repo-a \
  -a bob /work/repo-b
```

Every local mode launches the full interactive agent interface with a real TTY.
Abacus does not substitute `codex exec` or `claude --print`.

## What happens to a ticket?

Each configured agent repeats one focused loop:

1. Clean its assigned workspace.
2. Find eligible ready work and atomically claim one issue.
3. Create or reuse `abacus/<issue-id>`.
4. Start the selected coding agent with the issue context and Git instructions.
5. Watch both the Beads status and the hosted process.
6. Stop the session when the issue becomes `closed`, `open`, or `blocked`.
7. Reopen work left `in_progress` by an unexpected exit or timeout.
8. Synchronize Beads when a Dolt remote is configured, then continue.

Explore the [visual agent-loop guide](docs/agent-loop-flow.html), or read the
[operations guide](docs/operations.md) for detailed recovery and shutdown rules.

## Commands at a glance

| Command | Purpose |
| --- | --- |
| `abacus --init-new-multi-agent-repo <name> <count>` | Create a complete new multi-agent project layout. |
| `abacus --install-skills` | Install the four bundled agent skills into the current repository. |
| `abacus --health` | Report whether the current repository is ready. |
| `abacus --models` | List model IDs discoverable from installed harnesses. |
| `abacus --list-user-attention` | Print issue IDs that need a decision or outside action. |
| `abacus --resolve <id> [message] [--reopen]` | Respond to and clear an attention request. |
| `abacus --prune-closed-branches` | Remove local Abacus branches for closed issues. |
| `abacus [run options] -a <name> <workspace> [-a ...]` | Start one or more agent loops. |

Run `abacus --help` for the built-in summary and see the
[CLI reference](docs/cli-reference.md) for every mode and option.

## Prerequisites

Abacus targets macOS and Linux.

| Tool | Minimum supported version | When required |
| --- | --- | --- |
| .NET SDK | 10.0.101 | Building from source |
| Beads (`bd`) | 1.2.2 | Always |
| Git | 2.55.0 | Always |
| OpenCode | 1.18.20 | OpenCode modes |
| Codex CLI | 0.151.0 | Codex mode |
| Claude Code | 2.1.212 | Claude mode |
| tmux | 3.6a | Interactive modes; optional for OpenCode Server |

You need only the agent harness selected for a particular run. Before launching
agents, the repository must have Beads initialized with `no-git-ops=false`, and
each agent must have a unique workspace. Multiple agents must share one
reachable, server-backed Dolt database.

Use `abacus --health` to inspect most of these conditions before configuring a
run. See [Getting started](docs/getting-started.md) for exact setup commands.

## Build and install

Build and test:

```sh
dotnet test Abacus.sln
```

Publish a framework-dependent executable:

```sh
dotnet publish src/Abacus -c Release -o artifacts/publish
./artifacts/publish/abacus --help
```

Or publish a self-contained single file for one of `osx-arm64`, `osx-x64`,
`linux-x64`, or `linux-arm64`:

```sh
dotnet publish src/Abacus -c Release -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o artifacts/publish
```

Copy or symlink `artifacts/publish/abacus` to a directory on `PATH` if desired.

## Safety and ownership

Abacus owns orchestration—not your infrastructure or engineering decisions.

It **does** validate workspaces, claim tickets, prepare issue branches, launch
agents, monitor status, recover interrupted work, and report outcomes.

It **does not** start tmux or OpenCode servers, judge whether code is correct,
choose ticket outcomes, merge branches, push Git commits, or manage a dynamic
agent pool. Except for the standalone initializer, it also does not create
worktrees or configure Beads/Dolt.

The exact boundary is documented in
[Architecture and boundaries](docs/architecture.md#deliberate-boundaries).

## Project documentation

- [Getting started](docs/getting-started.md) — build, initialize, and launch
- [CLI reference](docs/cli-reference.md) — commands, modes, filters, and options
- [Operations guide](docs/operations.md) — dashboard, attention, recovery, and shutdown
- [Shared Dolt operations](docs/shared-dolt.md) — creation, migration, backup,
  troubleshooting, and rollback
- [Architecture and boundaries](docs/architecture.md) — design and safety model
- [Product specification](SPEC.md) — normative behavior and exact agent prompt
- [Implementation plan](PLAN.md) — phased design constraints and definition of done
- [External CLI contracts](docs/contracts/cli-contracts.md) — tested subprocess assumptions
- [Release smoke-test record](docs/smoke-test.md) — latest manual release evidence

For the full map, including visual guides and bundled skills, see
[`docs/README.md`](docs/README.md).
