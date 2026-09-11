# Abacus Implementation Plan

> **Document role:** Engineering plan, design constraints, and definition of
> done. Intended product behavior is normative in [SPEC.md](SPEC.md); user-facing
> setup and operation are organized under the [documentation index](docs/README.md).

## Contents

- [Goal](#goal)
- [Simplicity rules](#simplicity-rules)
- [Proposed shape](#proposed-shape)
- [End-to-end state machine](#end-to-end-state-machine)
- [Phase 1 — CLI contracts](#phase-1---lock-down-cli-contracts)
- [Phase 2 — Console and subprocess foundation](#phase-2---console-skeleton-and-subprocess-runner)
- [Phase 3 — Preflight](#phase-3---preflight-safety-checks)
- [Phase 4 — Claims and workspaces](#phase-4---claiming-and-workspace-preparation)
- [Phase 5 — Agent hosting](#phase-5---agent-processes-panes-and-prompt-delivery)
- [Phase 6 — Supervision and recovery](#phase-6---ticket-supervision-and-recovery)
- [Phase 7 — Verification and release](#phase-7---tests-documentation-and-release-smoke-test)
- [Definition of done](#definition-of-done)
- [Explicit non-goals](#explicit-non-goals-for-the-first-version)

## Goal

Build the smallest useful Abacus: a Unix-oriented C# console application that coordinates Beads, Git, one of four supported agent modes, and optional tmux hosting by running existing command-line tools.

Abacus should own only the orchestration state machine. It should not reimplement or directly integrate with the internals of any of those tools.

## Simplicity rules

- Use one .NET console application and the .NET standard library.
- Use `Process`/`ProcessStartInfo` to run `bd`, `git`, the selected `opencode`, `codex`, or `claude` executable, and `tmux`. Do not add vendor SDKs or Beads, Git, tmux, Dolt, or HTTP client libraries.
- Support exactly four agent modes: interactive OpenCode, interactive Codex, interactive Claude Code, and OpenCode Server attachment. Do not call agent server APIs.
- Accept `--remote-control` only for Claude Code. Keep Claude interactive and enable Remote Control with an explicit `<issue-id> • <issue-title>` session name. Do not implement the remote-control protocol in Abacus.
- Require one default `--model <model>` value per Abacus invocation. Accept repeatable `--reasoning-model <high|medium|low> <model>` routes and load the project enforcement toggle from `.abacus/reasoning.json`. Resolve the claimed ticket's exact reasoning label after its atomic claim and before Git mutation; quarantine missing strict labels and conflicting labels with user attention. In optional mode, absent labels and unmapped single labels use the default model. Accept one provider-specific `--effort <effort>` value, default it to `high`, and translate model and effort into the selected CLI's native arguments where supported. Preserve OpenCode's `provider/model` validation while allowing native Codex and Claude model identifiers. Interactive OpenCode 1.18.20 has no TUI variant option, so keep its model ID unchanged and let OpenCode use its configured or session-selected variant.
- Parse only the small amount of JSON/JSONL emitted by `bd` that Abacus needs: issue ID, issue title and status, direct-child status, Dolt identity, remote presence, and the comment fields and labels needed by the dashboard. Query Beads by label rather than importing its issue model when the dashboard needs attention alerts; use read-only `bd export` for the latest-comment snapshot so embedded and server-backed modes share one path.
- Pass ordinary command arguments through `ProcessStartInfo.ArgumentList`, not interpolated shell strings. Use a generated shell wrapper only where tmux needs a pane command and process-exit marker.
- Keep transient runtime state in memory; persist ticket execution bindings in Beads metadata. A temporary per-run directory may contain prompt files, pane wrapper scripts, and exit markers; there is no Abacus database.
- Run one asynchronous loop per configured agent. Do not introduce a scheduler, message bus, dependency-injection container, plugin model, web UI, or daemon.
- Target macOS/Linux only. tmux and POSIX shell behavior are explicit prerequisites for OpenCode, Codex, Claude, and pane-hosted OpenCode Server modes; direct OpenCode Server mode can supervise a child without tmux.
- Prefer clear failure and retry behavior over automatic repair of repositories, Beads configuration, or OpenCode servers.

## Proposed shape

```text
Abacus.sln
src/Abacus/
  Abacus.csproj
  Program.cs          # argument parsing, cancellation, startup, exit code
  CommandRunner.cs    # subprocess execution and concise command logging
  Options.cs          # agent mode, tmux target, model, server, repeated -a pairs
  Preflight.cs        # executable, tmux, workspace, Git, and Beads checks
  Beads.cs            # thin wrappers around bd commands and minimal JSON parsing
  Git.cs              # thin wrappers around git commands
  AgentCommand.cs     # explicit four-mode command construction
  AgentHost.cs        # common lifecycle contract for pane and direct runs
  DirectOpenCodeServerHost.cs # directly supervised attached-server processes
  TmuxAgentHost.cs    # pane creation, interruption, and cleanup
  AgentLoop.cs        # the single-agent state machine
  Prompt.cs           # renders the fixed prompt from SPEC.md
tests/Abacus.Tests/
  ...                 # parser/state tests plus fake-CLI integration tests
```

These files are boundaries for readability, not layers or generic interfaces. Concrete classes are sufficient.

## End-to-end state machine

The core claim/run cycle is summarized below; dashboard and recovery states
include pausing, synchronization, retries, and persistent halts:

```text
Waiting -> Claimed -> PreparingWorkspace -> RunningAgent -> Finalizing -> Waiting
                                      \-> Recover or Quarantine
```

Before starting any loop, acquire exclusive OS-backed ownership of each
workspace's Git administrative directory. Pull once when a single configured
agent has a Dolt remote, then record the current commit with read-only `bd vc status`. For a
multi-agent shared server, record its live commit without pulling. A failure
aborts before any ticket is claimed. Keep the full commit in memory for the
final summary; do not create a checkpoint commit. Ticket execution bindings are
persisted separately in Beads before branch preparation.

1. Inspect the workspace before looking for work. A dirty `abacus/<issue-id>` branch resumes that exact open issue without changing its files. Any dirty workspace that cannot be tied safely to a resumable issue stops that agent and remains untouched. A shared one-shot startup barrier holds every clean agent before ready lookup until all configured workspaces complete this recovery pass.
2. In single-agent mode, pull Beads before looking for work when a Dolt remote exists.
3. List matching work with `bd ready --unassigned --exclude-label gt:slot --limit 0 --json`, apply resolved-target eligibility, preserve Beads priority among eligible candidates, use the newest comment to break a highest-priority tie, skip each selected candidate when `bd show <id> --children --json` reports any unclosed direct child, and atomically claim the eligible issue with `bd update <id> --claim --json`, all with `BEADS_ACTOR=<agent name>`.
4. If no issue is ready, continuous mode sleeps and tries again; once/drain complete.
5. Revalidate ticket ownership and target, persist/read back the execution binding,
   and synchronize Beads when configured before branch preparation. Switch to a
   matching bound `abacus/<issue_id>` or create it from the recorded target commit.
   Refuse unbound existing branches until explicitly adopted.
6. Verify that a normal newly selected workspace is clean before the agent CLI starts. Preserve existing changes when resuming an interrupted issue workspace.
7. Render the target-aware SPEC.md prompt, replacing its default merge section with controller-snapshotted target instructions when present, and launch the selected mode. OpenCode, Codex, and Claude use a new interactive pane in the resolved tmux target. OpenCode Server uses `opencode run` and is directly supervised unless a tmux-related option was supplied.
8. Poll `bd show <issue_id> --json` while also watching the hosted agent run for exit.
9. When the ticket leaves `in_progress`, interrupt the agent CLI if it is still running and clean up its pane or direct process.
10. When the agent CLI exits while the ticket is still `in_progress`, warn, reopen the issue with a useful note, and clean up its hosted run.
11. After every agent exit, run `bd dolt push` when a remote is configured, then return to waiting.

Ordinary preparation failures and cancellation attempt to reopen the still-owned
claim with a reason. Target-validation failures instead block the owned ticket,
clear its assignee, add user attention, and verify the reason and synchronization.
Do not overwrite ownership or terminal-state races. Failed recovery halts the loop.
A validation claim consumes the one-claim budget in `--once` mode.

## Target routing and execution bindings

Implement the normative [ticket target contract](SPEC.md#ticket-targets) and
[target operations](docs/targets.md). Keep it shell-first: a small versioned JSON
allowlist, minimal Beads metadata, and Git CLI checks—not a scheduler or release
manager. Missing target metadata uses `defaultTarget` when enforcement is off;
strict enforcement requires explicit metadata. Target eligibility precedes priority
selection; invalid candidates are atomically claimed only to record an explicit
blocked/attention outcome. Persist and verify bindings before branch preparation.
OS-held workspace locks exclude competing runs. Never bypass another worktree's
branch checkout or infer a legacy branch binding. Provide read-only audits,
metadata setting, and explicit reviewed adoption. Update planner, doctor,
initializer, health, prompts, and fake-CLI/real-Git tests together.

The implementation boundary remains before managed landing: agents still merge
and close tickets. Do not add landing commands, receipts, or new completion/prune
semantics as part of target routing.

## Phase 1 - Lock down CLI contracts

Before building the loop, capture the exact behavior of the locally supported command versions.

### Work

- Record the minimum supported versions of `dotnet`, `bd`, `git`, `opencode`, `codex`, `claude`, and `tmux` in the README. The four-mode work was developed with Codex CLI 0.151.0, Claude Code 2.1.212, and OpenCode 1.18.20.
- In a disposable Beads repository, save representative outputs and exit codes for:
  - `bd ready --unassigned --exclude-label gt:slot --limit 0 --json` with and without ready work.
  - `bd show <ids...> --include-comments --json` for newest-comment tie-breaking.
  - `bd update <id> --claim --json` for success and a lost claim race.
  - `bd show <id> --json` for `in_progress`, `open`, `blocked`, and `closed` issues.
  - `bd dolt show --json` and `bd dolt remote list --json` with and without a remote.
  - failed pulls and pushes.
- Confirm that `opencode --prompt <prompt> --model <provider/model>` creates one full local interactive TUI session without corrupting the model ID; OpenCode uses its configured or session-selected variant because the TUI has no variant CLI option.
- Confirm that `codex --cd <workspace> --model <model> --config model_reasoning_effort=<effort> --approve-for-me <prompt>` starts an interactive TUI with the requested effort and automatic approval review rather than blocking command prompts.
- Confirm that `claude --model <model> --effort <effort> --permission-mode auto --name <name> [--remote-control '<issue-id> • <issue-title>'] <prompt>` starts an interactive session with the requested effort rather than print mode.
- Confirm that `opencode run <prompt> --model <provider/model> --variant <effort> --attach http://<server> --dir <workspace>` creates a new client session with the requested model variant and without any direct HTTP work in Abacus.
- Prove the shared tmux wrapper can write an exit-code marker after every supported interactive CLI exits and can be interrupted with `tmux send-keys ... C-c`.
- Turn the captured Beads JSON into test fixtures. Avoid broad DTOs; extract fields with `JsonDocument` so harmless schema additions do not matter.

### Exit criteria

- Every external command needed by the state machine has a known invocation, useful output, and understood failure behavior.
- Ambiguous cases such as “no ready issue” versus “Beads failed” are distinguishable.
- Shared-Dolt identity fields are known well enough to implement a reliable multi-agent preflight.

## Phase 2 - Console skeleton and subprocess runner

### Work

- Create a .NET 10 console project with nullable reference types enabled and no production NuGet dependencies.
- Bundle the `abacus-beads-planner`, `abacus-beads-doctor`, and
  `abacus-beads-attention` skills, plus the `abacus-git-check` agent-instruction
  audit, as executable resources. A standalone
  `abacus skills install [--repo <path>]` resolves the selected main Git checkout
  with the Git CLI and installs all
  four skills under `.agents/skills` without entering agent preflight or
  requiring normal run options. Stage the bundled contents before installation;
  if any bundled skill already exists, require one user confirmation before
  replacing those complete directories. Cancellation leaves all skills unchanged
  and unrelated skill directories remain untouched.
- Implement the exact CLI from the spec:

  ```text
  abacus new <project-name> --agents <agent-count>
  abacus init [--repo <main-checkout>]
  abacus skills install [--repo <main-checkout>]
  abacus targets check [<id> ...] [--repo <main-checkout>]
  abacus targets set <branch> <id> [<id> ...] [--repo <main-checkout>]
  abacus health [--repo <main-checkout>]
  abacus models
  abacus branches prune [--repo <main-checkout>]
  abacus attention list [--repo <main-checkout>]
  abacus attention resolve <issue-id> [--message <text>] [--reopen] [--repo <main-checkout>]

  abacus run [--mode <opencode|codex|claude|opencode-server>] \
    [--tmux-session <name>] [--tmux-window <name-or-index>] [--tmux-layout <layout>] \
    [--disown-tmux-session] \
    --model <model> \
    [--reasoning-model <high|medium|low> <model>] \
    [--effort <effort>] \
    [--remote-control] \
    [--repo <main-checkout>] [--target-filter <branch>] \
    [--label <label>] [--exclude-label <label>] \
    [--type <types>] [--priority <priority>] \
    [--ticket-timeout <duration>] \
    [--latest-comments <count>] \
    [--notify <off|attention|all>] [--notify-sound] \
    [--opencode-server <host:port>] \
    [--once | --drain] \
    [--verbose] \
    -a <agent_name> <git_workspace_path> [-a ...]
  ```

- Support standalone user-attention resolution: optionally add the exact
  supplied message with `bd comment`, then use `bd update` to remove the
  `abacus:needs-user-attention` label from the requested issue. When `--reopen`
  is present, use that same update to set the issue status to `open` and clear
  its assignee so it can be claimed again. Do not run normal
  preflight or require agent options for this operation.

- Support standalone repository helpers. `attention list` prints only
  the IDs returned by an unbounded Beads label query. `branches prune`
  queries all closed tickets and deletes only matching local
  `abacus/<issue-id>` branches, skipping branches checked out in worktrees.
  Neither command runs normal preflight or requires agent options.

- Reject a missing or malformed `--model` value, a malformed `--effort` value, `--remote-control` outside Claude mode, malformed or duplicate singular dispatch filters, malformed ticket timeouts, invalid mode/server/tmux combinations, other missing values, unknown options, duplicate agent names, duplicate canonical workspace paths, and zero agents. OpenCode model IDs use `provider/model`; Codex and Claude IDs must be nonempty and whitespace-free. Effort defaults to `high`; model and effort availability remain the selected CLI's responsibility. Dispatch labels are repeatable, priority is 0 through 4, and ticket timeouts are positive integer seconds, minutes, or hours.
- Implement `CommandRunner` around `ProcessStartInfo` with:
  - executable plus argument list;
  - working directory;
  - per-command environment variables, especially `BEADS_ACTOR`;
  - captured stdout/stderr and exit code;
  - cancellation that terminates the child process tree;
  - concise, agent-prefixed logging.
- Add Ctrl-C cancellation and one top-level error boundary.
- Support finite execution without a scheduler: `--once` processes at most one ticket per agent, `--drain` runs until agents observe an empty ready queue, and `preflight` exits after preflight without starting the application loop. Keep the run-length options mutually exclusive and reject both on `preflight` and fail fast on orchestration errors during finite runs.
- Add a standalone, read-only `health` report. Reuse the documented minimum
  versions while probing Git, Beads, OpenCode, Claude Code, Codex, and tmux;
  require at least one supported harness. Report Beads storage/concurrency,
  merge-slot availability and holder, available agent modes, every
  root-referenced Git worktree, whether additional worktrees make multi-agent
  workspaces possible, whether Beads `no-git-ops` is disabled, and whether all
  bundled skills are installed. Treat enabled `no-git-ops` as not ready and show
  the command that disables it. Warn—but do
  not fail—when no merge slot exists because the repository may provide another
  serialized merge process. Do not search the filesystem for separate clones or
  contact an OpenCode server.
- Add a standalone, read-only `models` report. Discover OpenCode IDs with
  `opencode models` and visible Codex IDs with `codex debug models`, group the
  results by harness, and isolate missing-tool or command failures. Report that
  Claude Code requires its interactive `/model` picker because its CLI exposes
  no non-interactive catalog command. Require no repository or agent options.
- Default to a dependency-free ANSI terminal dashboard with one state row per agent. Include ticket title, elapsed state time, process or pane, retry count, and last observed exit code; for pane-hosted runs also show the resolved tmux session and window names so users can attach from another shell. Distinguish idle polling from failure retries. Start with new claims enabled unless `--start-paused` is set, and let Shift-Tab pause or resume new ticket claims across all agents without interrupting active tickets; show the current claim state in the header and a paused state for agents waiting at the claim boundary. Let the operator select agent and latest-comment rows with the arrow keys. Enter opens an agent action panel or a complete, wrapped comment detail view with vertical scrolling. Support stopping one loop while retaining its active ticket reservation, restarting that loop and ticket, and explicitly confirmed cleanup with `git reset --hard` plus `git clean -fd`. Cleaning must safely reopen an active ticket first and leave the agent stopped. Persistently alert with the IDs and titles of issues labelled `abacus:needs-user-attention`, including closed issues, until the label is removed. Show a periodically refreshed latest-comments log at the bottom, defaulting to 8 entries with a validated `--latest-comments` count; put the issue ID, truncated issue title, and author on a colored header line, then wrap the uncolored, truncated comment across at most two indented lines beneath it. Color attention-labelled issue headers red, configured-agent headers green, and unrecognized-author headers cyan. Fall back to compact state-transition and alert lines when stderr is redirected, expose timestamped state, warning, and subprocess diagnostics through `--verbose`, and print the initial full Beads Dolt commit plus a per-agent outcome summary on shutdown. Do not add a general logging framework or configurable log sinks.
- Keep desktop notifications dependency-free and owned by the orchestrator. `--notify attention` reports new user-attention issues, blocked tickets, and persistent recovery failures; `--notify all` also reports all ticket outcomes and the final run summary. Use `osascript` on macOS and optional `notify-send` on Linux through `ProcessStartInfo.ArgumentList`. When sound is enabled, distinguish successful outcomes from attention or unsuccessful outcomes with positive and negative platform sounds. Treat delivery as best effort, deduplicate polled attention issues, and use a terminal bell fallback only when `--notify-sound` was requested.

### Exit criteria

- Argument parsing is covered by tests.
- Skill installation works from a subdirectory, requires confirmation before
  replacing existing bundled skills, removes obsolete files from confirmed
  replacements, and is covered by tests.
- Health parsing, version comparisons, Beads concurrency and merge-slot
  classification, worktree reporting, harness-mode availability, and skill
  presence are covered by fake-CLI tests.
- User-attention resolution parsing and its exact Beads argument list are
  covered by tests, including literal message passthrough and failure reporting.
- A fake executable test proves arguments with spaces and special characters are passed literally and `BEADS_ACTOR` is scoped to the child.
- `abacus --help` documents prerequisites and examples from SPEC.md.

## Phase 3 - Preflight safety checks

All checks happen before any ticket is claimed or agent run is created.

### Work

- Verify `bd`, `git`, and only the selected agent executable are available from `PATH`.
- Require tmux for OpenCode, Codex, and Claude modes. Resolve missing targets only after non-mutating preflight: default to `abacus - <safe-project-id>` and `Abacus Agents`, creating a detached session or window when absent. Own and remove a session only when this invocation created an implicit session and `--disown-tmux-session` was not supplied; never remove explicit or pre-existing sessions.
- Allow `--opencode-server` without tmux and do not look up or invoke tmux when no tmux-related option is supplied. Any tmux-related option selects pane hosting and may use the implicit session.
- For every agent workspace:
  - resolve the canonical absolute path and ensure it exists;
  - verify it is a Git worktree using `git -C <path> rev-parse`;
  - allow dirty workspaces at preflight; runtime inspection either safely resumes
    an interrupted bound issue or preserves the workspace and halts;
  - verify Beads can find and query its project from that directory;
  - reject enabled Beads `no-git-ops` with a clear correction command before any
    ticket is claimed or agent CLI is started;
  - inspect Dolt configuration and whether a remote is configured.
- For multiple agents, compare the normalized Dolt host, port, and database identity reported from every workspace. Require all agents to use the same shared Dolt database and refuse to start if identity is missing, local-only/separate, or different.
- For one agent, allow the normal local Beads database. Cache whether it has a remote so the loop knows whether to pull/push.
- If an OpenCode server is supplied, normalize `host:port` to an HTTP URL and perform only a CLI-level readiness probe if OpenCode offers one. Otherwise, let the first `opencode run --attach` fail clearly; do not add an HTTP client.

### Exit criteria

- Missing tmux for pane-hosted modes, duplicate workspaces, missing Beads projects, and unsafe multi-agent database configurations all fail before claims. Dirty workspaces are accepted here and recovered or preserved by the agent loop before normal dispatch. Missing tmux session/window targets are not failures because `run` creates them after preflight.
- A valid single-agent local setup and a valid multi-agent shared-Dolt setup pass.
- Preflight never mutates Git, Beads, tmux, or agent CLI state.
- `preflight` reports success immediately after this boundary and never claims work or starts an agent CLI.

## Phase 4 - Claiming and workspace preparation

### Work

- Implement `Beads.TryClaimReadyAsync` as a short list, select, and atomic-claim sequence:

  ```sh
  BEADS_ACTOR=<agent_name> bd ready --unassigned --exclude-label gt:slot --limit 0 --json
  BEADS_ACTOR=<agent_name> bd show <commented-highest-priority-ids...> --include-comments --json
  BEADS_ACTOR=<agent_name> bd show <selected-id> --children --json
  BEADS_ACTOR=<agent_name> bd update <selected-id> --claim --json
  ```

- Append configured `--label`, `--exclude-label`, `--type`, and `--priority` values literally to both the unassigned ready lookup and the same-agent assigned-ready fallback. Keep the built-in `gt:slot` exclusion. Apply resolved-target eligibility before priority/comment selection. Respect the priority ordering returned by Beads, then use the newest comment only to break a tie within the highest-priority group. If none of the tied issues has comments, select the first result. Before claiming, inspect that candidate's direct children and skip it when any child status is not `closed`; failed or malformed child data must not permit a claim. Claim an eligible selected ID atomically and refresh selection after a lost claim race or Dolt serialization conflict.

- In single-agent mode only, run `bd dolt pull` immediately before each claim attempt when a remote exists. A pull failure should log and delay the next attempt rather than claim against stale data.
- Check workspace cleanliness before every claim. If a workspace is dirty, require its current branch to be a valid `abacus/<issue-id>` branch, read that exact issue, and atomically claim it when it is open and unassigned or already assigned to the configured agent. Resume it without switching branches or changing tracked or untracked files. This recovery takes precedence over normal dispatch and ignores dispatch filters.
- At multi-agent startup, use one shared in-memory barrier so all workspaces finish their initial recovery inspection and resumable dirty workspaces complete their exact claims before any clean workspace performs a ready lookup. Release the barrier contribution for an agent that halts during recovery so other agents can continue.
- If a dirty workspace is not on a valid Abacus issue branch, its issue is not open, it belongs to another agent, or its exact claim fails, preserve the workspace, stop that agent, and raise a persistent alert. Never reset or clean a dirty workspace automatically.
- Treat “no ready issue” as idle, not as an error. Use one fixed polling interval (for example, five seconds) to avoid adding tuning options prematurely.
- Before both fresh and same-agent ready claims, use read-only Git branch/worktree
  ownership inspection to skip candidates checked out elsewhere. Keep searching
  without claiming/reopening those tickets. Fail closed on unreadable ownership;
  retain dirty-workspace recovery and Git's final checkout safety check. Regression
  tests must cover a clean blocked/reopened ticket returning to its owning worktree.
- After a claim, use Git CLI commands to:
  - verify the workspace is still clean;
  - switch to `abacus/<issue_id>` if it exists;
  - otherwise create `abacus/<issue_id>` from the durably recorded target starting commit;
  - verify the resulting branch name and cleanliness.
- Sanitize/validate issue IDs before using them in a branch name. Never interpolate an issue ID into a shell command.
- On ordinary branch-preparation failure, safely reopen/unassign the owned claim,
  verify recovery, and push when configured. Target/binding validation failures
  instead block with attention and a precise reason; preserve dirty files and
  halt unsafe recovery. Finite modes do not retry orchestration errors forever.

### Exit criteria

- Parallel fake-agent tests prove each atomic claim is handled by only one loop.
- A ready parent with an unclosed direct child is skipped without any claim attempt, while parents with no children or only closed children remain eligible.
- New branches start at the bound target commit. Existing branches require a
  matching execution binding and history; unbound legacy branches require adoption.
- Resumable dirty issue workspaces start the agent without losing changes. Ambiguous or unsafe dirty workspaces remain unchanged, never start an agent CLI, and raise a persistent alert.

## Phase 5 - Agent processes, panes, and prompt delivery

### Work

- Render the prompt in SPEC.md verbatim apart from substituting agent name, issue ID, and canonical workspace path. During preflight, snapshot the controller target registry and instruction files. Substitute the bound target into Git authority and the default merge section; use target-specific instructions when present. Treat even an empty file as an override so no default merge instructions reach that agent.
- Write the prompt and a small POSIX wrapper to the run's temporary directory. The wrapper should:
  - `cd` to the workspace;
  - export `BEADS_ACTOR`;
  - run the explicit command contract for the selected OpenCode, Codex, Claude, or OpenCode Server mode;
  - connect the selected CLI directly to the pane terminal rather than piping it through `tee`, because interactive modes require a TTY;
  - write the agent CLI exit code to an atomic exit-marker file;
  - remain alive briefly/idle until Abacus has observed the marker, so the pane does not disappear before cleanup.
- Enable `remain-on-exit` on the resolved agent window. Tag panes with pane-local tmux user options for Abacus ownership, repository project ID, agent, and issue. Prefer `respawn-pane` on a dead pane with matching ownership/project tags; if none is available or reuse loses a race, create a detached pane with `tmux split-window -d -P -F '#{pane_id}'`. Run the wrapper there, record the pane ID, and reapply the requested layout, defaulting to `tiled`.
- Give every managed pane a stable `<agent> • <issue-id>` title with `tmux select-pane -T` and disable application title changes for that pane so the selected CLI cannot replace the label.
- When `--opencode-server` is supplied without tmux, start one `opencode run --attach` child directly per agent using `ProcessStartInfo.ArgumentList`, the agent workspace, and `BEADS_ACTOR`. Drain stdout and stderr asynchronously to preserve the dashboard and prevent blocked pipes.
- Keep one small agent host boundary so ticket supervision can observe exit and perform idempotent cleanup for either a pane or a direct process. Use one explicit switch-based command builder for the four known modes; this is not a plugin system.
- Interrupt direct children, wait a short grace period, then terminate the process tree if needed.
- Do not use tmux control mode or a tmux protocol library. All lifecycle operations are CLI commands using the recorded pane ID.
- Implement idempotent, best-effort tmux cleanup: send Ctrl-C, allow a short grace period, then force the recorded managed pane into a dead reusable state when necessary. Remove run files and continue finalization regardless of tmux command results. Initialization failures may remove their partially initialized pane. Never target a pane ID that Abacus did not record at launch.
- Remove prompt, wrapper, and marker files when their run ends.

### Exit criteria

- Each configured agent gets a distinct pane or direct process, workspace, actor environment, prompt, and selected CLI session using the model passed to Abacus.
- All four modes use only their documented command-line tools; Abacus does not integrate with their APIs or protocols.
- Ctrl-C and startup failures do not leave Abacus-created panes or direct processes behind.

## Phase 6 - Ticket supervision and recovery

### Work

- While the agent CLI runs, poll `bd show <issue_id> --json` at the same small fixed interval used for idle polling.
- Handle ticket states directly:
  - `in_progress`: keep monitoring;
  - `closed`, `open`, or `blocked`: stop the agent CLI and finalize;
  - missing/unparseable/unknown: warn and retry a limited number of consecutive polls without changing the ticket.
- Watch the pane exit marker or direct child exit state in parallel with status polling.
- If the agent CLI exits and the ticket remains `in_progress`, log a warning and reopen it with a note containing the agent name and process exit code.
- After every agent exit or forced stop, run `bd dolt push` when the project has a remote. A push failure must be visible and retried a small bounded number of times, but must not misreport the ticket as completed.
- On Abacus shutdown, interrupt all active agent runs. For any ticket still `in_progress`, attempt to reopen it with an “Abacus shut down” note, push if configured, and then remove the pane or direct process.
- When `--ticket-timeout` is configured, measure from successful agent-host startup, attempt bounded host cleanup at the deadline, then use the same terminal-state-preserving reopen verification and push path. Do not count the timeout as a user shutdown interruption.
- Isolate loop failures: one agent's transient command failure should be logged and delayed without crashing other loops. A failure that invalidates a startup invariant, such as a missing workspace, should stop Abacus with a clear error.

### Exit criteria

- All three agent-owned terminal states from SPEC.md end the selected agent run.
- Unexpected agent CLI exit reliably returns `in_progress` work to `open` and pushes it when applicable.
- Status change and process-exit races are deterministic and do not overwrite an agent's final `closed`, `open`, or `blocked` state.

## Phase 7 - Tests, documentation, and release smoke test

### Automated tests

- Unit-test option parsing, prompt rendering, branch-name validation, Beads JSON extraction, and state transitions.
- Put fake `bd`, `git`, `opencode`, `codex`, `claude`, and `tmux` shell executables first on `PATH` for integration tests. Have them record calls and return scripted fixtures; this tests the real subprocess boundary without running agents.
- Cover at least:
  - no ready work followed by a claim;
  - two agents claiming concurrently;
  - exact-ticket recovery for a dirty issue workspace without reset or clean;
  - a clean agent cannot query ready work until another agent's interrupted workspace has reserved its issue;
  - preservation and halt for ambiguous or unsafe dirty workspaces;
  - mismatched Dolt databases rejection;
  - required and malformed model option handling;
  - exact OpenCode, Codex, Claude, pane-attached server, and direct-attached server command construction with the same requested model for every agent;
  - Codex and Claude interactive invocation, workspace, prompt, actor, model, and non-blocking permission behavior;
  - attached-server startup and cleanup without tmux installed or invoked;
  - successful close, agent-requested reopen, and blocked completion;
  - unexpected agent CLI exit while `in_progress`;
  - remote pull/push behavior and failures;
  - per-agent stop and restart preserve an active ticket reservation, while confirmed cleanup reopens the ticket and discards tracked and untracked workspace changes;
  - selecting a latest-comment row opens its complete wrapped text and supports scrolling without truncation;
  - Ctrl-C cleanup.
  - once, drain, and preflight-only process exit behavior.

### Documentation

- Show default and ticket-resolved model/effort, actual checkout branch/detached
  commit, and idle dirty-workspace markers in the dashboard. Reuse its monitoring
  cycle for read-only Git snapshots, clear failed snapshots to unknown, and test
  both rendering and real Git status parsing. Label OpenCode TUI effort requested.
- Add a README containing installation (`dotnet publish`), prerequisites, both usage examples from SPEC.md, how shared Dolt is validated, branch behavior, logs, and shutdown behavior.
- State explicitly that normal orchestration does not create worktrees or
  configure Beads/Dolt; the standalone new-repository initializer is the only
  setup exception. Abacus may create its resolved tmux session/window but does
  not start OpenCode servers,
  merge branches, or decide ticket outcomes.
- Document the exact shared agent prompt, its basic default merge process, optional
  merge-slot behavior, and how `.abacus/merge-instructions.md` replaces it.

### Manual smoke test

1. Create a disposable Git repository and Beads project, run `abacus init`,
   review `.abacus/targets.json`, create a small ticket, and audit its target.
2. Start a named tmux session and run one agent in each of the OpenCode, Codex, and Claude modes.
3. Verify claim, branch creation, selected model, prompt, completion-state detection, pane cleanup, and push behavior.
4. Repeat without tmux using two distinct worktrees sharing one Dolt database
   and an existing OpenCode server; pass `--repo <main-checkout>` explicitly.
5. Kill each selected agent CLI mid-ticket and verify the warning, reopen, push, and retry path.

### Exit criteria

- `dotnet test` passes without requiring real Beads, agent CLI, or tmux sessions.
- Both manual smoke paths satisfy SPEC.md end to end.
- A self-contained executable can be produced with `dotnet publish` and invoked as `abacus`.

## Definition of done

- `abacus skills install [--repo <path>]` installs all four bundled skills at
  the selected main Git checkout without starting preflight or agent loops, and requires confirmation before
  it replaces existing bundled skill directories.
- `abacus health` reports project readiness without mutating it and fails when
  `no-git-ops` is enabled, no single-agent mode is runnable, or a bundled skill
  is missing, or main-repository/target configuration validation fails.
- `abacus models` reports discoverable model IDs by harness without requiring
  Beads, Git, tmux, a model, or an agent configuration.
- `abacus branches prune` removes local Abacus issue branches for
  closed tickets while preserving non-Abacus, remote, and checked-out branches.
- `abacus attention list` prints the IDs of all attention-labelled
  tickets without starting orchestration.
- `abacus attention resolve` removes the attention label from one issue,
  optionally records the user's response, and can reopen and unassign the issue
  with `--reopen`, without starting agent orchestration.
- The CLI and prompt match SPEC.md.
- `--mode` selects exactly one of OpenCode, Codex, Claude, or OpenCode Server; server attachment requires explicit `--mode opencode-server`.
- `--model <model>` is required as the fallback. Each selected agent instance receives either that model or the model mapped from its ticket's single reasoning label.
- `--effort <effort>` defaults to `high`. Codex, Claude Code, and OpenCode Server receive the equivalent native effort or variant selection. Interactive OpenCode uses its configured or session-selected variant until the TUI exposes a variant CLI option.
- `--remote-control` keeps Claude Code interactive while exposing its CLI-managed Remote Control feature; it is rejected in Codex and both OpenCode modes.
- Optional dispatch filters limit every fresh and same-agent resumed ready claim without reimplementing Beads query semantics.
- Optional ticket timeouts stop the hosted run and safely reopen and synchronize work that remains `in_progress`, while preserving terminal-state races.
- Optional Abacus-owned desktop notifications report attention and ticket outcomes consistently across every agent mode without configuring the selected agent CLI.
- Every agent uses a unique validated workspace and either a dedicated Abacus-owned tmux pane or directly supervised attached process.
- Multi-agent execution is impossible unless all workspaces resolve to the same shared Dolt database.
- Claims are atomic and attributed with `BEADS_ACTOR`.
- Git branch preparation and clean-workspace enforcement happen before normal agent starts; interrupted issue recovery preserves and reuses the dirty workspace.
- Every agent is launched only through its CLI; OpenCode Server attachment remains the only direct non-tmux host.
- Ticket transitions control session lifetime exactly as specified.
- Unexpected exits reopen rather than complete work.
- Remote pull/push behavior matches the single-agent/shared-database rules in SPEC.md.
- Shutdown and failure paths do not strand `in_progress` tickets, make a bounded
  best-effort attempt to stop Abacus-created tmux panes, and verify directly
  supervised process cleanup.

## Explicit non-goals for the first version

- Creating, deleting, or repairing Git worktrees/clones during orchestration;
  only the standalone new-repository initializer creates worktrees.
- Setting up or migrating Beads/Dolt databases and remotes outside the
  standalone new-repository initializer.
- Starting or managing the requested tmux session, tmux window, or OpenCode server.
- Direct Codex app-server, Claude Remote Control, or other Git, tmux, Dolt, Beads, OpenCode, Codex, or Claude API/protocol integrations beyond invoking their supported CLI commands.
- A persistent queue, dashboard, web service, general workflow configuration language, dynamic agent pool, or automatic scaling.
- Interpreting ticket content, deciding whether work is correct, or performing the merge for the agent.
- Supporting Windows.

### Existing-repository setup and simplified identity

- `abacus init` validates existing Git/Beads setup and local targets, installs
  bundled skills with overwrite confirmation, and creates an absent default
  targets config. Preserve existing config and reject uninitialized Beads.
- Keep setup non-destructive: no automatic branch creation, ticket assignment,
  Beads setting changes, staging, or commits. Print health/audit next steps.
- Remove repository IDs from target config and execution bindings. Ignore legacy
  ID fields for compatibility; target/ref/start-commit/policy checks remain.

### Optional ticket target enforcement

- Config defaults: `enforceTargetBranch: false`, `defaultTarget: "main"`.
- Resolve absent metadata to the default only when unenforced; explicit invalid
  metadata still fails. Keep the same resolution for audits, filtering, binding,
  policy selection, and every agent prompt without stamping `abacus_target`.
- Strict mode retains quarantine for missing targets. Existing bindings cannot
  be silently redirected by a default change. Doctor/planner respect this policy.

### Explicit main repository selection

- Use shared `--repo <main-checkout>` selection for runs
  and repository-scoped standalone commands. Without an override, cwd must be
  inside the main checkout. Reject linked-worktree controller roots; never infer
  a controller from agent workspaces. Agent `-a` worktrees remain supported.
- Load targets only from `<repo>/.abacus/targets.json`. Run standalone Beads
  maintenance at that selected repository, not the invocation directory.
- New-project run configs must select `repo` relative to their directory;
  explicit --config paths work from outside Git.
  Help, models, and new-project creation need no existing repo.

### Command-oriented CLI

- Require bare operation names and an explicit `run`; bare invocation prints help.
- Parse options in their command scope, consuming values before interpreting any
  other tokens. Support `--`, single-value `--option=value`, and focused help.
- No compatibility aliases or implicit OpenCode Server mode. Keep `-a`, `-v`,
  and `-h` as documented short options for agent, verbosity, and help.
- Keep existing command handlers and shell-first integrations; use a small
  standard-library dispatcher rather than a CLI framework.
- Update bundled skills, generated configs, shell demos, and all documentation
  alongside parser and process-boundary regression tests.

### Structured events, stdio control, and intro

- Keep the existing output/state and control boundaries. Add a small synchronized
  JSONL reporter, not a logging framework, message bus, daemon, or external API.
- Mirror structured events to an append-only flushed file and/or stdout. Include
  structured state, alerts, comments, ticket outcomes, and final lifecycle events.
- Run-only `--stdio` accepts correlated JSONL commands using ClaimGate and
  AgentControl. Require explicit confirmation for destructive workspace cleanup,
  reject malformed input, and use normal cleanup on EOF/shutdown/output failure.
  Support `--start-paused` in both the TUI and stdio, initializing the header and
  claim gate consistently; reject it when no resume control is available. Keep
  finite exit independent of an open stdin pipe.
- Add a short ASCII animation before interactive ConsoleOutput construction,
  with a single bundled welcome/jingle audio mix, key skip, `--no-intro`,
  opt-in `--tui-audio`, no-color and narrow-terminal handling. Keep playback best
  effort, let it finish after natural animation completion, and stop it on skip
  or cancellation. Never render or play intro/TUI media for redirected or
  non-interactive modes.
- Test serialization/concurrent ordering, file mirroring, parser scope, controls,
  EOF/finite process exits, startup errors, and intro gating with fake tools.
  Document the versioned stream and accepted-versus-completed action semantics.

### Versioned GitHub releases

- Keep release automation shell-first: a dispatch-only local helper, shared
  version validation, native packaging/version smoke tests, and a GitHub Actions
  matrix for Linux/macOS x64/ARM64.
- Accept a manual version input and pin the dispatch commit for every job.
  Default source builds to 0.0.0-dev; embed the requested release version via
  MSBuild and read it through the standalone version command.
- Prepare changelog metadata without tracked mutations. Gate the write-enabled
  finalizer on all tests and native archive checks. Verify the release branch
  still matches tested source; commit only the changelog rollover and push its
  direct-child commit plus annotated tag atomically with an exact branch lease.
- Publish checksums and all archives through a draft. Bind retry ownership to
  the workflow run, source parent, exact changelog, and release-branch ancestry.
  Resume only that run's draft and leave already-published releases unchanged.
- Cover read-only dispatch, failed validation/build prerequisites, branch
  changes, rejected atomic pushes, partial uploads, wrong-run retries, and
  idempotent publication using disposable local Git remotes and a fake gh CLI.
- Document workflow permissions, protected-branch limitations, failed-job reruns,
  and the non-atomic boundary between Git finalization and Release publication.

### Saved run configuration editor

- Keep a versioned JSON-to-CLI boundary using the standard library and existing
  run validation; no new configuration framework or TUI dependencies.
- Provide a keyboard-driven config editor with all run fields, named agent
  add/edit/remove, Save/Save As, overwrite/discard confirmation, and draft warnings.
- Preserve CLI precedence (agent/filter list replacement; reasoning routes per
  tier) and config-relative paths, rebasing paths on Save As. Support false boolean
  overrides and read-only preflight of saved runs without run-only controls.
- Resolve one --config and its optional baseConfig single-parent inheritance
  before CLI overrides. Keep per-source path bases; replace agent/filter lists,
  merge reasoning routes per tier, and support null/false/empty overrides.
  Validate shapes per file and runtime requirements after composition; detect
  cycles and cap depth. Preserve/rebase baseConfig on editor Save As.
- For interactive run without --config, report missing required options, discover
  only top-level valid run JSON drafts in cwd, and allow one selection. Revalidate
  once and report remaining omissions before exiting. Fully specified/invalid CLI,
  explicit configs, preflight, stdio, verbose, and redirected I/O never prompt.
- Generate one shared abacus_base.json plus harness configs referencing it via
  baseConfig, without shell launchers. The base defaults to startPaused: true,
  notify: all, notifySound: true, and tuiAudio: true. Direct users to abacus run
  from the project root and explicit --config for non-interactive use.
- Test inheritance, cycles, missing bases, draft saves, path rebasing, CLI scope,
  picker success/failure/cancellation, non-interactive process behavior, and
  generated configs through the picker and explicit paths outside the project;
  assert that new creates no launcher scripts.
