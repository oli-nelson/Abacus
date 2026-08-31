# Abacus

Abacus is a simple agent orchestrator built on top of [Beads](https://github.com/gastownhall/beads).

It uses:

- Beads for task management
- OpenCode for running agents
- tmux for managing local Mini processes and optional pane-hosted attached processes

## Setup

Before running Abacus:

1. Set up a Beads project in your Git repository.
2. For local Mini mode, start a tmux session and, optionally, the window where agent panes should run.
3. For attached mode, start an OpenCode server. A tmux session is optional in this mode.

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
  [--tmux-window <window_name_or_index>] \
  [--tmux-layout <layout>] \
  --model <provider/model> \
  [--verbose] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--model` is required. Its OpenCode model ID is used for every agent started by that Abacus instance.

Each local agent runs in its own tmux pane through `opencode --mini --prompt ...`, using its assigned Git workspace and the requested model. OpenCode must remain connected directly to the pane terminal so Mini has a TTY. When `--tmux-window` is supplied, Abacus verifies that the existing window belongs to the requested session and creates every agent pane there. Without it, tmux targets the session's current window as before. `--tmux-layout` is optional and reapplies a supported built-in layout (`even-horizontal`, `even-vertical`, `main-horizontal`, `main-vertical`, or `tiled`) to that target after each pane is spawned.

To connect the agents to an existing OpenCode server:

```sh
abacus --model <provider/model> \
  --opencode-server 127.0.0.1:1234 \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

Without `--tmux-session`, each agent starts as a directly supervised, non-interactive `opencode run --attach` child process connected to the specified server. Direct processes receive their own workspace, prompt, model, and `BEADS_ACTOR`; Abacus drains their output so it does not corrupt the dashboard and stops them when supervision ends. tmux is not looked up or required in this mode.

Supplying both `--opencode-server` and `--tmux-session` keeps the pane-hosted attached behavior: each client runs in a separate tmux pane. `--tmux-window` and `--tmux-layout` remain valid only with `--tmux-session`.

By default, Abacus displays a live terminal dashboard with one row per agent, showing whether each agent is starting, waiting, idle, syncing, cleaning or preparing a workspace, working on a ticket, finalizing, recovering, retrying, or stopped. Active rows include the ticket ID and title, time in the current state, process or pane location, retry count, and most recently observed exit code when available. Warnings remain visible in the dashboard, and idle states are visually distinct from failures. `--verbose` (also accepted as `--debug` or `-v`) replaces the dashboard with timestamped state transitions, warnings, and every external command Abacus runs. When standard error is redirected, the default mode emits compact state transitions rather than terminal control sequences. On shutdown, Abacus prints a final per-agent run summary with elapsed time and counts for closed, reopened, blocked, and interrupted tickets.

## Agent workflow

Each Abacus agent follows this loop:

1. Before claiming work, discard tracked and untracked non-ignored workspace changes with `git reset --hard HEAD` and `git clean -fd`.
2. In single-agent mode, pull the latest Beads data if a remote is configured. Agents using a shared database already see the latest data.
3. Abacus atomically claims a ready task using the agent name as the actor:

   ```sh
   BEADS_ACTOR=<agent_name> bd ready --claim --exclude-label gt:slot --json
   ```

4. Create or check out an `abacus/<issue_id>` branch in the assigned workspace.
5. Make sure the workspace has no local changes before starting OpenCode.
6. Start local OpenCode through `opencode --mini --prompt ...` in tmux. Start attached OpenCode through `opencode run --attach ...`, either directly when no tmux session was supplied or in tmux when one was. In every mode, set `BEADS_ACTOR=<agent_name>` and pass the required Abacus `--model` value and a prompt describing the issue and its ticket-state responsibilities.
7. While OpenCode is running, Abacus monitors the ticket status through Beads.
8. The OpenCode agent does the work and changes the ticket status when it is finished:

   - Close it after the work has been completed and merged.
   - Return it to `open` if the work should be retried by another agent.
   - Mark it `blocked` if it cannot continue without outside help.

9. Changing the ticket from `in_progress` signals that the OpenCode session is finished. Abacus stops the OpenCode process.
10. Whenever an OpenCode process ends, Abacus runs `bd dolt push` if a remote is configured. This ensures the final ticket update is pushed even if the agent did not push it.
11. Abacus returns to the start of the loop.

An unexpected OpenCode exit that leaves the ticket `in_progress` must not be treated as completed work.
If abacus sees that the ticket is still `in_progress` after OpenCode exits, it should log a warning, reopen the ticket (along with a `bd dolt push` if applicable) and return to the start of the loop.

## OpenCode prompt template

```text
You are <agent_name>, working on Beads ticket <issue_id> in <workspace_path>.

Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
name. Do not claim another ticket.
Read the ticket with:

  bd show <issue_id> --json

Work on the branch abacus/<issue_id> and satisfy the ticket's definition of done.
Commit your changes, then use the repository's serialized merge process to merge
the branch into the latest main branch.

When you are completely finished, update the ticket:

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
