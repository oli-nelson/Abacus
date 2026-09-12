# Abacus Product Specification

> **Document role:** Normative product specification. For task-oriented user
> documentation, start with the [README](README.md) or
> [documentation index](docs/README.md). The phased implementation design lives
> in [PLAN.md](PLAN.md).

## Contents

- [Setup](#setup)
- [Usage and command behavior](#usage)
- [Repository health](#repository-health)
- [Project information](#project-information)
- [Ticket targets](#ticket-targets)
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
abacus new <project-name> --agents <agent-count>
```

This standalone operation must reject an existing `<project-name>` destination,
then create `<project-name>/repo`, initialize its `main` branch, configure a
unique shared-server Beads database with `no-git-ops=false`, local-only Dolt,
and a merge slot, install all bundled Abacus skills, and commit the initial
repository state. It then creates `<agent-count>` detached Git worktrees at
`<project-name>/worktrees/0` through `worktrees/<agent-count-1>`.
Beads initialization must be non-interactive and select the maintainer role.

The project root receives `abacus_base.json` with the created named worktrees,
repository path, `startPaused: true`, `notify: "all"`,
`notifySound: true`, and `tuiAudio: true`. `abacus_opencode.json`,
`abacus_codex.json`, and `abacus_claude.json` contain version, baseConfig, mode,
model with an explicit `#high` effort, and all three reasoning-model tiers
initially mapped to that same `model#high` specification as editable examples.
Their baseConfig is `abacus_base.json`. Paths are relative to their
config directory. Do not generate shell launcher scripts. From the project root,
execute `abacus run` and select a harness config (not the incomplete shared base).
For non-interactive use or invocation from elsewhere, pass
`abacus run --config <path-to-harness-config>`. Edit the base once for all harnesses,
edit individual harness/model configs, or pass CLI overrides. Worktrees are
recorded at creation time; new worktrees must be added to the base config.
Previously generated scripts are not modified or deleted.

Before running Abacus:

1. Set up a Beads project in your main Git checkout, then run `abacus init`
   to install skills and create missing target and reasoning configuration.
   Review the policies and use `targets check` before dispatch.
2. For OpenCode, Codex, or Claude mode, Abacus creates or reuses a detached tmux
   session and agent window. Supplying explicit names remains optional.
3. For OpenCode Server mode, start an OpenCode server. A tmux session is optional in this mode.

Install Abacus's bundled planning, issue-quality, attention-reporting, and
Git-instruction-audit skills from inside the main checkout (or pass
`--repo <main-checkout>` from elsewhere):

```sh
abacus skills install
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

### Initialize Abacus in an existing repository

`abacus init [--repo <path>]` is standalone. Resolve the main Git checkout,
require an initialized, readable Beads project belonging to the same Git
repository (including explicit Beads redirects), and validate target
configuration and local branch refs before installing anything. Reject missing
or unusable Beads with setup guidance; never run `bd init` automatically.
Install the bundled skills using the same confirmation contract as
`skills install`. A declined confirmation leaves both skills and configuration
unchanged. Create `.abacus/targets.json` allowing only `main` when absent, with
`enforceTargetBranch: false` and `defaultTarget: "main"`;
also create an absent `.abacus/reasoning.json` version-1 policy with
`enforceLabels: false`. Preserve existing configuration byte-for-byte. If no local `main` exists, require
an explicit configuration naming an existing target or an operator-created branch.
Do not create branches, assign tickets, change Beads settings, stage, or commit.
Report created/preserved config, installed skills, and next steps: review/commit
setup files, run `health`, then audit/set ticket targets. Full agent readiness
is a separate health/preflight check, not an initialization prerequisite.

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

Operations are bare commands: `version`, `run`, `preflight`, `new`, `init`, `skills install`,
`health`, `info`, `models`, `branches prune`, `attention list`, `attention resolve`,
`targets check`, `targets set`, and `config edit`. Bare `abacus` prints help; there is no implicit
run or compatibility syntax. `abacus help <command>`, `<command> --help`, and
`<command> -h` provide scoped help without operational prerequisites.
`--repo` works before or after repository-scoped commands, not `new`, `models`, `version`, or `config edit`.
Options are command-scoped, exact, and non-repeatable unless documented otherwise.
`--agent <name> <workspace>` and `-a` are equivalent repeatable agent declarations.
Single-value options accept `--option=value`; `--` ends option parsing before
positional subjects. Message and prompt values must be consumed contextually,
never scanned for operation keywords or help flags. Attention messages require
`--message <text>`; positional messages are rejected.


### Saved run configurations

`abacus config edit [file] [--output <file>]` opens a standalone terminal editor,
without Git, Beads, model, or workspace prerequisites. Omit the input to create a
new draft; an explicit input must exist. Support Save and Save As, confirm before
replacing a different existing file, and warn before discarding unsaved changes.
Incomplete and semantically invalid drafts can be saved, with visible warnings
about missing/invalid settings. Malformed JSON, unknown fields, wrong field types,
duplicate keys, and unsupported versions fail clearly instead of losing data.

`abacus run --config <file>` and `abacus preflight --config <file>` load one
version-1 JSON config. `--config` is not repeatable. Optional `baseConfig` names
one base file relative to the declaring file (or an absolute path); bases may
have their own bases, up to 64 files. Reject missing/malformed bases, cycles,
and excessive depth. Apply deepest base first, then each derived config, then
explicit CLI overrides. Omitted fields inherit; null clears an inherited value
back to unset/default. Agent/filter arrays replace their entire list (including
empty arrays); reasoning model specifications and per-tier argument strings merge
per tier, with null clearing a tier or the whole mapping. A layer specifying once/drain replaces the previous execution
choice; both true within one layer remain invalid. Validate each file's structure,
but only the final composition must meet runtime requirements. Relative paths
retain the directory of the file that supplied each value. The editor edits only
the selected file, validates inherited values for warnings, and rebases its
baseConfig reference on Save As without flattening inherited fields.
Use the schema in [run configurations](docs/run-config.md).

For `run` without `--config`, validate supplied CLI arguments first. If required
values are missing (model, at least one named agent workspace, or server address
for opencode-server), report all missing requirements and offer one config
selection from the working directory. Discover only top-level JSON files matching
the run-config schema, including incomplete drafts; sort by filename and require
explicit selection even for one candidate. Never search parent directories or
start tools/agents during selection. After selection, apply CLI overrides and
validate once; if incomplete, report every remaining missing argument and exit
nonzero, without offering another selection. No candidates, cancellation, bad
selection, or an unusable config also exits without starting a run.

**Only interactive runs may select.** Redirected stdin, stdout, or stderr,
`--stdio`, `--verbose`, and `preflight` fail directly without discovery or prompts.
Invalid CLI syntax/values/combinations fail directly too. Fully specified runs,
explicit configs, and help never trigger discovery. Repository and installed-tool
readiness remain normal preflight checks, not config-selection prerequisites.

All run settings are supported. Explicit CLI scalars and boolean flags override
saved values, CLI agent/filter lists replace their saved list, and reasoning model
specifications and argument strings override per tier. Boolean CLI flags accept `=true` or `=false`. An explicit
once/drain option replaces the configured execution choice; conflicting explicit
options remain errors. Duplicate non-repeatable CLI options remain errors.
Preflight ignores saved
run-only output/lifecycle options, but rejects those options on its own CLI.
Relative repository, workspace, and event-log paths resolve against the config
directory; CLI paths still resolve against cwd. Save As rebases relative paths to
preserve their destinations. Runtime validation and preflight remain mandatory;
saving never starts agents. Help does not read a config file.

Initialize a new multi-agent repository:

```sh
abacus new <project-name> --agents <agent-count>
```

Initialize Abacus in an existing Git/Beads repository:

```sh
abacus init
```

Install only the bundled agent skills:

```sh
abacus skills install
```

Inspect whether the current repository is ready for Abacus:

```sh
abacus health
```

Print a concise overview of the current project's Git state, worktrees,
Beads/Dolt database, tickets, and routing configuration:

```sh
abacus info
```

List model IDs exposed by the installed agent harnesses:

```sh
abacus models
```

Delete local Abacus branches whose corresponding tickets are closed:

```sh
abacus branches prune
```

List ticket IDs that need user attention:

```sh
abacus attention list
```

Resolve a user-attention callout from the current Beads project:

```sh
abacus attention resolve <issue-id> [--message <text>] [--reopen]
```

Start agents with automatic tmux hosting, or select explicit tmux names:

```sh
abacus run [--tmux-session <session_name>] \
  [--mode <opencode|codex|claude>] \
  [--tmux-window <window_name_or_index>] \
  [--tmux-layout <layout>] \
  [--disown-tmux-session] \
  --model <model[#effort]> \
  [--reasoning-model <high|medium|low> <model[#effort]>] \
  [--extra-args <arguments>] \
  [--reasoning-args <high|medium|low> <arguments>] \
  [--remote-control] \
  [--repo <main-checkout>] [--target-filter <branch>] \
  [--label <label>] [--exclude-label <label>] \
  [--type <types>] [--priority <priority>] \
  [--ticket-timeout <duration>] \
  [--latest-comments <count>] \
  [--notify <off|attention|all>] [--notify-sound] \
  [--once | --drain] \
  [--verbose] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

`--mode` defaults to `opencode`. `--model <model[#effort]>` is required. The
optional suffix selects a nonempty provider-specific effort without whitespace
and defaults to `high`. OpenCode modes require a `provider/model` ID before the
suffix; Codex and Claude accept their native model IDs and aliases. Model and
effort availability remain the selected CLI's responsibility. Interactive
OpenCode is the exception: OpenCode 1.18.20's TUI entry point does not expose
variant selection, so Abacus strips the suffix from the model passed to OpenCode
and OpenCode uses its configured or session-selected variant.

`--extra-args <arguments>` appends a command-line argument string to every
launched harness CLI, and repeatable `--reasoning-args <tier> <arguments>`
replaces it for tickets carrying the matching reasoning label, so one run can
select a different provider or profile per reasoning tier. Each string is split
on whitespace with single/double quote grouping and backslash escapes and is
passed through the process argument list, never a shell. An empty or malformed
string is rejected. A tier without a mapped string keeps the default arguments.
Arguments are placed before the trailing prompt for Codex and Claude and after
the generated flags for the OpenCode modes. Every mode accepts extra arguments;
they typically select providers, profiles, or other harness options.

`--reasoning-model <tier> <model[#effort]>` is repeatable for the exact tiers
`high`, `medium`, and `low`. It maps the Beads labels `abacus:high_reasoning`,
`abacus:medium_reasoning`, and `abacus:low_reasoning` to model IDs valid for the
selected harness and optional provider-specific efforts. A missing tier suffix
inherits the fallback model's effort. Project policy loads from `<repo>/.abacus/reasoning.json`:

```json
{
  "version": 1,
  "enforceLabels": false
}
```

When the file is absent, enforcement defaults to off for compatibility. With
enforcement off, an unlabelled ticket or a single reasoning label without a
runtime mapping uses `--model`. With enforcement on, every run must configure
all three model mappings during preflight, and every executable ticket must have
exactly one reasoning label. A ticket carrying multiple reasoning labels is
invalid regardless of enforcement. After winning the atomic claim, Abacus
re-reads the issue and validates its reasoning labels before changing Git or
starting an agent. Invalid tickets are set to `blocked`, unassigned, labelled
`abacus:needs-user-attention`, and given a precise repair note. Interrupted and
reserved-ticket recovery uses the same routing rules. The resolved model remains
fixed for that hosted agent session.

`--remote-control` is valid only with Claude Code. Abacus adds `--remote-control '<issue-id> • <issue-title>'` to the normal interactive command. Codex and both OpenCode modes reject the option.

Dispatch filters are optional and apply to every fresh or same-agent resumed ready claim. `--label` and `--exclude-label` are repeatable literal passthroughs to `bd ready`; `--type` accepts one literal Beads type filter, including comma-separated types; and `--priority` accepts priorities 0 through 4. Abacus always excludes `gt:slot` in addition to user filters. Beads priority remains the primary ordering. When multiple candidates share the highest available priority, Abacus prefers the candidate with the newest comment; if none of those candidates has a comment, it preserves the first candidate returned by Beads. Before claiming that candidate, Abacus checks its direct children and skips it when any child is not closed.

Before a normal ready claim (including the same-agent assigned fallback), inspect
the candidate's issue branch with Git. If it is checked out in another worktree,
skip it without claiming, reopening, or changing either worktree; continue with
other ready candidates. A clean workspace retains eligibility for its own checked-out
issue branch after a blocked ticket is reopened. This also excludes branches held
by worktrees outside the configured agent pool; do not force checkout or detach
another workspace. Failed or malformed ownership reads must not permit a claim.
Refresh this check on each candidate selection/retry. Git's checkout restriction
remains the final safety check against external checkout races.

Before normal dispatch, a dirty workspace on `abacus/<issue-id>` is treated as
an interrupted run. Abacus reads and atomically claims that exact issue when it
is open and unassigned or already assigned to the configured agent, then validates its target, binding, and history before starting
the agent without changing the workspace. Dispatch filters do not apply to this
recovery. If the current branch is not a valid Abacus issue branch, the issue is
not open, it belongs to another agent, or the exact claim fails, Abacus preserves
the workspace and stops that agent with a persistent alert. It never resets or
cleans a dirty workspace automatically. At multi-agent startup, every agent
finishes this interrupted-workspace pass before any clean workspace can query
the normal ready queue. This reserves resumable tickets for the agents that own
their existing workspaces before fresh dispatch begins.

`--ticket-timeout` is an optional positive integer duration with an `s`, `m`, or `h` suffix. The guard starts when the agent CLI starts. At the limit, Abacus attempts to stop and clean the hosted agent run, reopens the ticket only if it is still `in_progress`, verifies the result, and pushes when a Dolt remote is configured. A terminal ticket update that races with the timeout is preserved. Recovery or push failure stops that agent, keeps a persistent alert visible, and makes finite runs fail.

`--latest-comments` controls the number of recent Beads comments shown at the bottom of the interactive dashboard. It defaults to 8 and accepts integers from 1 through 100. Abacus refreshes the snapshot in the same periodic monitoring cycle as user-attention detection, using the read-only Beads export command after the attention query. Each entry has a header line containing the issue ID, a width-truncated issue title, and the comment author, followed by a width-truncated comment wrapped across at most two indented lines. Only headers are colored: red for issues labelled `abacus:needs-user-attention`, green for configured-agent authors, and cyan for unrecognized authors. Comment message lines use the terminal's default color.

`--notify` controls Abacus-owned desktop notifications and defaults to `off`. `attention` reports newly observed `abacus:needs-user-attention` issues, blocked tickets, and persistent recovery failures. `all` additionally reports every ticket outcome and the final run summary. On macOS Abacus uses `osascript`; on Linux it uses `notify-send` when available. Notification delivery is best effort and never changes orchestration outcomes. `--notify-sound` uses a positive sound for closed tickets and runs with only closed outcomes, and a negative sound for attention, persistent failures, reopened, blocked, or interrupted outcomes and run summaries containing any of those outcomes. It permits a terminal bell fallback if desktop delivery is unavailable and requires `--notify attention` or `--notify all`.

`attention resolve <issue-id> [--message <text>] [--reopen]` is a standalone operation. It runs
`bd update` at the selected main repository root to remove
`abacus:needs-user-attention`. When the optional message is present, it first
uses `bd comment` to add the exact supplied message. The label is not removed if
the comment fails. When `--reopen` is present, the same update that removes the
label also sets the issue status to `open` and clears its assignee so it can be
claimed again. On success it prints a concise confirmation instead of the
raw Beads JSON. It does not run agent preflight or require normal run options.
The command fails if Beads cannot perform any requested operation.

`attention list` is a standalone, read-only operation. It queries every
issue carrying `abacus:needs-user-attention`, including closed issues, and
prints only issue IDs, one per line. It does not run agent preflight or require
normal run options.

`branches prune` is a standalone repository-maintenance operation. It
queries all closed Beads tickets and force-deletes matching local
`abacus/<issue-id>` branches from the selected main Git repository. It does not touch
non-Abacus branches or remote refs. A matching branch checked out in any
worktree is skipped and reported rather than causing the rest of the prune to
fail. It does not run agent preflight or require normal run options.

`models` is a standalone, read-only operation. It invokes `opencode models`
and `codex debug models` when those harnesses are installed, then prints their
available model IDs in separate groups. A missing harness or failed catalog is
reported within its group without suppressing successful groups. Claude Code
does not expose non-interactive model discovery, so an installed Claude CLI is
reported with guidance to use its interactive `/model` picker. The command
exits zero when at least one model ID was discovered and one otherwise. It does
not require a Git repository, Beads project, model, agent, or tmux session.

Each local agent runs interactively in its own tmux pane using its assigned Git
workspace and ticket-resolved model (the matching reasoning route, or the
requested default model):

- OpenCode: `opencode --prompt <prompt> --model <provider/model>`
- Codex: `codex --cd <workspace> --model <model> --config model_reasoning_effort=<effort> --approve-for-me <prompt>`
- Claude Code: `claude --model <model> --effort <effort> --permission-mode auto --name <agent-ticket> [--remote-control <issue-ticket>] <prompt>`

These commands deliberately start the interactive interfaces rather than Codex `exec` or Claude `--print`. Codex and Claude use their automatic permission reviewers so ordinary approvals do not block an unattended Abacus pane while actions still receive background safety checks.

Each pane has a stable `<agent> • <issue-id>` tmux title that the child process cannot replace. The selected agent CLI remains connected directly to the pane terminal so it has a TTY. Interactive modes default to a safe, repository-derived session name of `abacus - <project-id>` and the window name `Abacus Agents`. Abacus reuses either target when it exists and creates it detached when it does not. An explicitly named session is always user-owned. An implicit session is owned and removed at shutdown only when this invocation created it; `--disown-tmux-session` preserves an automatically created implicit session. An implicit session that already existed is never removed.

Abacus enables the window-level `remain-on-exit` option for both existing and newly created target windows. Agent panes carry pane-local tmux user options identifying them as Abacus-managed and recording the repository project ID, agent, and issue. Before splitting the window, Abacus attempts to `respawn-pane` a dead pane whose management and project tags match. Failed or raced reuse attempts fall back to a new split. Normal cleanup interrupts the agent and leaves the tagged pane dead for later reuse; initialization failures remove the partially initialized pane. After each pane starts, Abacus reapplies `tiled` by default; `--tmux-layout` can select `even-horizontal`, `even-vertical`, `main-horizontal`, `main-vertical`, or `tiled` instead.

Tmux shutdown is deliberately best effort. Abacus sends Ctrl-C to the recorded
pane, waits briefly, and force-respawns a harmless exiting command if necessary
so the tagged pane becomes dead and reusable. It removes temporary run files and
continues finalization regardless of tmux command output. Cleanup never targets
a pane other than the ID Abacus recorded at launch.

To connect the agents to an existing OpenCode server:

```sh
abacus run --mode opencode-server --model <provider/model[#effort]> \
  --opencode-server 127.0.0.1:1234 \
  [--once | --drain] \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

With no tmux-related option, each OpenCode Server agent starts as a directly supervised, non-interactive `opencode run --attach` child process connected to the specified server. Direct processes receive their own workspace, prompt, model, and `BEADS_ACTOR`; Abacus drains their output so it does not corrupt the dashboard and stops them when supervision ends. tmux is not looked up or required in this mode.

Server attachment requires explicit `--mode opencode-server` with `--opencode-server <host:port>`; the address alone never changes modes. With no tmux-related option, this mode remains directly hosted. Supplying `--tmux-session`, `--tmux-window`, `--tmux-layout`, or `--disown-tmux-session` requests pane hosting; an omitted session then uses the repository-derived default. The server option is rejected for all other explicit modes.

The dashboard header shows the default model and reasoning effort; each agent row
shows its ticket-resolved model/effort and actual checked-out branch (or detached
commit). A read-only Git snapshot refreshes approximately every five seconds,
including paused/stopped agents. Show a DIRTY marker for tracked or untracked
changes outside Preparing, Working, and Finalizing. Failed reads clear stale
branch/dirty data to unknown. OpenCode TUI effort is labelled requested, since
that harness does not expose CLI effort selection. Workspace/model snapshots
also appear as additive fields in structured agent.state events.

By default, Abacus displays a live terminal dashboard with one row per agent, showing whether each agent is starting, paused, waiting, idle, syncing, preparing a workspace, working on a ticket, finalizing, recovering, retrying, or stopped. Active rows include the ticket ID and title, time in the current state, process or pane location, retry count, and most recently observed exit code when available. For pane-hosted runs, the dashboard also shows the resolved tmux session and window names so the operator can attach from another shell. The dashboard starts with new ticket claims enabled unless `--start-paused` is supplied; in that case its header shows claims paused from the first frame. Pressing Shift-Tab toggles new claims on or off for all agents; pausing does not interrupt tickets that are already active. The header shows the current claim state, and agents waiting for permission display a paused state. The up and down arrows select agent and latest-comment rows. Enter opens the selected agent's action panel or the selected comment's complete detail view; long comments scroll with the arrow or Page Up and Page Down keys, and Escape returns to the dashboard. Stop interrupts that agent's hosted process, keeps its current ticket reserved, and parks the loop. Restart interrupts an active process when necessary and relaunches the reserved ticket, or resumes a parked or idle loop. Clean Workspace requires explicit confirmation, safely reopens any active ticket, runs `git reset --hard` followed by `git clean -fd`, and leaves the agent parked until Restart. A successful clean clears that agent's persistent recovery alert. Issues labelled `abacus:needs-user-attention`, including closed issues, appear in a persistent alert containing their IDs and titles until the label is removed. A periodically refreshed latest-comments log appears at the bottom with the configured number of issue, author, and comment entries. Warnings remain visible in the dashboard, and idle states are visually distinct from failures. `--verbose` (also accepted as `-v`) replaces the dashboard with timestamped state transitions, warnings, alerts, and every external command Abacus runs. When standard error is redirected, the default mode emits compact state transitions rather than terminal control sequences. Before starting any agent loop, Abacus pulls once when a single configured agent has a Dolt remote, then records the current Dolt `HEAD` with read-only `bd vc status`. Shared multi-agent databases are already live and are not pulled. On shutdown, Abacus prints that initial full Dolt commit in the final summary alongside elapsed time and per-agent counts for closed, reopened, blocked, and interrupted tickets.

### Event reporting, stdio control, and interactive intro

`run --event-log <path>` appends flushed UTF-8 JSONL activity events independently
of the dashboard or verbosity. `run --stdio` replaces all human output on stdout
with the same ordered event stream, consumes JSONL commands on stdin, and never
shows a TUI, intro, or notification. Stdio rejects verbose and enabled desktop
notifications. Keep fatal diagnostics on stderr. Event envelopes contain version
1, run ID, sequence, UTC timestamp, type, and structured data. Report startup,
state/ticket/process metadata changes, subprocess diagnostics, warnings, attention,
comments, persistent alerts, controls, ticket outcomes, summary, errors, and exit.

Stdio commands require a caller-supplied string ID: status, pause, resume, stop,
restart, clean-workspace, and shutdown. Agent actions require a known agent;
cleanup additionally requires literal `confirm: true` and uses the existing
safe reopen/confirmed destructive cleanup path. Respond with correlated success
or error events; action success means accepted, not completed. Reject malformed,
duplicate/unknown fields and commands without terminating the session. EOF and
shutdown use normal recovery/cleanup and exit zero on success. Finite runs finish
even with stdin open. Event-output failures request graceful shutdown and fail
the run, never bypassing ticket recovery. `--start-paused` works in the interactive dashboard and stdio modes and
pauses claims, including interrupted-workspace recovery, until resume; recovery
then retains its priority over fresh dispatch. Resume with Shift-Tab in the TUI
or the stdio resume command. Reject `--start-paused` with verbose output or a
non-interactive terminal without stdio, where no resume control is available.
The exact protocol and event fields are documented in
[events and stdio control](docs/events-and-stdio.md).

Before the main interactive TUI, play a brief, skippable animated ASCII intro
with a bundled mix of the welcome and jingle tracks; the jingle has 15% gain in
the mix so the voice remains prominent. Audio playback is best effort and uses
a native macOS player or an available Linux command-line player. Let the mix
finish when the animation completes naturally instead of cutting it off. TUI
audio is off by default; `--tui-audio` enables it, while `--no-intro` skips both.
Never play the intro or its audio in stdio, verbose, preflight, standalone
operations, redirected stdin/stdout/stderr, or dumb terminals. Honor `NO_COLOR`
and terminal dimensions, stop its audio on skip or cancellation, and restore
cursor visibility. All five new flags above are run-only.

Abacus runs continuously unless a finite execution option is selected. `--once` makes each agent claim and process at most one currently ready ticket; an agent exits immediately when no ticket is ready. `--drain` lets each agent continue claiming tickets until it observes no ready work, then exits after any active ticket finishes. Finite options fail rather than retrying orchestration errors forever, making them suitable for CI and scripts. `preflight` runs the complete non-mutating preflight and exits without changing workspaces, tmux sessions or windows, claiming tickets, creating panes or processes, or printing a run summary. It validates the selected main repository, target registry and local refs, reasoning policy and strict-mode model mappings, required executables, workspace, Beads `no-git-ops` setting, and Dolt configuration, plus the OpenCode server address when applicable. Missing tmux targets are created only by `run`, after preflight succeeds. `--once` and `--drain` are mutually exclusive run options; `preflight` is a separate command and rejects both.

### Repository health

`health` is a standalone, read-only diagnostic. From the selected main Git
repository it reports:

- whether target and reasoning configuration, instruction files, and local target branches are valid;
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
otherwise it exits one. Target and reasoning configuration must also be valid.
On an interactive color-capable terminal, headings and status markers are
color-coded: green for pass/ready, yellow for warnings, red for failures/not
ready, and cyan for informational results. Redirected output and dumb terminals
remain plain text, and `NO_COLOR` disables color explicitly.

### Project information

`info` is a standalone, read-only overview for the selected main Git repository.
It reports the project name and root, current branch and commit, dirty state,
origin URL, referenced worktrees, Dolt storage mode, database and server identity,
connectivity, remote presence, current Dolt commit, ticket counts by status,
attention count, target allowlist/default/enforcement, and reasoning-label policy.
Internal merge-slot records are excluded from ticket counts. Optional Git origin
and reasoning configuration may be absent; missing or invalid required project
data is reported inline and makes the command exit one. It does not probe agent
harnesses or tmux, mutate Git or Beads, or run agent preflight.

## Ticket targets

`enforceTargetBranch` is an optional config boolean defaulting to false.
`defaultTarget` is an optional local branch name defaulting to `main`. With
enforcement off, absent `metadata.abacus_target` resolves to the configured
default without stamping that key. With enforcement on, every work ticket,
including epics, decisions, and closed history, must carry a nonempty string
target. Internal `gt:slot` records are exempt. Explicit null, non-string, empty,
and unconfigured targets are invalid in both modes. There is no label routing
or parent/dependency inheritance. Each ticket has exactly one destination;
backports use separate linked tickets.

The controller selects the main checkout with `--repo <path>`. Without it, the
current directory must be inside the main checkout; normalize subdirectories to
the Git root. A linked worktree is rejected as the controller even if it checks
out `main`; `--repo` must select the primary checkout, not a branch name. Never
silently promote a linked worktree to its main repo or infer the controller from
agent `-a` paths. Only an explicit `--repo` permits invocation outside the selected
repo. Repository-scoped standalone operations (init, skill installation, health,
project information, target audit/set, attention listing/resolution, and pruning)
use the same rule.
Help, model discovery, and new-repository creation need no existing repository;
`--repo` is not supported with the latter two operations.

`--config` selects a run config only, never a target registry. Load `<repo>/.abacus/targets.json` once at startup; all
repository-scoped standalone Beads commands run at the selected root as well.
Agent workspaces may still be linked worktrees. The version-1 schema requires a
nonempty `targets` object keyed by allowed local branch names. Each target may
specify a relative `mergeInstructions` file path,
resolved from the config directory. Otherwise `merge-instructions.md` beside the
config is the optional fallback. Missing explicit files, invalid or duplicate
keys, unknown properties, and missing local targets
fail preflight. Policy files are not read from changing agent checkouts.
Legacy `repositoryId` configuration and `repository` binding fields are ignored;
no repository ID is required or emitted. When enforcement is off, the default
must be in the allowlist. An explicitly configured default must be allowed in
either mode; strict registries may omit an unused default.

`--target-filter <branch>` is a repeatable eligibility filter, never a destination
override. Apply target eligibility before priority/comment selection. Invalid
metadata candidates remain eligible for an atomic validation claim. After the
claim, reread ownership and metadata. A missing target under enforcement, an
invalid explicit target, or conflicting binding must block the owned ticket, clear its assignee, add
`abacus:needs-user-attention`, and append the precise reason and repair commands.
Verify the block, label, and reason and push when configured. Do not launch a
coding agent or mutate Git. Failed blocking or synchronization halts the loop.
Dirty recovery bypasses dispatch filters but never these safety checks.
A validation claim consumes the agent's one claim in `--once` mode.

Before preparing a new issue branch, persist and read back
`metadata.abacus_execution`: version 1, exact target ref,
issue branch, full starting target commit, and relevant merge-policy identity.
Defaulted tickets use the resolved branch in filters, audits, bindings, and
agent prompts. Changing a default never rewrites an existing binding; conflicting
bindings require operator review. Synchronize Beads when configured before branch
creation. Resume only matching bindings; a changed target or policy requires operator recovery.
Existing issue and target histories must contain the recorded starting commit.
Create new branches explicitly from that commit, never current workspace HEAD.
Never reset, rebase, or forcibly share another worktree's checked-out issue branch.
Hold exclusive per-workspace OS ownership in the Git administrative directory
before runtime claims; release handles on shutdown without deleting lock files.
The read-only `preflight` operation creates no locks.

Standalone commands:

```sh
abacus targets check [<issue-id> ...] [--repo <path>]
abacus targets set <branch> <issue-id> [<issue-id> ...] [--repo <path>]
abacus targets set <branch> <issue-id> --adopt-existing-branch --start-commit <full-commit-id>
```

The check is read-only and audits all tickets, including closed history, when no
IDs are supplied, excluding `gt:slot`. Validate metadata, configuration membership,
local refs, bindings, and existing issue history. Exit 0 only when every selected
ticket passes. Neither command requires agent preflight, a harness, or tmux.

The setter preserves unrelated metadata, status, notes, and attention labels.
Reject active tickets, malformed execution metadata, and bound retargeting.
Restoring missing/incorrect target metadata to its unchanged bound destination is allowed.
Validate the batch before writing and reread before/after individual writes;
partial CLI failures report completed IDs. Operators must stop concurrent work
before repair: metadata edits are not a compare-and-swap transaction. Commands
perform no automatic Beads push. Adoption requires an explicit full starting
commit contained in both issue and target histories, one inactive unbound ticket,
and an existing issue branch. Record its binding without changing Git. Never
silently adopt legacy branches or overwrite an existing binding.

`health [--repo <path>]` checks target configuration, instruction files, and
local branches; invalid targets make readiness fail. It does not audit tickets.
The new-repository initializer writes and commits a config with `main` allowed,
and the shared run config explicitly selects its repository. Planner and doctor must use
the metadata setter and checker. See [target operations](docs/targets.md) for the
schema, repair, adoption, and upgrade procedures.

This change does not add orchestrator-owned merging, landing receipts, completion
verification, or changes to branch pruning. Agents retain merge and outcome
responsibilities. Git fetch/push and automatic release-branch creation remain
outside normal orchestration.

## Agent workflow

Before starting the agent loops, fail preflight without claiming work or starting
an agent when Beads `no-git-ops` is enabled, and report
`bd config set no-git-ops false` as the correction. Otherwise, pull once when
exactly one configured agent has
a Dolt remote, then record the current Dolt commit. Abort before claims if
either operation fails. For multiple agents, record the shared server's current
commit without pulling it.

Each Abacus agent follows this loop:

1. Inspect the workspace. If it is dirty on `abacus/<issue_id>`, preserve its files and attempt to resume that exact open ticket before normal dispatch. Stop that agent without changing the workspace when its branch or ticket cannot be recovered safely. On the first pass after startup, clean agents wait until every configured workspace has completed this recovery step.
2. In single-agent mode, pull the latest Beads data if a remote is configured. Agents using a shared database already see the latest data.
3. When no interrupted workspace needs recovery, Abacus lists every unassigned ready task using the agent name as the actor:

   ```sh
   BEADS_ACTOR=<agent_name> bd ready --unassigned --exclude-label gt:slot --limit 0 --json
   ```

   When dispatch filters are configured, their literal `--label`, `--exclude-label`, `--type`, and `--priority` arguments are added before `--limit 0 --json`. The same filters apply when resuming ready work already assigned to that agent. Abacus keeps the priority ordering from Beads. If the highest-priority group contains multiple issues, it reads comments for the commented candidates using `bd show <ids...> --include-comments --json`, selects the issue with the newest comment, or keeps the first issue when none has a comment. Before claiming the selected issue, it runs `bd show <id> --children --json`. A candidate with any child whose status is not `closed` is skipped and selection continues with the remaining candidates. A failed or malformed child lookup fails safely without claiming the candidate. Abacus then atomically claims the eligible issue with `bd update <id> --claim --json`. If another agent wins that claim race, Abacus refreshes the candidates and tries again.

4. Validate the ticket target and durable execution binding, then create or check out `abacus/<issue_id>` using the safe preparation contract above.
5. Make sure a normal newly selected workspace has no local changes before starting the agent CLI. An interrupted issue workspace intentionally retains its existing changes.
6. Start the selected local agent CLI interactively in tmux, or start an attached OpenCode Server client either directly or in tmux. In every mode, set `BEADS_ACTOR=<agent_name>` and pass the requested model, the resolved default or per-tier extra arguments, and a prompt describing the issue and its ticket-state responsibilities. Use the controller-snapshotted target merge instructions when present; file presence overrides the default even when the file is empty. Pass the requested effort where the selected CLI exposes it; interactive OpenCode uses its configured or session-selected variant because its TUI has no variant CLI option.
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

The template below is the default, with `<target_branch>` resolved from the
validated ticket binding. Controller-snapshotted merge instructions replace the
section beginning `Commit your changes` and ending `merge and release succeed.`
with the file's trimmed contents. The default section must not remain anywhere in that agent's prompt.
An empty file intentionally removes the section without adding a replacement.

```text
You are <agent_name>, working on Beads ticket <issue_id> in <workspace_path>.

Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
name. Do not claim another ticket.

Abacus grants you authority to perform the local Git operations needed for this
ticket, including staging, committing, and merging into the local <target_branch> branch.
You do not have authority to push; do not run `git push`. If `bd prime` says
there is no Git authority, this explicit Abacus instruction overrides that.
Follow any more restrictive user or repository instruction.
The bound destination is refs/heads/<target_branch>. Custom instructions cannot
redirect this ticket to another branch. Do not change abacus_target or abacus_execution.

Waiting and long-running processes:
Waiting for a lock, merge slot, build, another agent, or any other event is
normal and is not by itself a reason to block this ticket. When you need to
wait, use only your harness's own waiting mechanism: run one command, inspect
its result, and try again later in this session, for as long as the event can
still plausibly occur. Keep working the ticket once the wait resolves.

Never leave work running outside this session. Do not write shell
`until`/`while`/`for` retry loops or use trailing `&`, `nohup`, `disown`,
`setsid`, `at`, or detached tmux, screen, or other background jobs. A process
that survives this harness exiting, such as
`until bd merge-slot acquire --holder "$BEADS_ACTOR"; do sleep 2; done`, keeps
running after Abacus cleans up this session and can break shared coordination
such as the merge slot for every other agent.

Mark the ticket blocked only when the wait looks hopeless to resolve without
outside help: you have retried in this session several times over a sustained
period (minutes, not seconds), the event still has not happened, and nothing
you can do will make it happen (for example, `bd merge-slot check` keeps
reporting a held slot that never becomes available). When you do block, state
in the note what you were waiting for, how long you retried, and why you
concluded the wait cannot resolve. A slow wait is not a hopeless wait: while
there is still a plausible path, keep waiting and keep retrying.

Read the ticket with:

  bd show <issue_id> --include-comments --json

Work on the branch abacus/<issue_id> and satisfy the ticket's definition of done.
Commit your changes, then merge the branch into the latest local <target_branch> branch.

Follow any repository-specific merge instructions when they define a merge process.
Otherwise, use this basic merge strategy:

1. Check for a Beads merge slot with `bd merge-slot check --json`. If the response
   reports that no merge slot exists, continue without one; do not create one. If a
   slot exists, acquire it before merging by running this command once:

     bd merge-slot acquire --holder "$BEADS_ACTOR"

   If another agent holds the slot, wait with your harness's own waiting mechanism
   and run that same single command again later in this session. Never wrap it in a
   shell retry loop, and never block the ticket just because the slot is held.

2. While holding the merge slot when one is configured, merge the latest local
   `<target_branch>` into the issue branch. Resolve any conflicts and commit the result.
3. Locate the worktree where `<target_branch>` is checked out with
   `git worktree list --porcelain`, then fast-forward it to the issue branch with
   `git -C <target-worktree> merge --ff-only <issue-branch>`. If `<target_branch>` is not checked
   out elsewhere, switch this workspace to `<target_branch>` and fast-forward it there.
4. If you acquired a merge slot, release it with
   `bd merge-slot release --holder "$BEADS_ACTOR"`. Always release it, including
   when the merge fails. Only close the ticket after the merge and release succeed.

You might not be the first agent to work on this ticket. The branch can contain
commits or uncommitted changes preserved from an interrupted run. Inspect the
branch history, `git status`, and `git diff` before making changes so you preserve
useful existing work. If earlier work is incorrect, you can fix or remove it.

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

## Release versions

`abacus version` is standalone and prints only the embedded informational
version followed by a newline, exiting zero without repository or external-tool
prerequisites. Reject positional arguments and operational options, including
`--repo`; support the standard scoped help forms. Source builds default to
`0.0.0-dev`; release builds report the tag version without its leading `v` or
an appended commit hash.

A manually dispatched Release workflow accepts `X.Y.Z` (optionally
`-alpha.N`, `-beta.N`, or `-rc.N`, with an optional leading `v`) and a
release branch. Core version components are 0–9999 without leading zeroes.
Pin the dispatch source SHA and run native Linux/macOS x64/ARM64 tests and
packaging against that exact commit. Each extracted self-contained executable
must report the requested version. Validation may prepare rollover metadata as
temporary artifacts but must not modify tracked files, Git refs, or Releases.
The local helper requires a clean branch matching origin and only dispatches
the workflow with a source-SHA guard; it never commits, tags, or pushes.

Only after all four jobs succeed, finalize the changelog and publish. Refuse
first-time finalization if the remote release branch differs from the tested
commit. Commit only CHANGELOG.md as a direct child of that commit, moving
Unreleased into the selected dated version and inserting a fresh empty section;
preserve older sections. Atomically push the commit and annotated version tag
with an exact branch lease, then upload all four archives and SHA-256 checksums
to a draft GitHub Release using the prepared notes before publishing.
Tests failing must leave the repository changelog and tags unchanged.

Serialize release runs and scope write credentials to the gated final job.
Respect branch/tag protection; do not bypass required review. No automatic
release PR flow is provided. Preview versions produce prereleases, never latest.

Git finalization and Release publication are not atomic together. Failed
publication may leave a landed release commit/tag and a draft. Same-run
failed-job retries must verify the run identity, tested parent, exact changelog,
and branch ancestry before resuming publication. Replace partial draft assets
only for that run, and treat its already-published release as a no-op. Never
overwrite a published release or take over another run's version.

Just before committing, agents add noteworthy entries to Unreleased, combining
related changes and removing superseded intermediate behavior. Packaging alone
never uploads; tag pushes no longer trigger releases. See [releases](docs/releases.md).
