# Abacus Product Specification

> **Document role:** Normative product specification. For task-oriented user
> documentation, start with the [README](README.md) or
> [documentation index](docs/README.md). The phased implementation design lives
> in [PLAN.md](PLAN.md).

## Contents

- [Setup](#setup)
- [Usage and command behavior](#usage)
- [Repository health](#repository-health)
- [Agent workflow](#agent-workflow)
- [Exact agent prompt](#agent-prompt-template)

Abacus is a simple agent orchestrator built on top of [Beads](https://github.com/gastownhall/beads).

It uses:

- Beads for task management
- OpenCode, Codex, or Claude Code for running agents
- tmux for managing interactive agent processes and optional pane-hosted OpenCode Server clients

## Setup

Create a brand-new multi-agent repository layout from the current directory:

```sh
abacus --init-new-multi-agent-repo <project-name> <agent-count>
```

This standalone operation must reject an existing `<project-name>` destination,
then create `<project-name>/repo`, initialize its `main` branch, configure a
unique shared-server Beads database with `no-git-ops=false`, local-only Dolt,
and a merge slot, install all bundled Abacus skills, and commit the initial
repository state. It then creates `<agent-count>` detached Git worktrees at
`<project-name>/worktrees/0` through `worktrees/<agent-count-1>`.
Beads initialization must be non-interactive and select the maintainer role.

The project root also receives executable `run_abacus_opencode.sh`,
`run_abacus_codex.sh`, and `run_abacus_claude.sh` launchers. Each launcher must
discover the worktree directories at run time and pass one uniquely named agent
per worktree to Abacus. Launchers accept model and effort overrides but do not
create the tmux session.

Before running Abacus:

1. Set up a Beads project in your Git repository.
2. For OpenCode, Codex, or Claude mode, start a tmux session and, optionally, the window where agent panes should run.
3. For OpenCode Server mode, start an OpenCode server. A tmux session is optional in this mode.

Install Abacus's bundled planning, issue-quality, attention-reporting, and
Git-instruction-audit skills from anywhere inside the target Git repository:

```sh
abacus --install-skills
```

This installs `.agents/skills/abacus-beads-planner`,
`.agents/skills/abacus-beads-doctor`,
`.agents/skills/abacus-beads-attention`, and
`.agents/skills/abacus-git-check` at the repository root. If any bundled
skill directory already exists, installation must name the affected skills and
require user confirmation before replacing their complete directories. A
declined or unavailable confirmation leaves every skill unchanged. Unrelated
skills are preserved. Skill installation is a standalone operation: it does not
require a model, agent, Beads project, tmux session, or agent CLI, and it does
not start the orchestrator.

### Agent workspaces

Each agent is assigned a Git workspace. This can be:

- The main repository directory
- A Git worktree
- A separate clone of the repository

Multiple agents must not use the same workspace directory.

### Beads database

A single agent can use a normal local Beads database.

Multiple agents must use the same shared Dolt database so task claims are atomic and immediately visible to every agent. Abacus should refuse to start multiple agents if the Beads project is not configured this way.

## Usage

Initialize a new multi-agent repository:

```sh
abacus --init-new-multi-agent-repo <project-name> <agent-count>
```

Install the bundled agent skills:

```sh
abacus --install-skills
```

Inspect whether the current repository is ready for Abacus:

```sh
abacus --health
```

List model IDs exposed by the installed agent harnesses:

```sh
abacus --models
```

Delete local Abacus branches whose corresponding tickets are closed:

```sh
abacus --prune-closed-branches
```

List ticket IDs that need user attention:

```sh
abacus --list-user-attention
```

Resolve a user-attention callout from the current Beads project:

```sh
abacus --resolve <issue-id> [<message>] [--reopen]
abacus -r <issue-id> [<message>] [--reopen]
```

Start agents in an existing tmux session:

```sh
abacus --tmux-session <session_name> \
  [--mode <opencode|codex|claude>] \
  [--tmux-window <window_name_or_index>] \
  [--tmux-layout <layout>] \
  --model <model> \
  [--effort <effort>] \
  [--remote] \
  [--label <label>] [--exclude-label <label>] \
  [--type <types>] [--priority <priority>] \
  [--ticket-timeout <duration>] \
  [--latest-comments <count>] \
  [--notify <off|attention|all>] [--notify-sound] \
  [--once | --drain | --check] \
  [--verbose] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--mode` defaults to `opencode`. `--model` is required. `--effort` defaults to `high` and accepts a nonempty provider-specific variant name without whitespace. OpenCode modes require a `provider/model` ID; Codex and Claude accept their native model IDs and aliases. Model and effort availability remain the selected CLI's responsibility. Interactive OpenCode is the exception: OpenCode 1.18.20's TUI entry point does not expose variant selection, so Abacus passes the model unchanged and OpenCode uses its configured or session-selected variant.

`--remote` is valid only with Claude Code. Abacus adds `--remote-control '<issue-id> • <issue-title>'` to the normal interactive command. Codex and both OpenCode modes reject the option.

Dispatch filters are optional and apply to every fresh or same-agent resumed ready claim. `--label` and `--exclude-label` are repeatable literal passthroughs to `bd ready`; `--type` accepts one literal Beads type filter, including comma-separated types; and `--priority` accepts priorities 0 through 4. Abacus always excludes `gt:slot` in addition to user filters. Beads priority remains the primary ordering. When multiple candidates share the highest available priority, Abacus prefers the candidate with the newest comment; if none of those candidates has a comment, it preserves the first candidate returned by Beads. Before claiming that candidate, Abacus checks its direct children and skips it when any child is not closed.

`--ticket-timeout` is an optional positive integer duration with an `s`, `m`, or `h` suffix. The guard starts when the agent CLI starts. At the limit, Abacus attempts to stop and clean the hosted agent run, reopens the ticket only if it is still `in_progress`, verifies the result, and pushes when a Dolt remote is configured. A terminal ticket update that races with the timeout is preserved. Recovery or push failure stops that agent, keeps a persistent alert visible, and makes finite runs fail.

`--latest-comments` controls the number of recent Beads comments shown at the bottom of the interactive dashboard. It defaults to 8 and accepts integers from 1 through 100. Abacus refreshes the snapshot in the same periodic monitoring cycle as user-attention detection, using the read-only Beads export command after the attention query. Each entry has a header line containing the issue ID, a width-truncated issue title, and the comment author, followed by a width-truncated comment wrapped across at most two indented lines. Only headers are colored: red for issues labelled `abacus:needs-user-attention`, green for configured-agent authors, and cyan for unrecognized authors. Comment message lines use the terminal's default color.

`--notify` controls Abacus-owned desktop notifications and defaults to `off`. `attention` reports newly observed `abacus:needs-user-attention` issues, blocked tickets, and persistent recovery failures. `all` additionally reports every ticket outcome and the final run summary. On macOS Abacus uses `osascript`; on Linux it uses `notify-send` when available. Notification delivery is best effort and never changes orchestration outcomes. `--notify-sound` uses a positive sound for closed tickets and runs with only closed outcomes, and a negative sound for attention, persistent failures, reopened, blocked, or interrupted outcomes and run summaries containing any of those outcomes. It permits a terminal bell fallback if desktop delivery is unavailable and requires `--notify attention` or `--notify all`.

`--resolve <issue-id> [<message>] [--reopen]` (short form `-r`) is a standalone operation. It runs
`bd update` in the current directory to remove
`abacus:needs-user-attention`. When the optional message is present, it first
uses `bd comment` to add the exact supplied message. The label is not removed if
the comment fails. When `--reopen` is present, the same update that removes the
label also sets the issue status to `open` and clears its assignee so it can be
claimed again. On success it prints a concise confirmation instead of the
raw Beads JSON. It does not run agent preflight or require normal run options.
The command fails if Beads cannot perform any requested operation.

`--list-user-attention` is a standalone, read-only operation. It queries every
issue carrying `abacus:needs-user-attention`, including closed issues, and
prints only issue IDs, one per line. It does not run agent preflight or require
normal run options.

`--prune-closed-branches` is a standalone repository-maintenance operation. It
queries all closed Beads tickets and force-deletes matching local
`abacus/<issue-id>` branches from the current Git repository. It does not touch
non-Abacus branches or remote refs. A matching branch checked out in any
worktree is skipped and reported rather than causing the rest of the prune to
fail. It does not run agent preflight or require normal run options.

`--models` is a standalone, read-only operation. It invokes `opencode models`
and `codex debug models` when those harnesses are installed, then prints their
available model IDs in separate groups. A missing harness or failed catalog is
reported within its group without suppressing successful groups. Claude Code
does not expose non-interactive model discovery, so an installed Claude CLI is
reported with guidance to use its interactive `/model` picker. The command
exits zero when at least one model ID was discovered and one otherwise. It does
not require a Git repository, Beads project, model, agent, or tmux session.

Each local agent runs interactively in its own tmux pane using its assigned Git workspace and requested model:

- OpenCode: `opencode --prompt <prompt> --model <provider/model>`
- Codex: `codex --cd <workspace> --model <model> --config model_reasoning_effort=<effort> --approve-for-me <prompt>`
- Claude Code: `claude --model <model> --effort <effort> --permission-mode auto --name <agent-ticket> [--remote-control <issue-ticket>] <prompt>`

These commands deliberately start the interactive interfaces rather than Codex `exec` or Claude `--print`. Codex and Claude use their automatic permission reviewers so ordinary approvals do not block an unattended Abacus pane while actions still receive background safety checks.

Each pane has a stable `<agent> • <issue-id>` tmux title that the child process cannot replace. The selected agent CLI remains connected directly to the pane terminal so it has a TTY. When `--tmux-window` is supplied, Abacus verifies that the existing window belongs to the requested session and creates every agent pane there. Without it, tmux targets the session's current window. `--tmux-layout` is optional and reapplies a supported built-in layout (`even-horizontal`, `even-vertical`, `main-horizontal`, `main-vertical`, or `tiled`) to that target after each pane is spawned.

Tmux shutdown is deliberately best effort. Abacus sends Ctrl-C to the recorded
pane, waits briefly, attempts `kill-pane`, removes its temporary run files, and
continues finalization regardless of tmux command output or pane-verification
races. Cleanup never targets a pane other than the ID Abacus recorded at launch.

To connect the agents to an existing OpenCode server:

```sh
abacus --mode opencode-server --model <provider/model> --effort <effort> \
  --opencode-server 127.0.0.1:1234 \
  [--once | --drain | --check] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

Without `--tmux-session`, each agent starts as a directly supervised, non-interactive `opencode run --attach` child process connected to the specified server. Direct processes receive their own workspace, prompt, model, and `BEADS_ACTOR`; Abacus drains their output so it does not corrupt the dashboard and stops them when supervision ends. tmux is not looked up or required in this mode.

For backward compatibility, supplying `--opencode-server` without `--mode` implies `opencode-server`. Supplying both `--opencode-server` and `--tmux-session` keeps the pane-hosted attached behavior: each client runs in a separate tmux pane. `--tmux-window` and `--tmux-layout` remain valid only with `--tmux-session`. The server option is rejected for all other explicit modes.

By default, Abacus displays a live terminal dashboard with one row per agent, showing whether each agent is starting, paused, waiting, idle, syncing, cleaning or preparing a workspace, working on a ticket, finalizing, recovering, retrying, or stopped. Active rows include the ticket ID and title, time in the current state, process or pane location, retry count, and most recently observed exit code when available. The dashboard starts with new ticket claims enabled. Pressing Shift-Tab toggles new claims on or off for all agents; pausing does not interrupt tickets that are already active. The header shows the current claim state, and agents waiting for permission display a paused state. Issues labelled `abacus:needs-user-attention`, including closed issues, appear in a persistent alert containing their IDs and titles until the label is removed. A periodically refreshed latest-comments log appears at the bottom with the configured number of issue, author, and comment entries. Warnings remain visible in the dashboard, and idle states are visually distinct from failures. `--verbose` (also accepted as `--debug` or `-v`) replaces the dashboard with timestamped state transitions, warnings, alerts, and every external command Abacus runs. When standard error is redirected, the default mode emits compact state transitions rather than terminal control sequences. Before starting any agent loop, Abacus pulls once when a single configured agent has a Dolt remote, then records the current Dolt `HEAD` with read-only `bd vc status`. Shared multi-agent databases are already live and are not pulled. On shutdown, Abacus prints that initial full Dolt commit in the final summary alongside elapsed time and per-agent counts for closed, reopened, blocked, and interrupted tickets.

Abacus runs continuously unless a finite execution option is selected. `--once` makes each agent claim and process at most one currently ready ticket; an agent exits immediately when no ticket is ready. `--drain` lets each agent continue claiming tickets until it observes no ready work, then exits after any active ticket finishes. Finite options fail rather than retrying orchestration errors forever, making them suitable for CI and scripts. `--check` runs the complete non-mutating preflight and exits without cleaning workspaces, claiming tickets, creating panes or processes, or printing a run summary. It validates the selected agent executable, workspace, Beads `no-git-ops` setting, and Dolt configuration, the OpenCode server address when applicable, and any requested tmux session/window target. These three options are mutually exclusive.

### Repository health

`--health` is a standalone, read-only diagnostic. From the current Git
repository it reports:

- whether Git and Beads meet their minimum versions and Beads is initialized;
- whether Beads uses embedded single-writer storage or a reachable shared,
  server-backed Dolt database suitable for multiple agents;
- whether Beads `no-git-ops` is disabled; when enabled, health reports that
  Abacus cannot run and provides `bd config set no-git-ops false` as the fix;
- whether a Beads merge slot exists and, when occupied, who holds it; a missing
  slot warns that cross-workspace or cross-machine merge synchronization may be
  unsafe unless another serialized merge process is configured;
- which of OpenCode, Claude Code, and Codex are installed at supported versions,
  requiring at least one supported harness;
- whether tmux meets its minimum version and therefore enables pane-hosted modes;
- every worktree referenced by `git worktree list --porcelain`, with an explicit
  warning when there is no additional linked worktree;
- whether all bundled skills are present under `.agents/skills`; and
- the resulting available agent modes plus single- and multi-agent readiness.

Direct OpenCode Server mode does not require tmux, but health does not attempt to
find or contact a server. Multi-agent readiness requires a reachable shared
Beads database and more than one referenced Git worktree. Separate clones may
also provide distinct workspaces, but health deliberately does not search for
them. Merge-slot availability is advisory because repositories may serialize
merges another way. The command exits zero when at least one single-agent mode is
runnable, `no-git-ops` is disabled, and all bundled skills are installed;
otherwise it exits one.

## Agent workflow

Before starting the agent loops, fail preflight without claiming work or starting
an agent when Beads `no-git-ops` is enabled, and report
`bd config set no-git-ops false` as the correction. Otherwise, pull once when
exactly one configured agent has
a Dolt remote, then record the current Dolt commit. Abort before claims if
either operation fails. For multiple agents, record the shared server's current
commit without pulling it.

Each Abacus agent follows this loop:

1. Before claiming work, discard tracked and untracked non-ignored workspace changes with `git reset --hard HEAD` and `git clean -fd`.
2. In single-agent mode, pull the latest Beads data if a remote is configured. Agents using a shared database already see the latest data.
3. Abacus lists every unassigned ready task using the agent name as the actor:

   ```sh
   BEADS_ACTOR=<agent_name> bd ready --unassigned --exclude-label gt:slot --limit 0 --json
   ```

   When dispatch filters are configured, their literal `--label`, `--exclude-label`, `--type`, and `--priority` arguments are added before `--limit 0 --json`. The same filters apply when resuming ready work already assigned to that agent. Abacus keeps the priority ordering from Beads. If the highest-priority group contains multiple issues, it reads comments for the commented candidates using `bd show <ids...> --include-comments --json`, selects the issue with the newest comment, or keeps the first issue when none has a comment. Before claiming the selected issue, it runs `bd show <id> --children --json`. A candidate with any child whose status is not `closed` is skipped and selection continues with the remaining candidates. A failed or malformed child lookup fails safely without claiming the candidate. Abacus then atomically claims the eligible issue with `bd update <id> --claim --json`. If another agent wins that claim race, Abacus refreshes the candidates and tries again.

4. Create or check out an `abacus/<issue_id>` branch in the assigned workspace.
5. Make sure the workspace has no local changes before starting the agent CLI.
6. Start the selected local agent CLI interactively in tmux, or start an attached OpenCode Server client either directly or in tmux. In every mode, set `BEADS_ACTOR=<agent_name>` and pass the requested model and a prompt describing the issue and its ticket-state responsibilities. Pass the requested effort where the selected CLI exposes it; interactive OpenCode uses its configured or session-selected variant because its TUI has no variant CLI option.
7. While the agent CLI is running, Abacus monitors the ticket status through Beads and enforces the optional ticket runtime limit.
8. The coding agent does the work and changes the ticket status when it is finished:

   - Close it after the work has been completed and merged.
   - Return it to `open` if the work should be retried by another agent.
   - Mark it `blocked` if it cannot continue without outside help.

9. Changing the ticket from `in_progress` signals that the agent session is finished. Abacus stops the selected CLI process.
10. Whenever an agent process ends, Abacus runs `bd dolt push` if a remote is configured. This ensures the final ticket update is pushed even if the agent did not push it.
11. In continuous and drain modes, Abacus returns to the start of the loop. Once mode exits that agent after its first ticket; drain mode exits it when no further ticket is ready.

An unexpected agent CLI exit that leaves the ticket `in_progress` must not be treated as completed work.
If Abacus sees that the ticket is still `in_progress` after the agent exits, it logs a warning, reopens the ticket (along with a `bd dolt push` if applicable), and returns to the start of the loop.
The ticket-timeout path uses the same verified reopen and push behavior after stopping the hosted agent run.

## Agent prompt template

```text
You are <agent_name>, working on Beads ticket <issue_id> in <workspace_path>.

Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
name. Do not claim another ticket.

Abacus grants you authority to perform the local Git operations needed for this
ticket, including staging, committing, and merging into the local main branch.
You do not have authority to push; do not run `git push`. If `bd prime` says
there is no Git authority, this explicit Abacus instruction overrides that.
Follow any more restrictive user or repository instruction.

Read the ticket with:

  bd show <issue_id> --json

Work on the branch abacus/<issue_id> and satisfy the ticket's definition of done.
Commit your changes, then merge the branch into the latest local main branch.

Follow any repository-specific merge instructions when they define a merge process.
Otherwise, use this basic merge strategy:

1. Check for a Beads merge slot with `bd merge-slot check --json`. If the response
   reports that no merge slot exists, continue without one; do not create one. If a
   slot exists, acquire it before merging, waiting and retrying while another agent
   holds it:

     until bd merge-slot acquire --holder "$BEADS_ACTOR"; do sleep 2; done

2. While holding the merge slot when one is configured, merge the latest local
   `main` into the issue branch. Resolve any conflicts and commit the result.
3. Locate the worktree where `main` is checked out with
   `git worktree list --porcelain`, then fast-forward it to the issue branch with
   `git -C <main-worktree> merge --ff-only <issue-branch>`. If `main` is not checked
   out elsewhere, switch this workspace to `main` and fast-forward it there.
4. If you acquired a merge slot, release it with
   `bd merge-slot release --holder "$BEADS_ACTOR"`. Always release it, including
   when the merge fails. Only close the ticket after the merge and release succeed.

You might not be the first agent to work on this ticket, there might be commits
in this branch that are already contributing to the ticket. Make sure you
understand the current state of the branch before you make changes. If you think
the original commits are incorrect, you can fix/remove them.

If the issue needs user awareness, a decision, or outside action, bring it to the
user's attention with:

  bd comment <issue_id> "<decision or action needed>"
  bd update <issue_id> --add-label abacus:needs-user-attention --json

Continue working when possible. If work cannot continue, also mark the issue
blocked below. If user attention is no longer needed, remove the alert with:

  bd comment <issue_id> "<why user attention is no longer needed>"
  bd update <issue_id> --remove-label abacus:needs-user-attention --json

When you are completely finished, add a summary of what you did as a comment:

  bd comment <issue_id> "CLOSED/BLOCKED/REOPENED/etc: <summary of completed work>"

If your work introduces important things for other agents to remember before they start new tasks, add them to memory:

  bd remember "<thing to remember>"

But use memory sparingly; it is not a substitute for good documentation in the repository.

Then finally update the ticket:

- Success:
    bd close <issue_id> --reason "CLOSED: <summary of completed work>" --json
- Work should be retried:
    bd update <issue_id> --status open --assignee "" --append-notes "REOPENED: <reason>" --json
- Work is blocked:
    bd update <issue_id> --status blocked --append-notes "BLOCKED: <blocker>" --json

If you need to set the status of the ticket to anything other than closed, assess if your current local
changes need to be committed or discarded. For example, if you just need to block the ticket to get some
user attention, you can commit your changes and then block the ticket. Eventually an agent will come back
to the ticket and continue working on it.

Changing the ticket from in_progress tells Abacus to end this session. Make the
status change one of your final actions, after all code, commits, merges, and
ticket updates are complete.
```
