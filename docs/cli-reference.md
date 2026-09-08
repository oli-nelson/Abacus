# Abacus CLI Reference

> **Ticket targets:** Configure the target allowlist and default destination.
> Missing ticket metadata is allowed unless `enforceTargetBranch` is enabled. See [target setup, audit, and recovery](targets.md).

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

## Repository selection

`--repo <path>` selects the main Git checkout for orchestration and all
repository-scoped standalone commands. Without it, run inside the main checkout
(subdirectories are normalized to its root). Linked worktrees cannot be the
controller root; from a worktree or non-repo folder, pass the main checkout
explicitly. Agent `-a` paths may still be linked worktrees.

Configuration always loads from `<repo>/.abacus/targets.json`. `--config` has
been removed; update old launchers to `--repo <main-checkout>` rather than a
JSON file path. Relative `--repo` paths resolve against the invocation directory.
Help, model listing, and new-repository creation remain usable outside Git;
`--repo` is not accepted with model listing or new-repository creation.

## Command structure

Operations use bare command names; options configure those operations. Bare
`abacus` prints help and never starts agents. Use `abacus run` explicitly.
There are no legacy operation flags, implicit-run syntax, or deprecated aliases.

```sh
abacus help
abacus help attention resolve
abacus attention resolve --help
abacus run --help
abacus preflight --help
```

`--help` / `-h` works on every command and command group without repository or
agent prerequisites. `--repo <path>` is accepted before or after a complete
repository-scoped command (for example, `abacus --repo /work/repo health` or
`abacus health --repo /work/repo`), but not by `new` or `models`.
Options are command-scoped; unknown names and duplicate non-repeatable options
fail rather than being silently ignored. `--agent` / `-a`, `--label`,
`--exclude-label`, and `--target-filter` are repeatable.

Single-value options accept `--option=value`. `--` ends option parsing before
positional subjects, for example `abacus targets check -- ab-123 ab-456`.
Message and prompt values are consumed as text, even when they look like a
command or flag: `abacus attention resolve ab-123 --message "--help"` records
that exact message; it does not display help. Positional messages are rejected.

## Standalone operations

### Create a new multi-agent project

```sh
abacus new <project-name> --agents <agent-count>
```

Creates `<project-name>/repo`, shared-server Beads configuration, bundled
skills, detached worktrees under `<project-name>/worktrees`, and launch scripts
for OpenCode, Codex, and Claude. Launchers explicitly pass `--repo "$root/repo"`.
The destination must not already exist. See
[Path A](getting-started.md#path-a-create-a-new-multi-agent-project).

### Initialize an existing Git/Beads project

```sh
abacus init
```

Validates an existing, readable Beads project belonging to this Git repository,
installs the bundled skills, and creates `.abacus/targets.json` allowing `main`
only if absent, with `enforceTargetBranch: false` and `defaultTarget: "main"`.
It also creates an absent `.abacus/reasoning.json` with `enforceLabels: false`.
Existing configuration is never overwritten. All configured
local branches must exist; without a local `main`, supply your own config first.
Existing bundled skills require replacement confirmation; declining changes
nothing. Main-checkout subdirectories are supported; linked worktrees require
`--repo <main-checkout>` and installation goes to that main checkout.

Accepts `--repo <path>` and needs no harness, model, or tmux. It does not initialize
Beads, change Beads settings, create branches, assign targets to tickets, stage,
or commit. Run `health` and `targets check` afterwards; successful init
is not proof of agent readiness. Review and commit the installed files.

### Install bundled skills

```sh
abacus skills install
```

Installs the four bundled skills under `.agents/skills` at the selected main Git root.
Existing bundled directories require confirmation before complete replacement;
declining leaves every skill unchanged. Unrelated skills are preserved.

The command requires Git and either a working directory inside the main checkout
or an explicit `--repo <main-checkout>`. It does
not require Beads, tmux, a model, or an agent harness.

### Check repository health

```sh
abacus health
```

Reports:

- Git and Beads versions and main-checkout discovery;
- target registry, instruction files, local target refs, and enforcement/default policy;
- Beads storage mode, reachability, `no-git-ops`, and merge-slot state;
- supported agent harness and tmux versions;
- referenced Git worktrees;
- bundled skill presence; and
- available modes plus single- and multi-agent readiness.

It exits zero when at least one single-agent mode is runnable,
`no-git-ops=false`, all bundled skills are installed, and main-repository and
target configuration checks pass. It does not audit individual tickets; use
`targets check` for that. A missing merge slot
is advisory. The check is read-only and does not contact an OpenCode server.

### Audit and repair ticket targets

```sh
abacus targets check [<id> ...] [--repo <main-checkout>]
abacus targets set <branch> <id> [<id> ...] [--repo <main-checkout>]
```

The read-only audit includes closed history when no IDs are supplied and exempts
internal `gt:slot` records. It validates resolved targets, bindings, and existing
branch history. Missing metadata is valid when enforcement is off. The setter
requires inactive tickets and preserves status, attention, and unrelated metadata.
Pause dispatch/active work before edits; neither command automatically pushes Beads.
Reviewed legacy adoption requires one unbound ticket, `--adopt-existing-branch`,
and `--start-commit <full-commit-id>`. See [target operations](targets.md).

### List available models

```sh
abacus models
```

Groups IDs discovered from `opencode models` and `codex debug models`. Missing
or failed harnesses are isolated so another harness can still report results.
Claude Code is shown with guidance to use its interactive `/model` picker. The
command requires no repository or run options and exits zero when at least one
model ID is discovered.

### List user-attention issues

```sh
abacus attention list
```

Prints every issue ID carrying `abacus:needs-user-attention`, including closed
issues, one per line with no heading.

### Resolve a user-attention issue

```sh
abacus attention resolve <issue-id> [--message <text>] [--reopen]
```

- With a message, Abacus adds that exact text as a Beads comment first.
- It then removes `abacus:needs-user-attention`.
- `--reopen` also changes the issue to `open` and clears its assignee.
- If the comment fails, the label remains in place.

Quote a multi-word message as one argument:

```sh
abacus attention resolve ab-123 --message "Approved option A" --reopen
```

### Prune branches for closed issues

```sh
abacus branches prune
```

Force-deletes local `abacus/<issue-id>` branches whose Beads issues are closed.
It never deletes non-Abacus branches or remote refs. A matching branch checked
out in any worktree is skipped and reported without failing the remaining work.

## Orchestration synopsis

```sh
abacus run [--mode <opencode|codex|claude|opencode-server>] \
  [--tmux-session <name>] \
  [--tmux-window <name-or-index>] \
  [--tmux-layout <layout>] [--disown-tmux-session] \
  --model <model> \
  [--reasoning-model <high|medium|low> <model>] \
  [--effort <effort>] \
  [--remote-control] \
  [--append-prompt <prompt>] \
  [--label <label>] [--exclude-label <label>] \
  [--type <types>] [--priority <priority>] \
  [--ticket-timeout <duration>] \
  [--latest-comments <count>] \
  [--notify <off|attention|all>] [--notify-sound] \
  [--opencode-server <host:port>] \
  [--once | --drain] \
  [--verbose] \
  -a <agent-name> <git-workspace> [-a ...]
```

`--model` and at least one `-a` pair are required. Each agent name and canonical
workspace path must be unique.

## Preflight

`abacus preflight <run-configuration-options>` uses the same model, agent,
repository, filter, and hosting configuration as `run`. It validates without
claims, workspace changes, hosted agents, cleanup, or a run summary. It rejects
`--once` and `--drain`. For broad diagnostics without specifying agents, use
`abacus health` instead.

## Run options

### Agent and model

| Option | Default | Behavior |
| --- | --- | --- |
| `--agent <name> <workspace>`, `-a <name> <workspace>` | — | Adds an agent and its dedicated Git workspace. Repeat for a pool. |
| `--mode <mode>` | `opencode` | Selects one of the four supported modes. |
| `--model <model>` | — | Required fallback model; used when no mapped reasoning route applies. |
| `--reasoning-model <tier> <model>` | — | Repeatable mapping for `high`, `medium`, and `low`. |
| `--effort <effort>` | `high` | Nonempty provider-specific value without whitespace. |
| `--remote-control` | off | Enables Claude Remote Control; rejected in every other mode. |

OpenCode modes require `provider/model`. Codex and Claude accept their native
IDs or aliases. Harnesses remain responsible for validating model availability.

### Reasoning labels and model selection

The three exact routing labels are `abacus:high_reasoning`,
`abacus:medium_reasoning`, and `abacus:low_reasoning`. Model mappings belong to
the run configuration because available IDs vary by harness and machine. Project
enforcement belongs to `<repo>/.abacus/reasoning.json`:

```json
{
  "version": 1,
  "enforceLabels": false
}
```

With enforcement disabled or the file absent, tickets without a reasoning label
use `--model`; a single labelled ticket with no corresponding mapping also uses
`--model`. With enforcement enabled, preflight requires all three mappings and
claimed tickets require exactly one label. Multiple reasoning labels are always
invalid. Abacus atomically claims and re-reads such a ticket before setting it
to `blocked`, clearing its assignee, adding `abacus:needs-user-attention`, and
recording repair instructions. The selected model is fixed for that agent
session. `--effort` remains global and is not derived from the label.

### Hosting

| Option | Behavior |
| --- | --- |
| `--tmux-session <name>` | Session to use or create. Interactive modes default to `abacus - <safe-project-id>`. |
| `--disown-tmux-session` | Preserve an automatically created implicit session after Abacus exits; rejected with an explicit session. |
| `--tmux-window <name-or-index>` | Window to use or create. Defaults to `Abacus Agents`. |
| `--tmux-layout <layout>` | Reapplies a built-in layout after each pane starts. Defaults to `tiled` for pane-hosted runs. |
| `--opencode-server <host:port>` | Existing server used by `opencode-server` mode. |

Layouts: `even-horizontal`, `even-vertical`, `main-horizontal`,
`main-vertical`, and `tiled`. When omitted, pane-hosted runs use `tiled`.

OpenCode Server mode uses direct child-process hosting when every tmux-related
option is omitted. Any tmux-related option requests pane hosting and may use the
derived default session.

Abacus enables `remain-on-exit` on the selected window and prefers to respawn a
dead pane carrying matching Abacus/project tmux user options before splitting a
new pane. It owns a session only when the current invocation created an implicit
session without `--disown-tmux-session`; explicit and pre-existing sessions are
never removed. The dashboard displays the resolved session and window names.

### Dispatch and supervision

| Option | Behavior |
| --- | --- |
| `--label <label>` | Requires a label; repeat to require all supplied labels. |
| `--exclude-label <label>` | Excludes a label; repeat to reject any supplied label. |
| `--type <types>` | Passes one literal Beads type filter, including comma-separated values. |
| `--target-filter <branch>` | Restricts the pool to resolved destinations; repeat to allow several. Never supplies ticket metadata. |
| `--priority <0-4>` | Limits dispatch to one Beads priority (`0` is highest). |
| `--ticket-timeout <duration>` | Stops a run after a positive `s`, `m`, or `h` duration and safely recovers it. |

Filters apply to both fresh unassigned claims and matching ready work already
assigned to the same agent. Abacus always excludes `gt:slot` so merge
coordination beads are not dispatched as coding work.

Resolved-target eligibility is applied before priority and newest-comment
selection; invalid metadata candidates remain eligible for a validation claim.
Among eligible candidates, Beads priority stays primary. Among the highest-priority candidates, the issue
with the newest comment wins; if none has comments, Abacus preserves Beads'
first result. A candidate with an unclosed direct child is skipped. Claims remain
atomic, and Abacus refreshes selection after a lost race.

### Prompt customization

| Input | Scope | Order |
| --- | --- | --- |
| `--append-prompt <prompt>` | Every agent in the run | First addition |
| `<workspace>/.abacus/append-prompt.md` | Agents using that workspace | Second addition |

The final prompt is the built-in Abacus prompt, the command-line addition, then
the repository file, separated by blank lines. Empty repository files are
ignored; an empty command-line value is rejected.

From the selected main checkout, use the fallback policy file to replace the
built-in merge instructions for targets without their own `mergeInstructions`:

```sh
mkdir -p .abacus
cat > .abacus/merge-instructions.md <<'MERGE'
Submit the issue branch through the repository merge queue, then close the ticket
only after the queue reports success.
MERGE
```

A target-specific `mergeInstructions` file takes precedence; otherwise this
controller-owned fallback replaces the built-in merge section. All paths are
relative to `<main-repo>/.abacus`, and policies are loaded once at startup—not
from agents' changing workspaces.
The default merge instructions are not included elsewhere in the prompt. An
empty file intentionally suppresses the default section without replacing it.

Use the append file for other project-specific guidance:

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
| `--stdio` | off | Run only: JSONL events on stdout, JSONL control commands on stdin, no TUI or intro. |
| `--event-log <path>` | none | Run only: append and flush structured JSONL activity to a file, with or without the TUI. |
| `--start-paused` | off | Run only: pause claims initially; resume with Shift-Tab in the TUI or the stdio resume command. Rejects verbose or non-interactive output without stdio. |
| `--no-intro` | off | Run only: skip the interactive ASCII entrance. |
| `--verbose`, `-v` | off | Replaces the dashboard with timestamped transitions and subprocess diagnostics. |

`attention` reports newly observed attention labels, blocked tickets, and
persistent recovery failures. `all` also reports every outcome and the final
summary. Notification delivery is best effort and never changes orchestration
results. See [Operations](operations.md#desktop-notifications).

### Execution length

| Option | Behavior |
| --- | --- |
| `--once` | Each agent processes at most one currently ready issue, then exits. |
| `--drain` | Agents continue until active work finishes and no ready issue remains. |

`--once` and `--drain` are mutually exclusive options for `run`.
`preflight` is a separate command and rejects both. Normal runs poll continuously.
Finite modes fail instead of retrying orchestration errors forever, making them
suitable for scripts and CI.

Examples:

```sh
# Validate without claiming or changing a workspace.
abacus preflight --mode opencode-server \
  --model provider/model \
  --opencode-server 127.0.0.1:4096 \
  -a alice /work/repo-a

# Process the current ready queue, then return control.
abacus run --drain --mode opencode-server \
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

- Interactive terminals receive a skippable ASCII entrance, then the live ANSI dashboard.
- `--stdio` outputs only JSONL events and accepts JSONL commands; it rejects verbose
  output and desktop notifications. See [events and stdio](events-and-stdio.md).
- Redirected stdin/stdout/stderr, verbose mode, preflight, and `TERM=dumb` never
  show the intro; `--no-intro` also disables it explicitly.
- Redirected standard error receives compact state-transition lines instead of
  terminal control sequences.
- `NO_COLOR=1` disables colors while retaining the live layout.
- Successful `--once` and `--drain` runs print the normal summary.
- `preflight` prints preflight success without a run summary.
- The final summary contains elapsed time, initial Dolt commit, and per-agent
  closed, reopened, blocked, and interrupted counts.

Operational failures in finite modes produce a nonzero exit. For the detailed
recovery contract, see [Operations](operations.md#failure-and-recovery).
