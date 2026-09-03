# Architecture and Boundaries

Abacus is intentionally an orchestrator, not an agent platform. Its job is to
connect durable work state, isolated source workspaces, and existing coding-agent
interfaces with the smallest practical amount of machinery.

## The five moving parts

```text
┌──────────────┐      ready / claim / status      ┌──────────────┐
│    Beads     │ ◄──────────────────────────────► │    Abacus    │
└──────────────┘                                  └──────┬───────┘
                                                         │ launch / supervise
                   ┌─────────────────────────────────────┼──────────────────┐
                   ▼                                     ▼                  ▼
              ┌─────────┐                          ┌───────────┐      ┌──────────┐
              │   Git   │                          │ tmux/TUI  │      │ OC server│
              │workspace│                          │   agent   │      │  client  │
              └─────────┘                          └───────────┘      └──────────┘
```

| Component | Owns |
| --- | --- |
| Beads | Issues, dependencies, readiness, atomic assignment, comments, status, and Dolt history |
| Git | Source branches, commits, worktrees, and merges |
| Agent harness | Reasoning, editing, testing, commits, merge execution, and final issue decision |
| tmux or direct host | The running agent process and terminal attachment |
| Abacus | Validation, dispatch, workspace preparation, supervision, recovery, and reporting |

This separation is the core safety model: Abacus observes explicit external
state instead of inventing its own source of truth.

## Runtime state machine

One asynchronous loop runs per configured agent:

```text
Waiting → Claimed → Preparing → Running → Finalizing → Waiting
                         │          │
                         └──────────┴────→ Recovery ──→ Waiting or Stopped
```

The loops share no scheduler or internal queue. Each one asks Beads for ready
work and uses `bd update <id> --claim` for atomic ownership. The complete visual
walkthrough is in [The Abacus agent loop](agent-loop-flow.html).

## Concurrency model

Parallel safety comes from two independent forms of isolation:

1. **Task isolation.** Atomic Beads claims prevent intentional duplicate
   assignment.
2. **Filesystem isolation.** Every agent receives a unique Git worktree or
   clone.

Multiple agents must connect to the same server-backed Dolt database. Abacus
normalizes and compares host, port, and database identity in every workspace.
Embedded storage remains valid for a single agent but is rejected for a pool
because it is a single-writer mode.

Abacus does not share in-memory assignments across loops or rely on timing to
avoid claim races. Beads remains authoritative.

## Hosting model

Interactive OpenCode, Codex, and Claude Code sessions run directly in dedicated
tmux panes with real TTYs. Abacus records each pane ID, applies a stable
`<agent> • <issue-id>` title, and cleans up only that pane.

OpenCode Server mode uses the supported `opencode run --attach` CLI. Without
tmux, Abacus directly supervises the child and drains its output to protect the
dashboard. With tmux, it uses the same pane lifecycle as interactive modes.

There is no tmux control protocol, HTTP integration, vendor SDK, or hidden
agent-server implementation.

## Command boundary

External commands are launched with .NET `ProcessStartInfo.ArgumentList`, so
workspace paths, issue IDs, prompts, and user values are passed literally rather
than interpolated into a shell command. A generated POSIX wrapper exists only
where tmux needs a pane command and atomic process-exit marker.

The observed commands and their success/failure behavior are recorded in
[External CLI contracts](contracts/cli-contracts.md).

## Agent prompt and authority

Abacus renders one built-in prompt with the agent name, issue ID, and canonical
workspace. It:

- says the issue is already claimed;
- grants local Git staging, commit, and merge authority;
- forbids `git push`;
- provides a merge-slot-aware fallback merge process;
- defines the user-attention label protocol; and
- makes the agent responsible for choosing `closed`, `open`, or `blocked`.

Repository-specific instructions may replace the default merge process or be
appended from the CLI and `.abacus/append-prompt.md`. More restrictive user or
repository instructions still win.

The exact prompt is maintained once, in
[`SPEC.md`](../SPEC.md#agent-prompt-template), and rendered by
[`Prompt.cs`](../src/Abacus/Prompt.cs).

## State and observability

Abacus keeps runtime state in memory. Temporary prompt, wrapper, and marker files
live under a per-process system-temporary directory and are removed after use.
It has no database, persistent queue, or durable scheduler.

Durable operational context belongs in Beads comments, labels, statuses, and
Dolt history. The dashboard is a view of that state plus live process state; it
is not another authority.

## Deliberate boundaries

During normal orchestration, Abacus does **not**:

- create, delete, or repair Git worktrees or clones;
- initialize or migrate Beads/Dolt databases and remotes;
- create or start a tmux session or window;
- start, stop, or directly query an OpenCode server;
- merge agent branches or decide whether their work is correct;
- choose a terminal ticket status for an agent;
- push Git commits;
- integrate with Git, tmux, Beads, Dolt, OpenCode, Codex, or Claude APIs and
  protocols beyond invoking supported CLI commands;
- provide a daemon, web dashboard, persistent queue, dynamic pool, or automatic
  scaling; or
- support Windows.

The standalone `--init-new-multi-agent-repo` command is the explicit setup
exception: it creates a brand-new repository, shared-server Beads configuration,
skills, worktrees, and launch scripts. It still does not create tmux sessions.

## Design constraints

These boundaries keep the first version understandable and auditable:

- one .NET console application and the standard library;
- four explicit agent modes instead of a plugin system;
- thin JSON extraction instead of imported vendor models;
- one loop per agent instead of a scheduler or message bus;
- fixed, bounded retries and cleanup deadlines; and
- clear failure over speculative repository or infrastructure repair.

See the [implementation plan](../PLAN.md) for the full set of simplicity rules
and the [product specification](../SPEC.md) for normative behavior.
