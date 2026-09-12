# Architecture and Boundaries

> **Ticket targets:** Configure the target allowlist and default destination.
> Missing ticket metadata is allowed unless `enforceTargetBranch` is enabled. See [target setup, audit, and recovery](targets.md).

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
work and uses `bd update <id> --claim` for atomic ownership. Target- and
reasoning-label-validation failures quarantine the claim before any harness starts;
ordinary recoverable failures use the reopen path. The visual
walkthrough is in [The Abacus agent loop](agent-loop-flow.html).

## Concurrency model

Parallel safety comes from two independent forms of isolation:

1. **Task isolation.** Atomic Beads claims prevent intentional duplicate
   assignment.
2. **Filesystem isolation.** Every agent receives a unique Git worktree or
   clone. OS-held workspace locks also exclude competing Abacus runs.

Multiple agents must connect to the same server-backed Dolt database. Abacus
normalizes and compares host, port, and database identity in every workspace.
Embedded storage remains valid for a single agent but is rejected for a pool
because it is a single-writer mode.

Abacus does not share in-memory assignments across loops or rely on timing to
avoid claim races. Beads remains authoritative.

After a claim wins, Abacus re-reads the ticket and resolves its optional
reasoning label through the run-local model mapping. The project-owned
`.abacus/reasoning.json` decides whether one label is mandatory. Model IDs stay
run-local because they depend on the selected harness and machine.

## Hosting model

Interactive OpenCode, Codex, and Claude Code sessions run directly in dedicated
tmux panes with real TTYs. Abacus resolves or creates a detached project session
and agent window, records each pane ID, applies pane-local ownership/project
tags and a stable `<agent> • <issue-id>` title, and reuses matching dead panes.
Only an implicit session created by the current invocation is lifetime-owned;
explicit, pre-existing, and disowned sessions persist.

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
workspace, and resolved bound destination. It:

- says the issue is already claimed;
- grants local Git staging, commit, and merge authority;
- forbids `git push`;
- provides a merge-slot-aware fallback merge process;
- requires harness-native waiting, forbids shell retry loops or other processes
  that would outlive the agent session, and reserves blocking for waits that
  look hopeless to resolve;
- defines the user-attention label protocol; and
- makes the agent responsible for choosing `closed`, `open`, or `blocked`.

Target-specific `mergeInstructions` files take precedence. Otherwise,
`<main-repo>/.abacus/merge-instructions.md` is the optional fallback, and the
built-in merge process applies when neither is present. Selected file contents
replace the complete default merge section; an empty file suppresses it.
Policies are snapshotted once at startup, not reread from agent checkouts. Other repository-specific instructions may be appended from the
CLI and `.abacus/append-prompt.md`. More restrictive user or repository
instructions still win.

The exact prompt is maintained once, in
[`SPEC.md`](../SPEC.md#agent-prompt-template), and rendered by
[`Prompt.cs`](../src/Abacus/Prompt.cs).

## State and observability

Abacus keeps transient runtime state in memory and durable execution bindings in Beads metadata. Temporary prompt, wrapper, and marker files
live under a per-process system-temporary directory and are removed after use.
It has no database, persistent queue, or durable scheduler.

Durable operational context belongs in Beads comments, labels, statuses, and
Dolt history. The dashboard is a view of that state plus live process state; it
is not another authority.

## Deliberate boundaries

During normal orchestration, Abacus does **not**:

- create, delete, or repair Git worktrees or clones;
- initialize or migrate Beads/Dolt databases and remotes;
- delete an explicitly named or pre-existing tmux session or window;
- start, stop, or directly query an OpenCode server;
- merge agent branches or decide whether their work is correct;
- decide successful delivery for an agent (it does block invalid target or
  reasoning-label claims and reopen unfinished claims during recovery);
- push Git commits;
- integrate with Git, tmux, Beads, Dolt, OpenCode, Codex, or Claude APIs and
  protocols beyond invoking supported CLI commands;
- provide a daemon, web dashboard, persistent queue, dynamic pool, or automatic
  scaling; or
- support Windows.

The standalone `new` command is the explicit setup
exception: it creates a brand-new repository, shared-server Beads configuration,
skills, worktrees, and JSON run configs (no shell launchers). It still does not create tmux sessions.
The standalone `init` handles existing Git/Beads repositories: validate setup,
install bundled skills with confirmation, and create a missing default targets
and reasoning config without changing Beads settings or Git branches.

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
