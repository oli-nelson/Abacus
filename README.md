# Abacus

Abacus is a small Unix-oriented C# orchestrator for [Beads](https://github.com/gastownhall/beads), Git, [OpenCode](https://github.com/anomalyco/opencode), and tmux. It owns only the agent state machine and invokes each existing command-line tool with literal argument lists.

## Prerequisites

Abacus targets macOS and Linux. These commands must be on `PATH`:

| Tool | Minimum supported version |
| --- | --- |
| .NET SDK | 10.0.101 |
| Beads (`bd`) | 1.2.2 |
| Git | 2.55.0 |
| OpenCode | 1.17.10 |
| tmux | 3.6a |

Before running Abacus:

1. Initialize Beads in every assigned Git workspace.
2. Make every workspace clean and give each agent a distinct worktree or clone.
3. Start the named tmux session.
4. Optionally start an OpenCode server for attached mode.

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

Choose the repository, agent name, tmux session, and OpenCode model. `MODEL` must be an exact model ID reported by `opencode models`.

```sh
export REPO=/path/to/your/repository
export AGENT=alice
export SESSION=abacus-work
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

Abacus refuses to start with local Git changes. Confirm this prints nothing:

```sh
git status --porcelain
```

If it prints files created by repository or Beads setup, review and commit those files according to the repository's normal policy, then run the cleanliness check again.

### 2. Start the tmux session

Abacus requires an existing session and will not create one. Start it detached so the current terminal remains available:

```sh
tmux new-session -d -s "$SESSION"
tmux has-session -t "$SESSION"
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
  --model "$MODEL" \
  -a "$AGENT" "$REPO"
```

Abacus will claim ready tickets, create or reuse `abacus/<issue-id>`, and launch a new local `opencode run` in an Abacus-owned pane. The pane shows the command's streaming output; it is not the interactive OpenCode TUI. Abacus returns to polling after each ticket reaches `closed`, `open`, or `blocked`.

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

## Single-agent walkthrough with an OpenCode server

The repository, Beads ticket, clean-workspace check, and tmux setup are identical to the preceding walkthrough. The difference is that you start the server yourself and pass its address to Abacus.

### 1. Start the OpenCode server

In a dedicated terminal, run:

```sh
opencode serve --hostname 127.0.0.1 --port 4096
```

Leave that command running. Its output should report that the server is listening on `http://127.0.0.1:4096`.

### 2. Start tmux and Abacus

In another terminal:

```sh
export REPO=/path/to/your/repository
export AGENT=alice
export SESSION=abacus-work
export MODEL=provider/model

cd "$REPO"
bd ready --json
git status --porcelain

tmux new-session -d -s "$SESSION"

abacus \
  --tmux-session "$SESSION" \
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

Abacus does not start, stop, or directly query the server. Stop Abacus with `Ctrl-C`, stop the OpenCode server with `Ctrl-C` in its terminal, and remove the tmux session when it is no longer needed:

```sh
tmux kill-session -t "$SESSION"
```

## Usage

Start agents in an existing tmux session:

```sh
abacus --tmux-session <session_name> \
  --model <provider/model> \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

Connect new OpenCode client sessions to an existing server:

```sh
abacus --tmux-session <session_name> \
  --model <provider/model> \
  --opencode-server 127.0.0.1:1234 \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--model` is required and must use OpenCode's `provider/model` form. Abacus passes that exact value to every `opencode run` process. `--opencode-server` accepts `host:port`; Abacus normalizes it to an HTTP URL and still uses only `opencode run --attach`, never the server API.

Run `abacus --help` for the short prerequisite list and examples.

## Agent and branch behavior

Each agent has one asynchronous loop:

1. In single-agent mode, pull Dolt before claiming when a remote exists.
2. Atomically claim with `BEADS_ACTOR=<agent> bd ready --claim --exclude-label gt:slot --json`. If an older run left ready work assigned to that same agent, reclaim it without taking another agent's work. Merge-slot beads are never dispatched as coding work.
3. Create or reuse `abacus/<issue-id>` and verify the workspace is clean.
4. Start `opencode run` in a dedicated Abacus-owned tmux pane.
5. Watch both the Beads status and the OpenCode exit marker.
6. Stop and clean the pane when the ticket becomes `closed`, `open`, or `blocked`.
7. Reopen tickets left `in_progress` by an unexpected exit, push when configured, and continue waiting.

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

Every external command is logged concisely to stderr with an agent prefix. Prompt, wrapper, and marker files live under a per-process directory in the system temporary directory and are removed after a run. A small `opencode.log` remains there only for failed or interrupted runs, and Abacus prints the retained directory at shutdown.

Ctrl-C cancels all loops. Abacus interrupts every active pane, checks the ticket again, attempts to reopen any ticket still `in_progress` with a shutdown note, performs configured Dolt pushes with bounded retries, and removes only panes it created.

## Deliberate boundaries

Abacus does **not**:

- create, delete, or repair Git worktrees or clones;
- initialize or configure Beads, Dolt databases, or remotes;
- create or start the requested tmux session;
- start or manage an OpenCode server;
- merge branches or decide whether agent work is correct;
- choose ticket outcomes for an agent;
- integrate directly with Git, tmux, Dolt, Beads, or OpenCode APIs/protocols;
- provide a daemon, dashboard, persistent queue, dynamic pool, or Windows support.

The observed external command contracts and fixtures are documented in [`docs/contracts/cli-contracts.md`](docs/contracts/cli-contracts.md).
