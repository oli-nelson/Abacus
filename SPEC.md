# Abacus

Abacus is a simple agent orchestrator built on top of [Beads](https://github.com/gastownhall/beads).

It uses:

- Beads for task management
- OpenCode, Codex, or Claude Code for running agents
- tmux for managing interactive agent processes and optional pane-hosted OpenCode Server clients

## Setup

Before running Abacus:

1. Set up a Beads project in your Git repository.
2. For OpenCode, Codex, or Claude mode, start a tmux session and, optionally, the window where agent panes should run.
3. For OpenCode Server mode, start an OpenCode server. A tmux session is optional in this mode.

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
  [--once | --drain | --check] \
  [--verbose] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--mode` defaults to `opencode`. `--model` is required. `--effort` defaults to `high` and accepts a nonempty provider-specific variant name without whitespace. OpenCode modes require a `provider/model` ID; Codex and Claude accept their native model IDs and aliases. Model and effort availability remain the selected CLI's responsibility. Interactive OpenCode is the exception: OpenCode 1.18.20's TUI entry point does not expose variant selection, so Abacus passes the model unchanged and OpenCode uses its configured or session-selected variant.

`--remote` is valid only with Claude Code. Abacus adds `--remote-control '<issue-id> • <issue-title>'` to the normal interactive command. Codex and both OpenCode modes reject the option.

Dispatch filters are optional and apply to every fresh or same-agent resumed ready claim. `--label` and `--exclude-label` are repeatable literal passthroughs to `bd ready`; `--type` accepts one literal Beads type filter, including comma-separated types; and `--priority` accepts priorities 0 through 4. Abacus always excludes `gt:slot` in addition to user filters.

`--ticket-timeout` is an optional positive integer duration with an `s`, `m`, or `h` suffix. The guard starts when the agent CLI starts. At the limit, Abacus stops and cleans the hosted agent run, reopens the ticket only if it is still `in_progress`, verifies the result, and pushes when a Dolt remote is configured. A terminal ticket update that races with the timeout is preserved. Recovery or push failure stops that agent, keeps a persistent alert visible, and makes finite runs fail.

Each local agent runs interactively in its own tmux pane using its assigned Git workspace and requested model:

- OpenCode: `opencode --prompt <prompt> --model <provider/model>`
- Codex: `codex --cd <workspace> --model <model> --config model_reasoning_effort=<effort> --approve-for-me <prompt>`
- Claude Code: `claude --model <model> --effort <effort> --permission-mode auto --name <agent-ticket> [--remote-control <issue-ticket>] <prompt>`

These commands deliberately start the interactive interfaces rather than Codex `exec` or Claude `--print`. Codex and Claude use their automatic permission reviewers so ordinary approvals do not block an unattended Abacus pane while actions still receive background safety checks.

Each pane has a stable `<agent> • <issue-id>` tmux title that the child process cannot replace. The selected agent CLI remains connected directly to the pane terminal so it has a TTY. When `--tmux-window` is supplied, Abacus verifies that the existing window belongs to the requested session and creates every agent pane there. Without it, tmux targets the session's current window. `--tmux-layout` is optional and reapplies a supported built-in layout (`even-horizontal`, `even-vertical`, `main-horizontal`, `main-vertical`, or `tiled`) to that target after each pane is spawned.

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

By default, Abacus displays a live terminal dashboard with one row per agent, showing whether each agent is starting, waiting, idle, syncing, cleaning or preparing a workspace, working on a ticket, finalizing, recovering, retrying, or stopped. Active rows include the ticket ID and title, time in the current state, process or pane location, retry count, and most recently observed exit code when available. Issues labelled `abacus:needs-user-attention`, including closed issues, appear in a persistent alert containing their IDs and titles until the label is removed. Warnings remain visible in the dashboard, and idle states are visually distinct from failures. `--verbose` (also accepted as `--debug` or `-v`) replaces the dashboard with timestamped state transitions, warnings, alerts, and every external command Abacus runs. When standard error is redirected, the default mode emits compact state transitions rather than terminal control sequences. On shutdown, Abacus prints a final per-agent run summary with elapsed time and counts for closed, reopened, blocked, and interrupted tickets.

Abacus runs continuously unless a finite execution option is selected. `--once` makes each agent claim and process at most one currently ready ticket; an agent exits immediately when no ticket is ready. `--drain` lets each agent continue claiming tickets until it observes no ready work, then exits after any active ticket finishes. Finite options fail rather than retrying orchestration errors forever, making them suitable for CI and scripts. `--check` runs the complete non-mutating preflight and exits without cleaning workspaces, claiming tickets, creating panes or processes, or printing a run summary. It validates the selected agent executable, workspace and Dolt configuration, the OpenCode server address when applicable, and any requested tmux session/window target. These three options are mutually exclusive.

## Agent workflow

Each Abacus agent follows this loop:

1. Before claiming work, discard tracked and untracked non-ignored workspace changes with `git reset --hard HEAD` and `git clean -fd`.
2. In single-agent mode, pull the latest Beads data if a remote is configured. Agents using a shared database already see the latest data.
3. Abacus atomically claims a ready task using the agent name as the actor:

   ```sh
   BEADS_ACTOR=<agent_name> bd ready --claim --exclude-label gt:slot --json
   ```

   When dispatch filters are configured, their literal `--label`, `--exclude-label`, `--type`, and `--priority` arguments are added before `--json`. The same filters apply when resuming ready work already assigned to that agent.

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
Read the ticket with:

  bd show <issue_id> --json

Work on the branch abacus/<issue_id> and satisfy the ticket's definition of done.
Commit your changes, then use the repository's serialized merge process to merge
the branch into the latest main branch.

If the issue needs user awareness, a decision, or outside action, bring it to the
user's attention with:

  bd update <issue_id> --add-label abacus:needs-user-attention --append-notes "<decision or action needed>" --json

Continue working when possible. If work cannot continue, also mark the issue
blocked below. If user attention is no longer needed, remove the alert with:

  bd update <issue_id> --remove-label abacus:needs-user-attention --json

When you are completely finished, add a summary of what you did to the ticket notes:

  bd update <issue_id> --append-notes "<summary of completed work>" --json

Then finally update the ticket:

- Success:
    bd close <issue_id> --reason "<summary of completed work>" --json
- Work should be retried:
    bd update <issue_id> --status open --assignee "" --append-notes "<reason>" --json
- Work is blocked:
    bd update <issue_id> --status blocked --append-notes "<blocker>" --json

Changing the ticket from in_progress tells Abacus to end this session. Make the
status change one of your final actions, after all code, commits, merges, and
ticket notes are complete.
```
