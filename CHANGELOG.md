# Changelog

Noteworthy changes to Abacus are recorded here. Add entries under Unreleased;
the validated release workflow moves them into a dated version section and creates a fresh
Unreleased section. Released versions are listed newest first.

## [Unreleased]

### Added

- Add `abacus info` for a concise read-only overview of Git state and worktrees,
  Beads/Dolt identity and commits, ticket counts, and project routing policy.

### Changed

- **Breaking:** Configure reasoning effort by appending `#effort` to `--model`
  and `--reasoning-model` values. The separate `--effort`, `reasoningEfforts`,
  and saved `effort` settings are removed; combine them into model strings such
  as `gpt-6-astra#high`. Unsuffixed fallback models default to `high`, while
  unsuffixed reasoning routes inherit the fallback effort.
- Generated harness configs now show reasoning routing explicitly by mapping all
  three tiers to the harness's default model and explicitly setting every model
  specification to `#high`; users can edit the examples directly.

### Fixed

- Avoid tmux window-index collisions when creating the agent window in an
  existing session whose name also matches a window.

## [0.2.0] - 2026-09-11

### Added

- Add an opt-in bundled welcome-and-jingle mix to the interactive startup
  animation, enabled by `--tui-audio` or `tuiAudio`. Generated `abacus new`
  configs enable it; other runs and all non-interactive flows remain silent.
  Skipped or cancelled intros stop playback immediately, while naturally
  completed animations let the quieter jingle finish in the background.
- Add consistent semantic colors and clearer report structure to help, setup and
  maintenance commands, model and target reports, the run-config picker/editor,
  verbose events, errors, confirmation prompts, and run summaries. Redirected and
  `NO_COLOR` output remains plain, while script-oriented ID/version output is unchanged.
- Add a terminal run-config editor with Save As and saveable incomplete drafts,
  plus JSON run/preflight configs with `baseConfig` inheritance and explicit CLI
  overrides. Interactive runs missing required arguments offer a one-time config
  picker; non-interactive runs fail directly without discovery or prompts.

## [0.1.1] - 2026-09-11

### Changed

- New projects now create a shared base JSON config and three inheriting harness
  configs, without shell launchers. Run `abacus run` from the project root and
  select a harness; automation uses an explicit `--config` and CLI overrides.
  Generated runs start paused with all notifications and notification sounds enabled.
  Move repeated config arguments into `baseConfig` references and add new
  worktrees to the saved agent list. Previously generated scripts are unchanged.
- Move release finalization into a manually triggered GitHub Actions workflow.
  The local helper now only requests a version; all four platform builds and
  tests must pass before the changelog is committed and the version tag is
  created. Reject branch changes during testing and support same-run retries
  of interrupted publication without overwriting published releases.

### Fixed

- Include the Beads-generated root `.gitignore` in the `abacus new` initial
  commit so new repositories and agent worktrees start clean.
- Make dashboard tests independent of host terminal dimensions, handle unavailable
  console sizes with an 80-column by 24-row fallback, and verify comment scrolling
  across small and large viewports to prevent headless Linux CI failures.
- Update release workflow actions to Node.js 24 runtimes to remove Node.js 20
  deprecation warnings and avoid relying on forced runtime migration.

## [0.1.0] - 2026-09-11

### Added

- Parallel Beads task orchestration with actor-scoped atomic claims, isolated Git
  workspaces, dedicated `abacus/<issue-id>` branches, and ticket lifecycle
  supervision. Shared Dolt validation prevents unsafe multi-agent configurations.
- Interactive OpenCode, Codex, and Claude Code agents, plus OpenCode Server
  attachment with either tmux panes or directly supervised processes.
- Explicit model selection and provider-specific effort controls, Claude Code
  Remote Control, and `abacus models` for discovering installed harness catalogs.
- Ticket-based model routing through high, medium, and low reasoning labels,
  with configurable model mappings, optional enforcement, and attention reports
  for invalid label combinations.
- Continuous runs, finite `--once` and `--drain` modes, and a standalone
  `preflight` command that checks readiness without claiming work.
- Dispatch filters for labels, excluded labels, issue types, priorities, and
  target branches, plus configurable ticket timeouts with safe recovery.
- `abacus new` to create a shared Beads project, detached agent worktrees,
  bundled skills, and launchers for OpenCode, Codex, and Claude Code.
- Non-destructive `abacus init` for existing repositories and explicit `--repo`
  selection of the main checkout, independent of agent workspace locations.
- Bundled Beads planning, issue-quality, user-attention, and Git-instruction
  audit skills, with `skills install` and confirmation before replacement.
- Color-coded `abacus health` diagnostics for tools, Beads/Dolt storage,
  workspaces, configuration, bundled skills, and merge-slot availability.
- Configurable ticket target branches with an allowlist, default routing,
  optional enforcement, durable execution bindings, and `targets check` /
  `targets set` commands for auditing, repair, and explicit branch adoption.
- A live terminal dashboard showing agent states, ticket details, elapsed time,
  checkout branches, dirty workspaces, selected models, effort, and run outcomes.
- A recent-comment feed with attention and author highlighting, wrapped message
  previews, and a full-text detail view with keyboard scrolling.
- Global pause/resume of new claims, `--start-paused`, and per-agent stop,
  restart, and explicitly confirmed workspace-cleanup controls.
- Persistent user-attention alerts and `attention list` / `attention resolve`
  commands, including optional response comments and reopening/unassigning work.
- Best-effort native macOS/Linux desktop notifications for attention, ticket
  outcomes, and run summaries, with distinct success/failure sounds and an
  optional terminal-bell fallback.
- Structured JSONL events, append-only event logs, and correlated stdio controls
  for observing and operating Abacus without an interactive terminal.
- Configurable tmux session/window targets and pane layouts, stable agent/ticket
  pane titles, automatic session creation, and reuse of managed dead panes.
- Agent prompt extensions through `--append-prompt` and workspace files, plus
  repository-wide and target-specific merge-instruction overrides. Default
  merge guidance supports optional Beads merge-slot coordination.
- `branches prune` for closed-ticket local branches and initial Dolt commit
  reporting in run summaries to support operator-led recovery.
- A skippable terminal intro, verbose subprocess diagnostics, and compact
  redirected output for non-interactive runs.
- Versioned GitHub releases for Linux and macOS on x64 and ARM64, including
  self-contained binaries, SHA-256 checksums, and native version smoke tests.
- `abacus version` reports the embedded release version without requiring a
  repository or external tools; source builds report `0.0.0-dev`.
- A one-command release helper that commits the changelog rollover and atomically
  pushes the release branch and version tag.

### Changed

- **Breaking:** replaced legacy operation flags and implicit-run syntax with
  explicit commands and grouped subcommands. Added command-scoped help, strict
  option validation, `--option=value`, and `--` positional-argument handling;
  OpenCode Server mode must now be selected explicitly.
- Removed automatic resetting of dirty agent workspaces. Interrupted work is
  preserved and resumed only when its exact ticket and ownership are safe;
  destructive cleanup requires explicit operator confirmation.
- Use OpenCode's full interactive TUI instead of its earlier Mini interface.
  Keep the model ID unchanged and use OpenCode's configured/session effort when
  the TUI does not expose variant selection.
- Prefer the most recently commented ticket among equally high-priority ready
  candidates, while preserving Beads priority ordering. Skip merge-slot records
  and tickets with unfinished direct children.
- Default tmux windows to tiled layout and preserve user-owned sessions; allow
  automatically created sessions to survive shutdown with
  `--disown-tmux-session`.
- Make the dashboard more compact, add `j`/`k` navigation, and keep comment text
  uncolored while reserving attention/author colors for headers.
- Clarify agent authority to stage, commit, and merge locally without granting
  Git push authority; require completion summaries before final ticket updates.

### Fixed

- Reserve interrupted tickets before clean agents enter the ready queue, so
  another agent cannot take work belonging to a recoverable dirty workspace.
- Skip issue branches checked out in another worktree without repeatedly
  claiming and reopening their tickets; keep reopened work eligible in its
  owning workspace.
- Retry atomic claim contention and Dolt serialization conflicts without
  allowing duplicate ownership or hiding ordinary command failures.
- Preserve terminal ticket outcomes during exit, timeout, and shutdown races;
  reopen and unassign recoverable interrupted work instead of marking it done.
- Bound subprocess and host cleanup, tolerate tmux pane races, and stop unsafe
  recovery with persistent attention when ticket updates or Dolt synchronization
  cannot be verified.
- Reject Beads `no-git-ops` during health checks and preflight before any claims.
  Validate target bindings, branch history, and workspace ownership before
  changing Git state or launching agents.
- Preserve attention labels when response comments fail, record supplied
  messages verbatim, and avoid treating attention resolution as permission to
  close a ticket.

### Documentation

- Add focused setup, CLI, operations, architecture, target-routing, release,
  and shared-Dolt guides, including guarded backup and rollback procedures.
- Add visual quick-start and terminal demos, a dashboard screenshot, parallel
  worktree demo launchers, and an OpenCode Web demo runner.
- Add a parent-first shared-file tree demo with progressive task unlocking,
  agent attribution, and a human-attention checkpoint.
- Document external CLI contracts, supported tool versions, smoke-test
  evidence, and agent guidance for maintaining noteworthy Unreleased entries.
