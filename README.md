# Abacus

Abacus is a small Unix-oriented C# orchestrator for [Beads](https://github.com/gastownhall/beads), Git, [OpenCode](https://github.com/anomalyco/opencode), and optional tmux process hosting. It owns only the agent state machine and invokes each existing command-line tool with literal argument lists.

## Prerequisites

Abacus targets macOS and Linux. These commands must be on `PATH`:

| Tool | Minimum supported version |
| --- | --- |
| .NET SDK | 10.0.101 |
| Beads (`bd`) | 1.2.2 |
| Git | 2.55.0 |
| OpenCode | 1.18.20 |
| tmux | 3.6a (local Mini and optional pane-hosted attached mode) |

Before running Abacus:

1. Initialize Beads in every assigned Git workspace.
2. Make every workspace clean and give each agent a distinct worktree or clone.
3. For local Mini mode, start the named tmux session and optionally select a specific window.
4. For attached mode, start an OpenCode server. tmux is optional.

For multiple agents, every workspace must connect to the same server-backed Dolt database. Abacus reads `bd dolt show --json` in each workspace, rejects embedded/local storage, and requires equal normalized host, port, and database identities. A single agent may use embedded Dolt storage. Remote presence is discovered with `bd dolt remote list --json`.

## Build and install

Build and test:

```sh
dotnet test Abacus.sln
```

Create a framework-dependent executable:

```sh
dotnet publish src/Abacus -c Release -o artifacts/publish
./artifacts/publish/abacus --help
```

Create a self-contained, single-file executable by selecting the runtime identifier for the destination (`osx-arm64`, `osx-x64`, `linux-x64`, or `linux-arm64`):

```sh
dotnet publish src/Abacus -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true \
  -o artifacts/publish
./artifacts/publish/abacus --help
```

## Single-agent walkthrough

The walkthroughs below assume `abacus` is installed on `PATH`. If you are running the executable directly from this repository, replace `abacus` with `/path/to/abacus/artifacts/publish/abacus`.

### 1. Initialize Beads in a Git repository

Choose the repository, agent name, tmux session and window, and OpenCode model. `MODEL` must be an exact model ID reported by `opencode models`.

```sh
export REPO=/path/to/your/repository
export AGENT=alice
export SESSION=abacus-work
export WINDOW=agents
export MODEL=provider/model

opencode models
cd "$REPO"
git rev-parse --show-toplevel
```

Initialize Beads. `--init-if-missing` makes this safe to repeat when the repository is already initialized.

```sh
bd init --init-if-missing --non-interactive
bd dolt show --json
bd dolt remote list --json
```

Make sure the repository's agent instructions define how an issue branch is serialized and merged into `main`. Abacus tells the agent to follow that process, but does not perform the merge itself.

Create at least one ready ticket. Give it enough detail for the OpenCode agent to complete without further input.

```sh
bd create "Add a hello-world file" \
  --description "Create HELLO.md with a short hello-world message, test or verify the change, commit it, and use the repository's serialized merge process." \
  --acceptance "HELLO.md is committed and merged into main." \
  --json

bd ready --json
```

Abacus treats each assigned workspace as disposable agent state. Before every claim it automatically runs `git reset --hard HEAD` and `git clean -fd`, discarding tracked changes and untracked non-ignored files and directories. Inspect the workspace before starting Abacus if there is anything you may want to keep:

```sh
git status --porcelain
```

If this prints files you want to preserve, commit or move them before starting Abacus. Ignored files are not removed because cleanup does not use `git clean -x`.

### 2. Start the tmux session

Abacus requires an existing session and will not create one. It can also target a specific existing window by name or index. Start both together so the current terminal remains available:

```sh
tmux new-session -d -s "$SESSION" -n "$WINDOW"
tmux has-session -t "$SESSION"
tmux display-message -p -t "$SESSION:$WINDOW" '#{window_id}'
```

You can inspect the session at any time, then detach with `Ctrl-b d`:

```sh
tmux attach-session -t "$SESSION"
```

### 3. Run Abacus with one local OpenCode agent

From any terminal, start Abacus with the repository as the agent's workspace:

```sh
abacus \
  --tmux-session "$SESSION" \
  --tmux-window "$WINDOW" \
  --tmux-layout tiled \
  --model "$MODEL" \
  -a "$AGENT" "$REPO"
```

Abacus will claim ready tickets, create or reuse `abacus/<issue-id>`, and launch `opencode --mini` with the ticket prompt in an Abacus-owned pane. OpenCode receives the pane's terminal directly so Mini has a TTY. Abacus returns to polling after each ticket reaches `closed`, `open`, or `blocked`.

The default terminal display is a live dashboard with one row per agent. It shows the current lifecycle state (`STARTING`, `WAITING`, `IDLE`, `SYNCING`, `CLEANING`, `PREPARING`, `WORKING`, `FINALIZING`, `RECOVERING`, `RETRYING`, or `STOPPED`), elapsed time in that state, the active ticket ID and title, pane or process location, retry count, last observed exit code, and recent warnings. Idle polling is distinct from error retries. For raw diagnostics, add `--verbose` (or `--debug`/`-v`):

```sh
abacus --verbose \
  --tmux-session "$SESSION" \
  --tmux-window "$WINDOW" \
  --model "$MODEL" \
  -a "$AGENT" "$REPO"
```

Verbose mode prints timestamped state transitions, warnings, and every external command. If stderr is redirected to a file or pipe without `--verbose`, Abacus automatically uses compact state-transition lines instead of ANSI terminal control sequences. Set `NO_COLOR=1` to disable dashboard colors while retaining the live layout. When Abacus stops, it prints elapsed run time and per-agent counts for closed, reopened, blocked, and interrupted tickets.

Useful commands from another terminal are:

```sh
cd "$REPO"
bd list --all
bd show <issue-id> --json
tmux list-panes -t "$SESSION"
```

Press `Ctrl-C` in the Abacus terminal to stop it cleanly. Abacus interrupts its active pane and attempts to reopen any ticket that is still `in_progress`. When finished with the tmux session, remove it yourself:

```sh
tmux kill-session -t "$SESSION"
```

## Single-agent walkthrough with an OpenCode server and no tmux

The repository, Beads ticket, and clean-workspace check are identical to the preceding walkthrough. Start the server yourself and pass its address to Abacus; no tmux session is needed.

### 1. Start the OpenCode server

In a dedicated terminal, run:

```sh
opencode serve --hostname 127.0.0.1 --port 4096
```

Leave that command running. Its output should report that the server is listening on `http://127.0.0.1:4096`.

### 2. Start Abacus

In another terminal:

```sh
export REPO=/path/to/your/repository
export AGENT=alice
export MODEL=provider/model

cd "$REPO"
bd ready --json
git status --porcelain

abacus \
  --model "$MODEL" \
  --opencode-server 127.0.0.1:4096 \
  -a "$AGENT" "$REPO"
```

The value passed to `--opencode-server` is `host:port`, without an `http://` prefix. Abacus normalizes it and launches each agent through:

```sh
opencode run <prompt> \
  --model "$MODEL" \
  --attach http://127.0.0.1:4096 \
  --dir "$REPO"
```

Abacus launches one directly supervised `opencode run --attach` child per active agent. Each child has its own workspace, prompt, model, and `BEADS_ACTOR`. Its output is drained so the Abacus dashboard stays intact. Abacus does not start, stop, or directly query the server. Stop Abacus with `Ctrl-C`, then stop the OpenCode server with `Ctrl-C` in its terminal.

If you prefer pane-hosted attached clients, also pass `--tmux-session` and optionally `--tmux-window` and `--tmux-layout`; Abacus then uses the existing tmux behavior.

## Usage

Start agents in an existing tmux session:

```sh
abacus --tmux-session <session_name> \
  --tmux-window <window_name_or_index> \
  --tmux-layout <layout> \
  --model <provider/model> \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

Connect new OpenCode client sessions to an existing server:

```sh
abacus --model <provider/model> \
  --opencode-server 127.0.0.1:1234 \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--model` is required and must use OpenCode's `provider/model` form. Abacus passes that exact value to local Mini and attached run processes. Local mode requires `--tmux-session`. With `--opencode-server`, tmux is optional: omit `--tmux-session` for directly supervised child processes, or include it for pane-hosted attached clients. `--tmux-window` accepts a window name or index and is valid only with `--tmux-session`; when omitted, panes use that session's current window. `--tmux-layout` is also tmux-only and accepts `even-horizontal`, `even-vertical`, `main-horizontal`, `main-vertical`, or `tiled`; Abacus reapplies it after each pane is spawned. `--opencode-server` accepts `host:port`; Abacus normalizes it to an HTTP URL and still uses only `opencode run --attach`, never the server API.

Output has two levels: the default live agent dashboard and `--verbose` debug output. `--debug` and `-v` are aliases for `--verbose`.

Run `abacus --help` for the short prerequisite list and examples.

## Agent and branch behavior

Each agent has one asynchronous loop:

1. In single-agent mode, pull Dolt before claiming when a remote exists.
2. If the workspace is dirty, discard tracked and untracked non-ignored changes with `git reset --hard HEAD` and `git clean -fd` before claiming.
3. Atomically claim with `BEADS_ACTOR=<agent> bd ready --claim --exclude-label gt:slot --json`. If an older run left ready work assigned to that same agent, reclaim it without taking another agent's work. Merge-slot beads are never dispatched as coding work.
4. Create or reuse `abacus/<issue-id>` and verify the workspace is clean.
5. Start local `opencode --mini --prompt ...` in a dedicated Abacus-owned tmux pane. Start attached `opencode run --attach ...` in a pane when tmux was supplied, otherwise as a directly supervised child process.
6. Watch both the Beads status and the hosted OpenCode run for exit.
7. Stop and clean the pane or direct process when the ticket becomes `closed`, `open`, or `blocked`.
8. Reopen tickets left `in_progress` by an unexpected exit, push when configured, and continue waiting.

Every OpenCode session receives this exact prompt after substituting the agent name, issue ID, and canonical workspace path:

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

The implementation is in [`Prompt.cs`](src/Abacus/Prompt.cs) and the source contract is in [`SPEC.md`](SPEC.md#opencode-prompt-template). Repository-specific agent instructions must define the serialized merge process named in the prompt.

In default mode, agent states and recent warnings are shown without subprocess noise. In verbose mode, every external command is logged concisely to stderr with a timestamp and agent prefix. Pane-hosted prompt, wrapper, and marker files live under a per-process directory in the system temporary directory and are removed after a run. OpenCode output is displayed directly in tmux for pane-hosted runs; direct attached-process output is drained to preserve the dashboard.

Ctrl-C cancels all loops. Abacus interrupts every active pane or direct process, checks the ticket again, attempts to reopen any ticket still `in_progress` with a shutdown note, performs configured Dolt pushes with bounded retries, and removes only runs it created.

## Deliberate boundaries

Abacus does **not**:

- create, delete, or repair Git worktrees or clones;
- initialize or configure Beads, Dolt databases, or remotes;
- create or start the requested tmux session or window;
- start or manage an OpenCode server;
- merge branches or decide whether agent work is correct;
- choose ticket outcomes for an agent;
- integrate directly with Git, tmux, Dolt, Beads, or OpenCode APIs/protocols;
- provide a daemon, web dashboard, persistent queue, dynamic pool, or Windows support.

The observed external command contracts and fixtures are documented in [`docs/contracts/cli-contracts.md`](docs/contracts/cli-contracts.md).
