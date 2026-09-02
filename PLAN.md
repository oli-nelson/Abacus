# Abacus Implementation Plan

## Goal

Build the smallest useful Abacus: a Unix-oriented C# console application that coordinates Beads, Git, one of four supported agent modes, and optional tmux hosting by running existing command-line tools.

Abacus should own only the orchestration state machine. It should not reimplement or directly integrate with the internals of any of those tools.

## Simplicity rules

- Use one .NET console application and the .NET standard library.
- Use `Process`/`ProcessStartInfo` to run `bd`, `git`, the selected `opencode`, `codex`, or `claude` executable, and `tmux`. Do not add vendor SDKs or Beads, Git, tmux, Dolt, or HTTP client libraries.
- Support exactly four agent modes: interactive OpenCode, interactive Codex, interactive Claude Code, and OpenCode Server attachment. Do not call agent server APIs.
- Accept `--remote` only for Claude Code. Keep Claude interactive and enable Remote Control with an explicit `<issue-id> • <issue-title>` session name. Do not implement the remote-control protocol in Abacus.
- Require one `--model <model>` value per Abacus invocation. Accept one provider-specific `--effort <effort>` value, default it to `high`, and translate model and effort into the selected CLI's native arguments where supported. Preserve OpenCode's `provider/model` validation while allowing native Codex and Claude model identifiers. Interactive OpenCode 1.18.20 has no TUI variant option, so keep its model ID unchanged and let OpenCode use its configured or session-selected variant.
- Parse only the small amount of JSON emitted by `bd --json` that Abacus needs: issue ID, issue title and status, Dolt identity, and remote presence. Query Beads by label rather than importing its issue model when the dashboard needs attention alerts.
- Pass ordinary command arguments through `ProcessStartInfo.ArgumentList`, not interpolated shell strings. Use a generated shell wrapper only where tmux needs a pane command and process-exit marker.
- Keep state in memory. A temporary per-run directory may contain prompt files, pane wrapper scripts, and exit markers; there is no Abacus database.
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

Each agent loop has only these states:

```text
Waiting -> Claimed -> PreparingWorkspace -> RunningAgent -> Finalizing -> Waiting
                                      \-> ReopenOnFailure -------^
```

1. Clean the workspace with `git reset --hard HEAD` and `git clean -fd` before looking for work.
2. In single-agent mode, pull Beads before looking for work when a Dolt remote exists.
3. Run `bd ready --claim --exclude-label gt:slot --json` in the agent workspace with `BEADS_ACTOR=<agent name>`.
4. If no issue is ready, sleep for a small fixed interval and try again.
5. Switch to existing branch `abacus/<issue_id>`, or create it if absent.
6. Verify that the workspace is clean before the agent CLI starts.
7. Render the SPEC.md prompt and launch the selected mode. OpenCode, Codex, and Claude use a new interactive pane in the requested tmux target. OpenCode Server uses `opencode run` and is directly supervised unless tmux was explicitly supplied.
8. Poll `bd show <issue_id> --json` while also watching the hosted agent run for exit.
9. When the ticket leaves `in_progress`, interrupt the agent CLI if it is still running and clean up its pane or direct process.
10. When the agent CLI exits while the ticket is still `in_progress`, warn, reopen the issue with a useful note, and clean up its hosted run.
11. After every agent exit, run `bd dolt push` when a remote is configured, then return to waiting.

Any failure after a successful claim but before the agent changes ticket state must attempt to return the issue to `open` with an appended reason. This prevents an orchestration error from stranding work in `in_progress`.

## Phase 1 - Lock down CLI contracts

Before building the loop, capture the exact behavior of the locally supported command versions.

### Work

- Record the minimum supported versions of `dotnet`, `bd`, `git`, `opencode`, `codex`, `claude`, and `tmux` in the README. The four-mode work was developed with Codex CLI 0.151.0, Claude Code 2.1.212, and OpenCode 1.18.20.
- In a disposable Beads repository, save representative outputs and exit codes for:
  - `bd ready --claim --exclude-label gt:slot --json` with and without ready work.
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
  `abacus-beads-attention` skills as executable resources. A standalone
  `abacus --install-skills` resolves the current Git root with the Git CLI and installs all
  three skills under `.agents/skills` without entering agent preflight or
  requiring normal run options. Stage the bundled contents before installation;
  if any bundled skill already exists, require one user confirmation before
  replacing those complete directories. Cancellation leaves all skills unchanged
  and unrelated skill directories remain untouched.
- Implement the exact CLI from the spec:

  ```text
  abacus --install-skills
  abacus --health

  abacus [--mode <opencode|codex|claude|opencode-server>] \
    [--tmux-session <name> [--tmux-window <name-or-index>] [--tmux-layout <layout>]] \
    --model <model> \
    [--effort <effort>] \
    [--remote] \
    [--label <label>] [--exclude-label <label>] \
    [--type <types>] [--priority <priority>] \
    [--ticket-timeout <duration>] \
    [--opencode-server <host:port>] \
    [--once | --drain | --check] \
    [--verbose] \
    -a <agent_name> <git_workspace_path> [-a ...]
  ```

- Reject a missing or malformed `--model` value, a malformed `--effort` value, `--remote` outside Claude mode, malformed or duplicate singular dispatch filters, malformed ticket timeouts, invalid mode/server/tmux combinations, other missing values, unknown options, duplicate agent names, duplicate canonical workspace paths, and zero agents. OpenCode model IDs use `provider/model`; Codex and Claude IDs must be nonempty and whitespace-free. Effort defaults to `high`; model and effort availability remain the selected CLI's responsibility. Dispatch labels are repeatable, priority is 0 through 4, and ticket timeouts are positive integer seconds, minutes, or hours.
- Implement `CommandRunner` around `ProcessStartInfo` with:
  - executable plus argument list;
  - working directory;
  - per-command environment variables, especially `BEADS_ACTOR`;
  - captured stdout/stderr and exit code;
  - cancellation that terminates the child process tree;
  - concise, agent-prefixed logging.
- Add Ctrl-C cancellation and one top-level error boundary.
- Support finite execution without a scheduler: `--once` processes at most one ticket per agent, `--drain` runs until agents observe an empty ready queue, and `--check` exits after preflight without starting the application loop. Treat the modes as mutually exclusive and fail fast on orchestration errors during finite runs.
- Add a standalone, read-only `--health` report. Reuse the documented minimum
  versions while probing Git, Beads, OpenCode, Claude Code, Codex, and tmux;
  require at least one supported harness. Report Beads storage/concurrency,
  available agent modes, every root-referenced Git worktree, whether additional
  worktrees make multi-agent workspaces possible, and whether all bundled skills
  are installed. Do not search the filesystem for separate clones or contact an
  OpenCode server.
- Default to a dependency-free ANSI terminal dashboard with one state row per agent. Include ticket title, elapsed state time, process or pane, retry count, and last observed exit code; distinguish idle polling from failure retries. Persistently alert with the IDs and titles of issues labelled `abacus:needs-user-attention`, including closed issues, until the label is removed. Fall back to compact state-transition and alert lines when stderr is redirected, expose timestamped state, warning, and subprocess diagnostics through `--verbose`, and print a per-agent outcome summary on shutdown. Do not add a general logging framework or configurable log sinks.

### Exit criteria

- Argument parsing is covered by tests.
- Skill installation works from a subdirectory, requires confirmation before
  replacing existing bundled skills, removes obsolete files from confirmed
  replacements, and is covered by tests.
- Health parsing, version comparisons, Beads concurrency classification,
  worktree reporting, harness-mode availability, and skill presence are covered
  by fake-CLI tests.
- A fake executable test proves arguments with spaces and special characters are passed literally and `BEADS_ACTOR` is scoped to the child.
- `abacus --help` documents prerequisites and examples from SPEC.md.

## Phase 3 - Preflight safety checks

All checks happen before any ticket is claimed or agent run is created.

### Work

- Verify `bd`, `git`, and only the selected agent executable are available from `PATH`.
- Require tmux for OpenCode, Codex, and Claude modes. When a tmux session is supplied, verify `tmux` is executable and the session exists; if `--tmux-window` is supplied, verify that the named or indexed window exists in that session. Abacus must not create or own either one.
- Allow `--opencode-server` without tmux and do not look up or invoke tmux in that configuration. Reject `--tmux-window` and `--tmux-layout` unless `--tmux-session` is also supplied.
- For every agent workspace:
  - resolve the canonical absolute path and ensure it exists;
  - verify it is a Git worktree using `git -C <path> rev-parse`;
  - verify `git status --porcelain` is empty;
  - verify Beads can find and query its project from that directory;
  - inspect Dolt configuration and whether a remote is configured.
- For multiple agents, compare the normalized Dolt host, port, and database identity reported from every workspace. Require all agents to use the same shared Dolt database and refuse to start if identity is missing, local-only/separate, or different.
- For one agent, allow the normal local Beads database. Cache whether it has a remote so the loop knows whether to pull/push.
- If an OpenCode server is supplied, normalize `host:port` to an HTTP URL and perform only a CLI-level readiness probe if OpenCode offers one. Otherwise, let the first `opencode run --attach` fail clearly; do not add an HTTP client.

### Exit criteria

- Missing tmux for local mode, invalid explicit tmux targets, duplicate workspaces, missing Beads projects, and unsafe multi-agent database configurations all fail before claims. Dirty workspaces are accepted here and cleaned by the agent loop before claiming.
- A valid single-agent local setup and a valid multi-agent shared-Dolt setup pass.
- Preflight never mutates Git, Beads, tmux, or agent CLI state.
- `--check` reports success immediately after this boundary and never claims work or starts an agent CLI.

## Phase 4 - Claiming and workspace preparation

### Work

- Implement `Beads.TryClaimReadyAsync` as a thin call to:

  ```sh
  BEADS_ACTOR=<agent_name> bd ready --claim --exclude-label gt:slot --json
  ```

- Append configured `--label`, `--exclude-label`, `--type`, and `--priority` values literally to both the atomic ready claim and the same-agent assigned-ready fallback. Keep the built-in `gt:slot` exclusion.

- In single-agent mode only, run `bd dolt pull` immediately before each claim attempt when a remote exists. A pull failure should log and delay the next attempt rather than claim against stale data.
- Check workspace cleanliness before every claim. If a workspace is dirty, warn, discard tracked changes with `git reset --hard HEAD`, remove untracked non-ignored files and directories with `git clean -fd`, and verify the result is clean before claiming.
- If automatic cleanup fails or leaves the workspace dirty, stop Abacus with a clear startup-invariant error rather than repeatedly claiming and reopening work.
- Treat “no ready issue” as idle, not as an error. Use one fixed polling interval (for example, five seconds) to avoid adding tuning options prematurely.
- After a claim, use Git CLI commands to:
  - verify the workspace is still clean;
  - switch to `abacus/<issue_id>` if it exists;
  - otherwise create `abacus/<issue_id>` from the workspace's current HEAD;
  - verify the resulting branch name and cleanliness.
- Sanitize/validate issue IDs before using them in a branch name. Never interpolate an issue ID into a shell command.
- If branch preparation fails, reopen and unassign the claimed issue with `bd update <id> --status open --assignee "" --append-notes <reason> --json`, push if configured, and return to waiting.

### Exit criteria

- Parallel fake-agent tests prove each atomic claim is handled by only one loop.
- Existing and new issue branches both work.
- Dirty or unusable workspaces never start an agent CLI and do not leave the issue claimed.

## Phase 5 - Agent processes, panes, and prompt delivery

### Work

- Render the prompt in SPEC.md verbatim apart from substituting agent name, issue ID, and canonical workspace path.
- Write the prompt and a small POSIX wrapper to the run's temporary directory. The wrapper should:
  - `cd` to the workspace;
  - export `BEADS_ACTOR`;
  - run the explicit command contract for the selected OpenCode, Codex, Claude, or OpenCode Server mode;
  - connect the selected CLI directly to the pane terminal rather than piping it through `tee`, because interactive modes require a TTY;
  - write the agent CLI exit code to an atomic exit-marker file;
  - remain alive briefly/idle until Abacus has observed the marker, so the pane does not disappear before cleanup.
- Create a detached pane in the existing session's current window, or the explicit `session:window` target supplied through `--tmux-window`, with `tmux split-window -d -P -F '#{pane_id}'`; run the wrapper there and record the returned pane ID. When `--tmux-layout` is supplied, reapply that validated built-in layout after each split.
- Give every created pane a stable `<agent> • <issue-id>` title with `tmux select-pane -T` and disable application title changes for that pane so the selected CLI cannot replace the label.
- When `--opencode-server` is supplied without tmux, start one `opencode run --attach` child directly per agent using `ProcessStartInfo.ArgumentList`, the agent workspace, and `BEADS_ACTOR`. Drain stdout and stderr asynchronously to preserve the dashboard and prevent blocked pipes.
- Keep one small agent host boundary so ticket supervision can observe exit and perform idempotent cleanup for either a pane or a direct process. Use one explicit switch-based command builder for the four known modes; this is not a plugin system.
- Interrupt direct children, wait a short grace period, then terminate the process tree if needed.
- Do not use tmux control mode or a tmux protocol library. All lifecycle operations are CLI commands using the recorded pane ID.
- Implement idempotent cleanup: send Ctrl-C, allow a short grace period, then `tmux kill-pane` if the pane remains. Never target panes that Abacus did not create.
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
- When `--ticket-timeout` is configured, measure from successful agent-host startup, stop and verify cleanup at the deadline, then use the same terminal-state-preserving reopen verification and push path. Do not count the timeout as a user shutdown interruption.
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
  - dirty workspace cleanup before claiming;
  - mismatched Dolt databases rejection;
  - required and malformed model option handling;
  - exact OpenCode, Codex, Claude, pane-attached server, and direct-attached server command construction with the same requested model for every agent;
  - Codex and Claude interactive invocation, workspace, prompt, actor, model, and non-blocking permission behavior;
  - attached-server startup and cleanup without tmux installed or invoked;
  - successful close, agent-requested reopen, and blocked completion;
  - unexpected agent CLI exit while `in_progress`;
  - remote pull/push behavior and failures;
  - Ctrl-C cleanup.
  - once, drain, and preflight-only process exit behavior.

### Documentation

- Add a README containing installation (`dotnet publish`), prerequisites, both usage examples from SPEC.md, how shared Dolt is validated, branch behavior, logs, and shutdown behavior.
- State explicitly that Abacus does not create worktrees, configure Beads/Dolt, start tmux, start OpenCode servers, merge branches, or decide ticket outcomes.
- Document the exact shared agent prompt and the requirement that repository-specific instructions define the serialized merge process.

### Manual smoke test

1. Create a disposable Git repository and Beads project with one small ticket.
2. Start a named tmux session and run one agent in each of the OpenCode, Codex, and Claude modes.
3. Verify claim, branch creation, selected model, prompt, completion-state detection, pane cleanup, and push behavior.
4. Repeat without tmux using two distinct worktrees sharing one Dolt database and an existing OpenCode server.
5. Kill each selected agent CLI mid-ticket and verify the warning, reopen, push, and retry path.

### Exit criteria

- `dotnet test` passes without requiring real Beads, agent CLI, or tmux sessions.
- Both manual smoke paths satisfy SPEC.md end to end.
- A self-contained executable can be produced with `dotnet publish` and invoked as `abacus`.

## Definition of done

- `abacus --install-skills` installs all three bundled skills at the current Git repository
  root without starting preflight or agent loops, and requires confirmation before
  it replaces existing bundled skill directories.
- `abacus --health` reports project readiness without mutating it and fails when
  no single-agent mode is runnable or a bundled skill is missing.
- The CLI and prompt match SPEC.md.
- `--mode` selects exactly one of OpenCode, Codex, Claude, or OpenCode Server; legacy `--opencode-server` implies server mode.
- `--model <model>` is required and every selected agent instance receives that exact model ID.
- `--effort <effort>` defaults to `high`. Codex, Claude Code, and OpenCode Server receive the equivalent native effort or variant selection. Interactive OpenCode uses its configured or session-selected variant until the TUI exposes a variant CLI option.
- `--remote` keeps Claude Code interactive while exposing its CLI-managed Remote Control feature; it is rejected in Codex and both OpenCode modes.
- Optional dispatch filters limit every fresh and same-agent resumed ready claim without reimplementing Beads query semantics.
- Optional ticket timeouts stop the hosted run and safely reopen and synchronize work that remains `in_progress`, while preserving terminal-state races.
- Every agent uses a unique validated workspace and either a dedicated Abacus-owned tmux pane or directly supervised attached process.
- Multi-agent execution is impossible unless all workspaces resolve to the same shared Dolt database.
- Claims are atomic and attributed with `BEADS_ACTOR`.
- Git branch preparation and clean-workspace enforcement happen before the agent CLI starts.
- Every agent is launched only through its CLI; OpenCode Server attachment remains the only direct non-tmux host.
- Ticket transitions control session lifetime exactly as specified.
- Unexpected exits reopen rather than complete work.
- Remote pull/push behavior matches the single-agent/shared-database rules in SPEC.md.
- Shutdown and failure paths do not strand `in_progress` tickets or Abacus-created panes/processes.

## Explicit non-goals for the first version

- Creating, deleting, or repairing Git worktrees/clones.
- Setting up or migrating Beads/Dolt databases and remotes.
- Starting or managing the requested tmux session, tmux window, or OpenCode server.
- Direct Codex app-server, Claude Remote Control, or other Git, tmux, Dolt, Beads, OpenCode, Codex, or Claude API/protocol integrations beyond invoking their supported CLI commands.
- A persistent queue, dashboard, web service, configuration file, dynamic agent pool, or automatic scaling.
- Interpreting ticket content, deciding whether work is correct, or performing the merge for the agent.
- Supporting Windows.
