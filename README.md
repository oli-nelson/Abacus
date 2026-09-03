# Abacus

Abacus is a small Unix-oriented C# orchestrator for [Beads](https://github.com/gastownhall/beads), Git, OpenCode, Codex, Claude Code, and optional tmux process hosting. It owns only the agent state machine and invokes each existing command-line tool.

## Prerequisites

Abacus targets macOS and Linux. These commands must be on `PATH`:

| Tool | Minimum supported version |
| --- | --- |
| .NET SDK | 10.0.101 |
| Beads (`bd`) | 1.2.2 |
| Git | 2.55.0 |
| OpenCode | 1.18.20 |
| Codex CLI | 0.151.0 |
| Claude Code | 2.1.212 |
| tmux | 3.6a (all interactive modes and optional pane-hosted server mode) |

Before running Abacus:

1. Initialize Beads in every assigned Git workspace.
2. Make every workspace clean and give each agent a distinct worktree or clone.
3. For OpenCode, Codex, or Claude mode, start the named tmux session and optionally select a specific window.
4. For OpenCode Server mode, start an OpenCode server. tmux is optional.

For a brand-new multi-agent project, Abacus can perform the repository, Beads,
skill, and worktree setup in one standalone command:

```sh
abacus --init-new-multi-agent-repo my-project 4
```

Run it from the directory that should contain `my-project/`. It creates this
layout:

```text
my-project/
├── repo/                       # main Git worktree and shared Beads project
├── worktrees/0/                # detached agent worktree
├── worktrees/1/
├── worktrees/2/
├── worktrees/3/
├── run_abacus_opencode.sh
├── run_abacus_codex.sh
└── run_abacus_claude.sh
```

The initializer refuses to replace an existing destination. It initializes
`main`, configures Beads with a unique shared-server Dolt database, disables
`no-git-ops`, marks the database local-only, creates a Beads merge slot,
installs the bundled skills, and commits the initial repository before making
the detached worktrees. Beads initialization is non-interactive and selects the
maintainer role. It does not create the tmux session.

Each launcher discovers every directory under `worktrees/` and starts one agent
per directory. Its default tmux session is the normalized project name; override
it with `ABACUS_TMUX_SESSION`. Override the executable, model, or effort with
`ABACUS_BIN`, `ABACUS_MODEL`, or `ABACUS_EFFORT`, or pass model and effort as
the first two launcher arguments:

```sh
cd my-project
tmux new-session -d -s my-project
./run_abacus_codex.sh gpt-5.6-sol high
```

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

### Install the bundled agent skills

From anywhere inside a target Git repository, run:

```sh
abacus --install-skills
```

This installs four skills under `.agents/skills` at the repository root:

| Skill | Purpose |
| --- | --- |
| `abacus-beads-planner` | Turns a concept into a user-reviewed, execution-ready Beads epic and issue graph. |
| `abacus-beads-doctor` | Audits issue content, fields, labels, metadata, and dependencies, then works with the user on approved repairs. |
| `abacus-beads-attention` | Finds every issue carrying `abacus:needs-user-attention`, including closed issues, and produces a prioritized high-level action report. |
| `abacus-git-check` | Audits active agent instructions for misleading restrictions on local Git operations while accepting push restrictions. |

The installed skills include their `SKILL.md` instructions and
`agents/openai.yaml` discovery metadata. Installing them does not invoke a skill
or change the Beads database.

If any bundled skill directory already exists, Abacus lists the affected skills
and requires confirmation before replacing their complete directories with the
bundled versions. Declining or sending end-of-input cancels installation without
changing any files. Confirming replaces each existing bundled skill's complete
directory, including local edits and extra files; unrelated skills are never
removed. A first-time install does not prompt.

`--install-skills` is standalone and cannot be combined with run options. It requires Git
and a current directory inside a Git repository, but does not require a model,
agent configuration, Beads project, tmux session, or agent CLI.

After installation, invoke the skills by name in a compatible agent client, for
example:

```text
Use $abacus-beads-planner to turn this concept into a Beads issue graph: ...
Use $abacus-beads-doctor to audit the issues under epic bd-123.
Use $abacus-beads-attention to tell me what needs my attention.
Use $abacus-git-check to check this repository's agent instructions for incorrect Git restrictions.
```

Note: These skills are optional and their installation is not required in order for abacus to function.

### Check repository health

Run the standalone, read-only health check from anywhere inside the repository:

```sh
abacus --health
```

The report checks:

- Git and Beads installation, minimum versions, and the resolved repository root;
- whether Beads is initialized and configured for embedded single-agent use or
  reachable shared-Dolt multi-agent use;
- whether Beads `no-git-ops` is disabled, with
  `bd config set no-git-ops false` shown as the correction when needed;
- whether the project has a Beads merge slot, including its current holder when
  occupied; a missing slot warns that agents may not serialize merges unless the
  repository defines another coordination mechanism;
- OpenCode, Claude Code, and Codex installation and minimum versions—individual
  harnesses are optional, but at least one supported harness is required;
- the minimum tmux version and which pane-hosted modes it enables;
- all worktrees referenced by the root repository;
- all four bundled skill directories, including `SKILL.md` and
  `agents/openai.yaml`; and
- the resulting runnable agent modes and single-/multi-agent readiness.

The primary checkout is itself a Git worktree. If it is the only referenced
worktree, the report makes clear that linked-worktree multi-agent execution is
not available. Separate clones can also be passed to Abacus as distinct
workspaces, but `--health` intentionally does not search the filesystem for
them. Direct OpenCode Server mode does not need tmux, and the health check does
not attempt to find or contact an OpenCode server.

`--health` exits with status 0 when at least one single-agent mode is runnable,
`no-git-ops` is disabled, and all bundled skills are installed. It exits with
status 1 when the repository needs attention. A missing merge slot is advisory
and does not change the exit status because repositories may use another
serialized merge process.

### List available models

Run the standalone model catalog from any directory:

```sh
abacus --models
```

Abacus invokes `opencode models` and `codex debug models`, then prints the
available IDs under separate OpenCode and Codex headings. Missing harnesses and
catalog failures are shown within their group, so one failure does not hide
models returned by another harness. Claude Code currently exposes its model
picker only through the interactive `/model` command; the Claude Code group
explains that limitation instead of presenting aliases as account-specific
availability. The command exits zero if at least one model ID is discovered and
one otherwise. It does not require Git, Beads, tmux, a model, or agent options.

### Resolve a user-attention callout

Remove the `abacus:needs-user-attention` label from one issue in the current
Beads project:

```sh
abacus --resolve ab-123
# Short form: abacus -r ab-123
```

Optionally provide a quoted response message:

```sh
abacus --resolve ab-123 "Approved option A"
```

Add `--reopen` to also set the ticket back to `open` and clear its assignee so
an agent can claim it again:

```sh
abacus --resolve ab-123 "Approved option A" --reopen
```

Without a message, Abacus performs one `bd update` that removes the label. When
a message is present, Abacus first adds the exact message as a Beads comment and
then removes the label. With `--reopen`, that same update also reopens and
unassigns the ticket:

```text
Approved option A
```

If adding the comment fails, the attention label is left in place. The command
prints a concise confirmation rather than the raw Beads JSON.

This is a standalone operation. It does not require a model, agent workspace,
tmux session, or agent harness. Multi-word messages must be passed as one quoted
argument.

### List tickets needing user attention

Print every ticket ID carrying the `abacus:needs-user-attention` label, including
closed tickets:

```sh
abacus --list-user-attention
```

The output is script-friendly: one ID per line with no heading. This standalone,
read-only command does not run preflight or require agent options.

### Prune branches for closed tickets

Delete local `abacus/<issue-id>` branches whose matching Beads tickets are
closed:

```sh
abacus --prune-closed-branches
```

The command never deletes non-Abacus branches or remote refs. It skips and
reports matching branches that are checked out in any worktree, and reports the
branches it deleted. It is standalone and does not require normal run options.

## Agent modes

Abacus supports exactly four modes:

| `--mode` | Hosting | Command |
| --- | --- | --- |
| `opencode` | Interactive tmux pane | `opencode --prompt <prompt> --model <provider/model>` |
| `codex` | Interactive tmux pane | `codex --cd <workspace> --model <model> --config model_reasoning_effort=<effort> --approve-for-me <prompt>` |
| `claude` | Interactive tmux pane | `claude --model <model> --effort <effort> --permission-mode auto --name <agent-ticket> <prompt>` |
| `opencode-server` | Direct process or tmux pane | `opencode run <prompt> --model <provider/model> --variant <effort> --attach <url> --dir <workspace>` |

`--mode` defaults to `opencode`, and `--effort` defaults to `high`. For compatibility with earlier releases, using `--opencode-server` without `--mode` implies `opencode-server`. OpenCode modes require models in `provider/model` form. Codex and Claude modes accept their native model IDs or aliases. Effort and variant names are provider- and model-specific; Abacus passes them through and lets the selected CLI validate availability, except in interactive OpenCode mode. OpenCode 1.18.20 does not expose a variant option for its interactive TUI, so Abacus leaves the model ID intact and OpenCode uses its configured or session-selected variant. OpenCode Server mode continues to pass `--variant <effort>`.

The Codex and Claude commands remain interactive and receive a real pane TTY. Abacus does not use `codex exec` or `claude --print`. Both start with their automatic permission reviewers, preventing an unattended pane from waiting indefinitely for ordinary approvals without disabling vendor safety boundaries.

Add `--remote` in Claude mode to make the interactive session remotely controllable. Claude receives `--remote-control '<issue-id> • <issue-title>'`. `--remote` is rejected for Codex, OpenCode, and OpenCode Server.

Examples using the same existing tmux target:

```sh
abacus --mode opencode --tmux-session workers --model anthropic/claude-sonnet-4-6 --effort high \
  -a alice /work/repo-a

abacus --mode codex --tmux-session workers --model gpt-5.6-terra --effort high \
  -a alice /work/repo-a

abacus --mode claude --tmux-session workers --model sonnet --effort high --remote \
  -a alice /work/repo-a
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
export EFFORT=high

opencode models
cd "$REPO"
git rev-parse --show-toplevel
```

Initialize Beads. `--init-if-missing` makes this safe to repeat when the repository is already initialized.

```sh
bd init --init-if-missing --non-interactive
bd config set no-git-ops false
bd dolt show --json
bd dolt remote list --json
```

Abacus agents must be allowed to commit and merge. Set `no-git-ops` explicitly
after initialization so the project does not inherit a global Beads setting.

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
  --effort "$EFFORT" \
  -a "$AGENT" "$REPO"
```

Abacus will claim ready tickets, create or reuse `abacus/<issue-id>`, and launch the full OpenCode TUI with the ticket prompt in an Abacus-owned pane. Each pane is given a stable `<agent> • <issue-id>` tmux title, and OpenCode is prevented from replacing it while that pane exists. tmux shows the active pane title in its default status line; configurations that display `#{pane_title}` in pane borders show every agent label beside its pane. OpenCode receives the pane's terminal directly so the TUI has a TTY. Abacus returns to polling after each ticket reaches `closed`, `open`, or `blocked`.

The default terminal display is a live dashboard with one row per agent. It shows the current lifecycle state (`STARTING`, `PAUSED`, `WAITING`, `IDLE`, `SYNCING`, `CLEANING`, `PREPARING`, `WORKING`, `FINALIZING`, `RECOVERING`, `RETRYING`, or `STOPPED`), elapsed time in that state, the active ticket ID and title, pane or process location, retry count, last observed exit code, and recent warnings. New ticket claims start enabled. Press `Shift-Tab` to pause or resume new claims for every agent; active tickets continue running, and the dashboard header shows the current claim state. At the bottom, a periodically refreshed log shows the latest 8 Beads comments by default. Each entry puts the issue ID, truncated issue title, and author on a header line, then uses an indented second line for the truncated comment. Attention-labelled issues are red, configured-agent authors are yellow, and unrecognized authors are cyan. Use `--latest-comments <count>` to show from 1 through 100 entries. Idle polling is distinct from error retries. For raw diagnostics, add `--verbose` (or `--debug`/`-v`):

```sh
abacus --verbose \
  --tmux-session "$SESSION" \
  --tmux-window "$WINDOW" \
  --model "$MODEL" \
  --effort "$EFFORT" \
  -a "$AGENT" "$REPO"
```

Verbose mode prints timestamped state transitions, warnings, and every external command. If stderr is redirected to a file or pipe without `--verbose`, Abacus automatically uses compact state-transition lines instead of ANSI terminal control sequences. Set `NO_COLOR=1` to disable dashboard colors while retaining the live layout. When Abacus stops, it prints elapsed run time and per-agent counts for closed, reopened, blocked, and interrupted tickets.

### Desktop notifications

Desktop notifications are disabled by default and are generated by Abacus itself, so behavior is consistent across OpenCode, Codex, Claude Code, and OpenCode Server modes:

```sh
abacus --notify attention --notify-sound \
  --tmux-session "$SESSION" --model "$MODEL" \
  -a "$AGENT" "$REPO"
```

`--notify attention` reports newly observed `abacus:needs-user-attention` issues, blocked tickets, and persistent recovery failures. `--notify all` additionally reports closed, reopened, and interrupted outcomes plus the final run summary. `--notify-sound` uses a positive sound for closed tickets and fully successful runs, and a negative sound for attention, persistent failures, reopened, blocked, or interrupted outcomes and run summaries containing any of those outcomes. It permits a terminal bell fallback when notification delivery fails.

On macOS, Abacus invokes `/usr/bin/osascript` with `Hero` and `Basso` as the positive and negative sounds. On Linux, it invokes `notify-send` with the standard `complete` and `dialog-warning` sound hints; `notify-send` must be installed and requires a graphical desktop notification session, and the active notification daemon and sound theme determine whether those hints produce distinct sounds. Delivery is best effort: unavailable notification support is visible only in verbose diagnostics and never changes an agent or process outcome. Polled attention issues are notified once when they first appear, and can notify again after their attention label is removed and later restored.

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

## Four-agent walkthrough with Git worktrees and shared Dolt

This example keeps the repository's primary checkout as an administrative workspace and creates four linked worktrees exclusively for Abacus agents. All four worktrees automatically discover the primary checkout's Beads workspace, so initialize Beads only once; do not run `bd init` separately in each worktree.

Choose paths, the base branch, the tmux target, and an OpenCode model:

```sh
export REPO=/path/to/your/repository
export WORKTREES=/path/to/your/repository-worktrees
export BASE=main
export SESSION=abacus-work
export WINDOW=agents
export MODEL=provider/model
export EFFORT=high

opencode models
git -C "$REPO" rev-parse --verify "$BASE"
git -C "$REPO" status --porcelain
```

Start from a clean repository and initialize Beads in shared-server mode. This uses the Beads-managed Dolt server under `~/.beads/shared-server/`, allowing the four agents to claim and update tickets concurrently against one database. Review and commit any project files changed by `bd init` before creating the worktrees.

```sh
cd "$REPO"
bd init --shared-server --non-interactive
bd config set no-git-ops false
bd dolt start
bd dolt show --json
```

Create four detached worktrees from the same base branch. Detached worktrees are intentional: after claiming a ticket, Abacus creates or checks out the corresponding `abacus/<issue-id>` branch itself.

```sh
mkdir -p "$WORKTREES"
git -C "$REPO" worktree add --detach "$WORKTREES/alice" "$BASE"
git -C "$REPO" worktree add --detach "$WORKTREES/bob" "$BASE"
git -C "$REPO" worktree add --detach "$WORKTREES/carol" "$BASE"
git -C "$REPO" worktree add --detach "$WORKTREES/dave" "$BASE"
git -C "$REPO" worktree list
```

Confirm that every worktree resolves the same server-backed Dolt database. The normalized host, port, and database reported by each command must match or Abacus will reject the configuration.

```sh
bd -C "$WORKTREES/alice" dolt show --json
bd -C "$WORKTREES/bob" dolt show --json
bd -C "$WORKTREES/carol" dolt show --json
bd -C "$WORKTREES/dave" dolt show --json
```

Create at least four independent ready tickets so every agent can claim work immediately. Replace the example titles, descriptions, and acceptance criteria with real tasks that the agents can complete without further input:

```sh
cd "$REPO"
bd create "Implement task one" --description "Describe task one." --acceptance "Define task one completion." --json
bd create "Implement task two" --description "Describe task two." --acceptance "Define task two completion." --json
bd create "Implement task three" --description "Describe task three." --acceptance "Define task three completion." --json
bd create "Implement task four" --description "Describe task four." --acceptance "Define task four completion." --json
bd ready --json
```

Start the tmux target and run one Abacus agent in each worktree:

```sh
tmux new-session -d -s "$SESSION" -n "$WINDOW"

abacus \
  --tmux-session "$SESSION" \
  --tmux-window "$WINDOW" \
  --tmux-layout tiled \
  --model "$MODEL" \
  --effort "$EFFORT" \
  -a alice "$WORKTREES/alice" \
  -a bob "$WORKTREES/bob" \
  -a carol "$WORKTREES/carol" \
  -a dave "$WORKTREES/dave"
```

Abacus verifies the shared Dolt identity during preflight, then each agent atomically claims a different ready ticket and works only in its assigned worktree. Attach with `tmux attach-session -t "$SESSION"` to watch the four tiled agent panes. As in the single-agent walkthrough, each worktree is disposable: Abacus resets tracked changes and removes untracked non-ignored files before every claim.

## Single-agent walkthrough with an OpenCode server and no tmux

The repository, Beads ticket, and clean-workspace check are identical to the single-agent walkthrough above. Start the server yourself and pass its address to Abacus; no tmux session is needed.

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
export EFFORT=high

cd "$REPO"
bd ready --json
git status --porcelain

abacus \
  --mode opencode-server \
  --model "$MODEL" \
  --effort "$EFFORT" \
  --opencode-server 127.0.0.1:4096 \
  -a "$AGENT" "$REPO"
```

The value passed to `--opencode-server` is `host:port`, without an `http://` prefix. Abacus normalizes it and launches each agent through:

```sh
opencode run <prompt> \
  --model "$MODEL" \
  --variant "$EFFORT" \
  --attach http://127.0.0.1:4096 \
  --dir "$REPO"
```

Abacus launches one directly supervised `opencode run --attach` child per active agent. Each child has its own workspace, prompt, model, and `BEADS_ACTOR`. Its output is drained so the Abacus dashboard stays intact. Abacus does not start, stop, or directly query the server. Stop Abacus with `Ctrl-C`, then stop the OpenCode server with `Ctrl-C` in its terminal.

If you prefer pane-hosted attached clients, also pass `--tmux-session` and optionally `--tmux-window` and `--tmux-layout`; Abacus then uses the existing tmux behavior.

## Usage

Initialize a new multi-agent repository from the current directory:

```sh
abacus --init-new-multi-agent-repo <project-name> <agent-count>
```

Install the bundled skills:

```sh
abacus --install-skills
```

See [Install the bundled agent skills](#install-the-bundled-agent-skills) for
the installed paths, skill purposes, and replacement confirmation behavior.

Check repository readiness:

```sh
abacus --health
```

See [Check repository health](#check-repository-health) for the checks and exit
status.

Start agents in an existing tmux session:

```sh
abacus --mode <opencode|codex|claude> \
  --tmux-session <session_name> \
  --tmux-window <window_name_or_index> \
  --tmux-layout <layout> \
  --model <model> \
  [--effort <effort>] \
  [--remote] \
  [--append-agent-prompt <prompt>] \
  [--label <label>] [--exclude-label <label>] \
  [--type <types>] [--priority <priority>] \
  [--ticket-timeout <duration>] \
  [--latest-comments <count>] \
  [--notify <off|attention|all>] [--notify-sound] \
  [--once | --drain | --check] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

Connect new OpenCode client sessions to an existing server:

```sh
abacus --mode opencode-server --model <provider/model> [--effort <effort>] \
  --opencode-server 127.0.0.1:1234 \
  [--append-agent-prompt <prompt>] \
  [--label <label>] [--exclude-label <label>] \
  [--type <types>] [--priority <priority>] \
  [--ticket-timeout <duration>] \
  [--latest-comments <count>] \
  [--notify <off|attention|all>] [--notify-sound] \
  [--once | --drain | --check] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--model` is required. `--effort` defaults to `high` and accepts a nonempty provider-specific effort or variant name without whitespace. `--remote` is valid only for Claude. OpenCode modes require `provider/model`; Codex and Claude accept nonempty native model IDs without whitespace. OpenCode, Codex, and Claude modes require `--tmux-session`. With OpenCode Server mode, tmux is optional: omit `--tmux-session` for directly supervised child processes, or include it for pane-hosted attached clients. `--tmux-window` accepts a window name or index and is valid only with `--tmux-session`; when omitted, panes use that session's current window. `--tmux-layout` is also tmux-only and accepts `even-horizontal`, `even-vertical`, `main-horizontal`, `main-vertical`, or `tiled`; Abacus reapplies it after each pane is spawned. `--opencode-server` accepts `host:port`; Abacus normalizes it to an HTTP URL and still uses only `opencode run --attach`, never the server API.

### Append repository-specific agent instructions

Abacus can add extra instructions to the end of its built-in agent prompt in two ways:

- Put Markdown in `.abacus/append-prompt.md` at the root of an agent's Git workspace.
  Each workspace's file is read during preflight and applies to agents using that
  workspace.
- Pass one global `--append-agent-prompt <prompt>` value. Quote the value when it
  contains spaces or newlines.

When both are present, Abacus appends the command-line prompt first, followed by
the contents of `.abacus/append-prompt.md`, with a blank line between the two.
For example:

```sh
mkdir -p .abacus
cat > .abacus/append-prompt.md <<'EOF'
Run the repository's smoke-test checklist before completing the ticket.
EOF

abacus --mode claude --tmux-session work --model sonnet \
  --append-agent-prompt "Keep the change narrowly scoped." \
  -a alice /work/repo-a
```

The final prompt order is: built-in Abacus prompt, command-line addition, then
repository addition. Empty repository files are ignored, and an empty
`--append-agent-prompt` value is rejected.

Output has two levels: the default live agent dashboard and `--verbose` debug output. `--debug` and `-v` are aliases for `--verbose`.

`--latest-comments` sets the number of recent comments displayed at the bottom of the live dashboard. It defaults to 8 and accepts 1 through 100. Abacus refreshes attention detection and then this data in one shared five-second monitoring cycle, using `bd --readonly export` for comments; redirected and verbose output do not run or print the dashboard-only comment feed.

`--notify` accepts `off`, `attention`, or `all` and defaults to `off`. `--notify-sound` requires an enabled notification mode.

### Dispatch filters and ticket timeout

Dispatch filters are global to the run and are passed directly to every `bd ready` lookup. Repeat `--label` to require all listed labels and repeat `--exclude-label` to reject issues carrying any supplied excluded label. `--type` accepts one Beads type filter, including comma-separated values such as `bug,task`; `--priority` accepts 0 (highest) through 4 (lowest). The same filters apply to new atomic claims and ready work already assigned to that agent. Abacus always excludes the `gt:slot` merge bead as well.

Beads priority remains the primary ordering. If multiple ready issues share the highest available priority after filtering, Abacus selects the issue with the newest comment. If none of those tied issues has a comment, it keeps the first issue in the Beads result. Before claiming, Abacus checks the selected issue with `bd show <id> --children --json` and skips it if any direct child is not closed. A failed or malformed child lookup cannot result in a claim. An eligible issue is still claimed atomically, and Abacus refreshes the candidates if another agent wins the claim race.

For example, expose only priority-1 bugs or tasks deliberately labelled for Abacus, while keeping human-owned work out of the queue:

```sh
abacus --mode opencode-server --model "$MODEL" \
  --opencode-server 127.0.0.1:4096 \
  --label abacus-ready \
  --exclude-label needs-human \
  --type bug,task \
  --priority 1 \
  -a "$AGENT" "$REPO"
```

`--ticket-timeout` is an optional runtime guard measured from agent CLI startup. It accepts a positive integer followed by `s`, `m`, or `h`, such as `30s`, `15m`, or `2h`. At the limit, Abacus attempts to stop and clean the hosted run, reopens the ticket only if it remains `in_progress`, verifies that recovery, and pushes when a Dolt remote is configured. If the agent's terminal ticket update races with the limit, Abacus preserves it. Failed recovery or synchronization stops that agent with a persistent attention alert; finite runs exit nonzero.

### Finite and preflight-only runs

Abacus normally waits and polls continuously. For CI and scripts, choose one mutually exclusive finite mode:

- `--once` lets each configured agent process at most one ready ticket, then exits. If no ticket is ready for an agent, that agent exits without waiting.
- `--drain` keeps processing tickets until each agent finishes its active work and observes no more ready tickets.
- `--check` runs preflight validation and exits without claiming tickets, cleaning workspaces, or starting an agent CLI. It checks only the selected agent executable plus Git workspaces, Beads `no-git-ops`, Dolt identity and remote configuration, applicable server-address syntax, and requested tmux target. Normal runs use the same preflight and exit with a correction command when `no-git-ops` is enabled.

Finite execution modes fail fast on command or orchestration errors instead of retrying forever. Successful `--once` and `--drain` runs print the normal outcome summary; `--check` prints a preflight success message and no run summary.

For example:

```sh
# Validate a CI worker without claiming work.
abacus --check --model "$MODEL" --opencode-server 127.0.0.1:4096 \
  -a "$AGENT" "$REPO"

# Empty the current ready queue and return control to the script.
abacus --drain --model "$MODEL" --opencode-server 127.0.0.1:4096 \
  -a "$AGENT" "$REPO"
```

Run `abacus --help` for the short prerequisite list and examples.

## Agent and branch behavior

Before starting any agent loop, Abacus establishes and records a Beads baseline.
With exactly one configured agent and a Dolt remote, it first runs `bd dolt pull`;
with multiple agents on a shared server, no pull is needed because every agent
already sees the live database. It then reads the full current Dolt commit with
`bd --readonly vc status --json`. The final run summary prints this commit in
both interactive and redirected output.

This identifies the Dolt `HEAD` from before Abacus claimed or updated any
tickets; it does not create a new checkpoint commit. Resetting a shared database
to that commit can also remove other writers' later changes and must be
coordinated separately.

Each agent has one asynchronous loop:

1. In single-agent mode, pull Dolt before claiming when a remote exists.
2. If the workspace is dirty, discard tracked and untracked non-ignored changes with `git reset --hard HEAD` and `git clean -fd` before claiming.
3. List all matching unassigned work with `BEADS_ACTOR=<agent> bd ready --unassigned --exclude-label gt:slot [dispatch filters] --limit 0 --json`. Preserve the highest Beads priority and break ties using the newest comment when available. Check the selected candidate with `bd show <id> --children --json`, skip it when any direct child is not closed, and only then atomically claim it with `bd update <id> --claim --json`. If an older run left matching ready work assigned to that same agent, apply the same child guard and selection rule while reclaiming it without taking another agent's work. Merge-slot beads are never dispatched as coding work.
4. Create or reuse `abacus/<issue-id>` and verify the workspace is clean.
5. Start the selected OpenCode, Codex, or Claude CLI in a dedicated Abacus-owned tmux pane. Start attached OpenCode Server clients in a pane when tmux was supplied, otherwise as directly supervised child processes.
6. Watch both the Beads status and the hosted agent run for exit or the optional ticket runtime limit.
7. Stop and clean the pane or direct process when the ticket becomes `closed`, `open`, or `blocked`.
8. Reopen tickets left `in_progress` by an unexpected exit, verify the resulting ticket state, push when configured, and continue waiting only after recovery and synchronization succeed.

Temporary `bd show` failures put the agent row into `RECOVERING`, but Abacus keeps the agent process supervised and continues polling with repeated-warning suppression. If the agent process exits while ticket status is still unreadable, Abacus does not guess or claim more work for that agent: it stops the loop and raises a persistent attention alert.

Reopen and Dolt-push retries return explicit success or failure outcomes. Exhausted retries stop that agent, remain visible as an attention alert, and make `--once` or `--drain` exit nonzero. A ticket counts as reopened in the final summary only after its `open` state has been read back from Beads.

Every agent session receives the prompt defined in
[`src/Abacus/Prompt.cs`](src/Abacus/Prompt.cs), with the agent name, issue ID,
and canonical workspace path substituted at runtime. The source contract is in
[`SPEC.md`](SPEC.md#agent-prompt-template). The built-in prompt supplies a basic
merge process that uses a Beads merge slot when the project has one and proceeds
without it when it does not. Repository-specific instructions can replace that
default with a custom merge process. The prompt explicitly grants agents authority
to stage, commit, and merge locally, but forbids `git push`. This overrides Beads
1.2.2's no-Git-authority guidance only when that
guidance is caused by the absence of a Git remote; `no-git-ops=true` still stops
Abacus during preflight, and more restrictive user or repository instructions
still take precedence. The optional command-line and repository
prompt additions described above are appended after this built-in prompt in
that order.

In default mode, agent states and recent warnings are shown without subprocess noise. In verbose mode, every external command is logged concisely to stderr with a timestamp and agent prefix. Pane-hosted prompt, wrapper, and marker files live under a per-process directory in the system temporary directory and are removed after a run. Agent CLI output is displayed directly in tmux for pane-hosted runs; direct attached-process output is drained to preserve the dashboard.

Ctrl-C cancels all loops. Abacus interrupts every active pane or direct process, checks the ticket again, attempts to reopen any ticket still `in_progress` with a shutdown note, performs configured Dolt pushes with bounded retries, and removes only runs it created. Ordinary shell commands have a fixed 30-second deadline; recovery has a fixed 15-second total budget, host cleanup has a fixed 10-second budget, and the complete per-ticket finalization path has a fixed 30-second budget, so a wedged external tool cannot block shutdown indefinitely.

Tmux cleanup is best effort: Abacus sends Ctrl-C to the recorded pane, waits briefly, attempts `kill-pane`, removes its run files, and moves on. It does not block shutdown or stop future work because tmux returned an unexpected pane during verification, rejected a cleanup command, or disappeared during shutdown. Abacus still targets only the pane ID it recorded when starting that agent.

## Deliberate boundaries

Abacus does **not**:

- create, delete, or repair Git worktrees or clones during orchestration; the
  standalone `--init-new-multi-agent-repo` command only creates a new layout;
- initialize or configure Beads, Dolt databases, or remotes outside the new
  standalone initializer;
- create or start the requested tmux session or window;
- start or manage an OpenCode server;
- merge branches or decide whether agent work is correct;
- choose ticket outcomes for an agent;
- integrate directly with Git, tmux, Dolt, Beads, OpenCode, Codex, or Claude APIs/protocols;
- integrate directly with Codex app-server or the Claude Remote Control protocol instead of their CLI commands;
- provide a daemon, web dashboard, persistent queue, dynamic pool, or Windows support.

The observed external command contracts and fixtures are documented in [`docs/contracts/cli-contracts.md`](docs/contracts/cli-contracts.md).
