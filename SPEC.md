# Abacus Product Specification

> **Document role:** Normative product specification. For task-oriented user
> documentation, start with the [README](README.md) or
> [documentation index](docs/README.md). The phased implementation design lives
> in [PLAN.md](PLAN.md).

## Contents

- [Setup](#setup)
- [Usage and command behavior](#usage)
- [Repository health](#repository-health)
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
per worktree to Abacus. Each launcher passes `--repo "$root/repo"` explicitly
so invocation does not depend on the caller's working directory. Launchers accept
model and effort overrides but do not
create the tmux session.

Before running Abacus:

1. Set up a Beads project in your main Git checkout, then run `abacus --init`
   to install skills and create missing target configuration. Review the target
   policy and use `--check-ticket-targets` before dispatch.
2. For OpenCode, Codex, or Claude mode, start a tmux session and, optionally, the window where agent panes should run.
3. For OpenCode Server mode, start an OpenCode server. A tmux session is optional in this mode.

Install Abacus's bundled planning, issue-quality, attention-reporting, and
Git-instruction-audit skills from inside the main checkout (or pass
`--repo <main-checkout>` from elsewhere):

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

### Initialize Abacus in an existing repository

`abacus --init [--repo <path>]` is standalone. Resolve the main Git checkout,
require an initialized, readable Beads project belonging to the same Git
repository (including explicit Beads redirects), and validate target
configuration and local branch refs before installing anything. Reject missing
or unusable Beads with setup guidance; never run `bd init` automatically.
Install the bundled skills using the same confirmation contract as
`--install-skills`. A declined confirmation leaves both skills and configuration
unchanged. Create `.abacus/targets.json` allowing only `main` when absent, with
`enforceTargetBranch: false` and `defaultTarget: "main"`;
preserve existing configuration byte-for-byte. If no local `main` exists, require
an explicit configuration naming an existing target or an operator-created branch.
Do not create branches, assign tickets, change Beads settings, stage, or commit.
Report created/preserved config, installed skills, and next steps: review/commit
setup files, run `--health`, then audit/set ticket targets. Full agent readiness
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

Initialize a new multi-agent repository:

```sh
abacus --init-new-multi-agent-repo <project-name> <agent-count>
```

Initialize Abacus in an existing Git/Beads repository:

```sh
abacus --init
```

Install only the bundled agent skills:

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
  [--repo <main-checkout>] [--target-branch <branch>] \
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

`--resolve <issue-id> [<message>] [--reopen]` (short form `-r`) is a standalone operation. It runs
`bd update` at the selected main repository root to remove
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
`abacus/<issue-id>` branches from the selected main Git repository. It does not touch
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

By default, Abacus displays a live terminal dashboard with one row per agent, showing whether each agent is starting, paused, waiting, idle, syncing, preparing a workspace, working on a ticket, finalizing, recovering, retrying, or stopped. Active rows include the ticket ID and title, time in the current state, process or pane location, retry count, and most recently observed exit code when available. The dashboard starts with new ticket claims enabled. Pressing Shift-Tab toggles new claims on or off for all agents; pausing does not interrupt tickets that are already active. The header shows the current claim state, and agents waiting for permission display a paused state. The up and down arrows select agent and latest-comment rows. Enter opens the selected agent's action panel or the selected comment's complete detail view; long comments scroll with the arrow or Page Up and Page Down keys, and Escape returns to the dashboard. Stop interrupts that agent's hosted process, keeps its current ticket reserved, and parks the loop. Restart interrupts an active process when necessary and relaunches the reserved ticket, or resumes a parked or idle loop. Clean Workspace requires explicit confirmation, safely reopens any active ticket, runs `git reset --hard` followed by `git clean -fd`, and leaves the agent parked until Restart. A successful clean clears that agent's persistent recovery alert. Issues labelled `abacus:needs-user-attention`, including closed issues, appear in a persistent alert containing their IDs and titles until the label is removed. A periodically refreshed latest-comments log appears at the bottom with the configured number of issue, author, and comment entries. Warnings remain visible in the dashboard, and idle states are visually distinct from failures. `--verbose` (also accepted as `--debug` or `-v`) replaces the dashboard with timestamped state transitions, warnings, alerts, and every external command Abacus runs. When standard error is redirected, the default mode emits compact state transitions rather than terminal control sequences. Before starting any agent loop, Abacus pulls once when a single configured agent has a Dolt remote, then records the current Dolt `HEAD` with read-only `bd vc status`. Shared multi-agent databases are already live and are not pulled. On shutdown, Abacus prints that initial full Dolt commit in the final summary alongside elapsed time and per-agent counts for closed, reopened, blocked, and interrupted tickets.

Abacus runs continuously unless a finite execution option is selected. `--once` makes each agent claim and process at most one currently ready ticket; an agent exits immediately when no ticket is ready. `--drain` lets each agent continue claiming tickets until it observes no ready work, then exits after any active ticket finishes. Finite options fail rather than retrying orchestration errors forever, making them suitable for CI and scripts. `--check` runs the complete non-mutating preflight and exits without changing workspaces, claiming tickets, creating panes or processes, or printing a run summary. It validates the selected main repository, target registry and local refs, agent executable, workspace, Beads `no-git-ops` setting, and Dolt configuration, the OpenCode server address when applicable, and any requested tmux session/window target. These three options are mutually exclusive.

### Repository health

`--health` is a standalone, read-only diagnostic. From the selected main Git
repository it reports:

- whether target configuration, instruction files, and local target branches are valid;
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
otherwise it exits one. Target configuration must also be valid.

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
target audit/set, attention listing/resolution, and pruning) use the same rule.
Help, model discovery, and new-repository creation need no existing repository;
`--repo` is not supported with the latter two operations.

`--config` is removed. Load `<repo>/.abacus/targets.json` once at startup; all
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

`--target-branch <branch>` is a repeatable eligibility filter, never a destination
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
The read-only `--check` operation creates no locks.

Standalone commands:

```sh
abacus --check-ticket-targets [<issue-id> ...] [--repo <path>]
abacus --set-ticket-target <branch> <issue-id> [<issue-id> ...] [--repo <path>]
abacus --set-ticket-target <branch> <issue-id> --adopt-existing-branch --start-commit <full-commit-id>
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

`--health [--repo <path>]` checks target configuration, instruction files, and
local branches; invalid targets make readiness fail. It does not audit tickets.
The new-repository initializer writes and commits a config with `main` allowed,
and launchers explicitly select it. Planner and doctor must use
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
6. Start the selected local agent CLI interactively in tmux, or start an attached OpenCode Server client either directly or in tmux. In every mode, set `BEADS_ACTOR=<agent_name>` and pass the requested model and a prompt describing the issue and its ticket-state responsibilities. Use the controller-snapshotted target merge instructions when present; file presence overrides the default even when the file is empty. Pass the requested effort where the selected CLI exposes it; interactive OpenCode uses its configured or session-selected variant because its TUI has no variant CLI option.
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

Read the ticket with:

  bd show <issue_id> --include-comments --json

Work on the branch abacus/<issue_id> and satisfy the ticket's definition of done.
Commit your changes, then merge the branch into the latest local <target_branch> branch.

Follow any repository-specific merge instructions when they define a merge process.
Otherwise, use this basic merge strategy:

1. Check for a Beads merge slot with `bd merge-slot check --json`. If the response
   reports that no merge slot exists, continue without one; do not create one. If a
   slot exists, acquire it before merging, waiting and retrying while another agent
   holds it:

     until bd merge-slot acquire --holder "$BEADS_ACTOR"; do sleep 2; done

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
