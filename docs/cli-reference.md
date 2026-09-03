# Abacus CLI Reference

This page is the lookup reference for Abacus commands and run options. Use
`abacus --help` for a compact terminal summary and [Getting started](getting-started.md)
for complete setup examples.

## Command families

Abacus has two command families:

1. **Standalone operations** perform one setup, diagnostic, or maintenance task
   and exit.
2. **Orchestration runs** supervise one or more coding agents.

Standalone operations do not start preflight or agent loops unless their own
description says otherwise.

## Standalone operations

### Create a new multi-agent project

```sh
abacus --init-new-multi-agent-repo <project-name> <agent-count>
```

Creates `<project-name>/repo`, shared-server Beads configuration, bundled
skills, detached worktrees under `<project-name>/worktrees`, and launch scripts
for OpenCode, Codex, and Claude. The destination must not already exist. See
[Path A](getting-started.md#path-a-create-a-new-multi-agent-project).

### Install bundled skills

```sh
abacus --install-skills
```

Installs the four bundled skills under `.agents/skills` at the current Git root.
Existing bundled directories require confirmation before complete replacement;
declining leaves every skill unchanged. Unrelated skills are preserved.

The command requires Git and a working directory inside a repository. It does
not require Beads, tmux, a model, or an agent harness.

### Check repository health

```sh
abacus --health
```

Reports:

- Git and Beads versions and repository discovery;
- Beads storage mode, reachability, `no-git-ops`, and merge-slot state;
- supported agent harness and tmux versions;
- referenced Git worktrees;
- bundled skill presence; and
- available modes plus single- and multi-agent readiness.

It exits zero when at least one single-agent mode is runnable,
`no-git-ops=false`, and all bundled skills are installed. A missing merge slot
is advisory. The check is read-only and does not contact an OpenCode server.

### List available models

```sh
abacus --models
```

Groups IDs discovered from `opencode models` and `codex debug models`. Missing
or failed harnesses are isolated so another harness can still report results.
Claude Code is shown with guidance to use its interactive `/model` picker. The
command requires no repository or run options and exits zero when at least one
model ID is discovered.

### List user-attention issues

```sh
abacus --list-user-attention
```

Prints every issue ID carrying `abacus:needs-user-attention`, including closed
issues, one per line with no heading.

### Resolve a user-attention issue

```sh
abacus --resolve <issue-id> [<message>] [--reopen]
abacus -r <issue-id> [<message>] [--reopen]
```

- With a message, Abacus adds that exact text as a Beads comment first.
- It then removes `abacus:needs-user-attention`.
- `--reopen` also changes the issue to `open` and clears its assignee.
- If the comment fails, the label remains in place.

Quote a multi-word message as one argument:

```sh
abacus --resolve ab-123 "Approved option A" --reopen
```

### Prune branches for closed issues

```sh
abacus --prune-closed-branches
```

Force-deletes local `abacus/<issue-id>` branches whose Beads issues are closed.
It never deletes non-Abacus branches or remote refs. A matching branch checked
out in any worktree is skipped and reported without failing the remaining work.

## Orchestration synopsis

```sh
abacus [--mode <opencode|codex|claude|opencode-server>] \
  [--tmux-session <name> \
    [--tmux-window <name-or-index>] \
    [--tmux-layout <layout>]] \
  --model <model> \
  [--effort <effort>] \
  [--remote] \
  [--append-agent-prompt <prompt>] \
  [--label <label>] [--exclude-label <label>] \
  [--type <types>] [--priority <priority>] \
  [--ticket-timeout <duration>] \
  [--latest-comments <count>] \
  [--notify <off|attention|all>] [--notify-sound] \
  [--opencode-server <host:port>] \
  [--once | --drain | --check] \
  [--verbose] \
  -a <agent-name> <git-workspace> [-a ...]
```

`--model` and at least one `-a` pair are required. Each agent name and canonical
workspace path must be unique.

## Run options

### Agent and model

| Option | Default | Behavior |
| --- | --- | --- |
| `-a <name> <workspace>` | — | Adds an agent and its dedicated Git workspace. Repeat for a pool. |
| `--mode <mode>` | `opencode` | Selects one of the four supported modes. |
| `--model <model>` | — | Required; passed to every selected harness. |
| `--effort <effort>` | `high` | Nonempty provider-specific value without whitespace. |
| `--remote` | off | Enables Claude Remote Control; rejected in every other mode. |

OpenCode modes require `provider/model`. Codex and Claude accept their native
IDs or aliases. Harnesses remain responsible for validating model availability.

### Hosting

| Option | Behavior |
| --- | --- |
| `--tmux-session <name>` | Existing session that will host agent panes. Required by interactive modes. |
| `--tmux-window <name-or-index>` | Existing window inside the requested session. |
| `--tmux-layout <layout>` | Reapplies a built-in layout after each pane is created. |
| `--opencode-server <host:port>` | Existing server used by `opencode-server` mode. |

Layouts: `even-horizontal`, `even-vertical`, `main-horizontal`,
`main-vertical`, and `tiled`.

OpenCode Server mode may omit tmux for direct child-process hosting. Supplying
`--opencode-server` without `--mode` selects server mode for compatibility.
`--tmux-window` and `--tmux-layout` are valid only with `--tmux-session`.

### Dispatch and supervision

| Option | Behavior |
| --- | --- |
| `--label <label>` | Requires a label; repeat to require all supplied labels. |
| `--exclude-label <label>` | Excludes a label; repeat to reject any supplied label. |
| `--type <types>` | Passes one literal Beads type filter, including comma-separated values. |
| `--priority <0-4>` | Limits dispatch to one Beads priority (`0` is highest). |
| `--ticket-timeout <duration>` | Stops a run after a positive `s`, `m`, or `h` duration and safely recovers it. |

Filters apply to both fresh unassigned claims and matching ready work already
assigned to the same agent. Abacus always excludes `gt:slot` so merge
coordination beads are not dispatched as coding work.

Beads priority stays primary. Among the highest-priority candidates, the issue
with the newest comment wins; if none has comments, Abacus preserves Beads'
first result. A candidate with an unclosed direct child is skipped. Claims remain
atomic, and Abacus refreshes selection after a lost race.

### Prompt additions

| Input | Scope | Order |
| --- | --- | --- |
| `--append-agent-prompt <prompt>` | Every agent in the run | First addition |
| `<workspace>/.abacus/append-prompt.md` | Agents using that workspace | Second addition |

The final prompt is the built-in Abacus prompt, the command-line addition, then
the repository file, separated by blank lines. Empty repository files are
ignored; an empty command-line value is rejected.

Use repository instructions to replace the built-in basic merge process or add
project-specific verification:

```sh
mkdir -p .abacus
cat > .abacus/append-prompt.md <<'PROMPT'
Run the repository smoke-test checklist before closing the ticket.
PROMPT
```

The exact built-in prompt is normative in
[`SPEC.md`](../SPEC.md#agent-prompt-template).

### Dashboard and notifications

| Option | Default | Behavior |
| --- | --- | --- |
| `--latest-comments <1-100>` | `8` | Number of recent Beads comments in the live dashboard. |
| `--notify <off\|attention\|all>` | `off` | Selects desktop notification coverage. |
| `--notify-sound` | off | Adds positive/negative sounds; requires notifications. |
| `--verbose`, `--debug`, `-v` | off | Replaces the dashboard with timestamped transitions and subprocess diagnostics. |

`attention` reports newly observed attention labels, blocked tickets, and
persistent recovery failures. `all` also reports every outcome and the final
summary. Notification delivery is best effort and never changes orchestration
results. See [Operations](operations.md#desktop-notifications).

### Execution length

| Option | Behavior |
| --- | --- |
| `--once` | Each agent processes at most one currently ready issue, then exits. |
| `--drain` | Agents continue until active work finishes and no ready issue remains. |
| `--check` | Runs full non-mutating preflight and exits before cleanup or claims. |

These options are mutually exclusive. Normal operation polls continuously.
Finite modes fail instead of retrying orchestration errors forever, making them
suitable for scripts and CI.

Examples:

```sh
# Validate without claiming or cleaning a workspace.
abacus --check --mode opencode-server \
  --model provider/model \
  --opencode-server 127.0.0.1:4096 \
  -a alice /work/repo-a

# Process the current ready queue, then return control.
abacus --drain --mode opencode-server \
  --model provider/model \
  --opencode-server 127.0.0.1:4096 \
  -a alice /work/repo-a
```

## Harness commands

Abacus constructs these command families with argument lists rather than shell
interpolation:

| Mode | Effective command |
| --- | --- |
| OpenCode | `opencode --prompt <prompt> --model <provider/model>` |
| Codex | `codex --cd <workspace> --model <model> --config model_reasoning_effort=<effort> --approve-for-me <prompt>` |
| Claude | `claude --model <model> --effort <effort> --permission-mode auto --name <agent-ticket> [--remote-control <issue-ticket>] <prompt>` |
| OpenCode Server | `opencode run <prompt> --model <provider/model> --variant <effort> --attach <url> --dir <workspace>` |

OpenCode's interactive TUI at the supported version does not expose a variant
option, so it uses the configured or session-selected variant. Server mode can
pass `--variant` directly.

## Exit and output behavior

- Interactive terminals receive the live ANSI dashboard by default.
- Redirected standard error receives compact state-transition lines instead of
  terminal control sequences.
- `NO_COLOR=1` disables colors while retaining the live layout.
- Successful `--once` and `--drain` runs print the normal summary.
- `--check` prints preflight success without a run summary.
- The final summary contains elapsed time, initial Dolt commit, and per-agent
  closed, reopened, blocked, and interrupted counts.

Operational failures in finite modes produce a nonzero exit. For the detailed
recovery contract, see [Operations](operations.md#failure-and-recovery).
