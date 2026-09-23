# Changelog

Noteworthy changes to Abacus are recorded here. Add entries under Unreleased;
the validated release workflow moves them into a dated version section and creates a fresh
Unreleased section. Released versions are listed newest first.

## [Unreleased]

- Make timeline event nodes easier to select in both 3D and 2D with a consistent,
  screen-space click target, modestly larger visible dots that taper in dense
  clusters, and a clear ring around the pinned event;
  replace persistent timeline guidance and
  history coverage prose with a temporary top-of-dashboard loading progress bar.
  Grouped event popups now show recorded status transitions and use the resulting
  status color for their border, heading, badge, and pointer.

- Clarify `abacus help run` with a copyable example for dashboard bind and port,
  the local-only defaults, and the distinction from standalone `dashboard`
  server flags.

- Add a selectable Dependency Tree dashboard tab. It lays out current Beads issues
  from blocking prerequisites to dependents, groups connected work, separates issue cards,
  and uses distinct at-a-glance colors and labels for open, in-progress, blocked,
  and closed states. Drag-to-pan, Ctrl/⌘ wheel zoom, focused arrow-key pan,
  `+`/`-` zoom, fit-view, focus-selected, and search/status dimming keep large
  graphs navigable without hiding their dependencies. Beads' all-link export
  no longer gets rejected when its blocking-only dependency count omits
  parent-child links.

- Read recorded issue history for the whole project in one query instead of one
  bounded batch of 64 issues at a time. Beads keeps a row per issue per database
  commit, so a long-lived issue answered with a full copy of itself per commit:
  tens of megabytes and three processes per issue, and the timeline could only
  show the batch it had loaded. Repeated identical states now collapse to the
  earliest commit that records them, on both the whole-project and per-issue
  reads, so history that previously exceeded its output limit is readable again.
  Sources that cannot answer a whole-project query keep the batched reader.

- Keep recorded title, assignee, priority and type visible during timeline playback.
  A closure is recorded from second-precision `closed_at` and usually lands a few
  milliseconds after the snapshot that closed the issue, which previously blanked
  every field to "Title unavailable" for closed lanes. Playback fields now come from
  the newest recorded snapshot at or before the playhead, with the snapshot time and
  any lag behind the status shown in the inspector.

- Draw 120 timeline lanes per page instead of 24, and sample branch paths by their
  on-screen length instead of a fixed 100 points. Ordering lanes by start time made a
  small page cluster every drawn lane into the earliest part of the range, leaving
  most of the width empty; the scene's vertical spread follows peak simultaneous work
  rather than the lane count, so a larger page costs geometry, not height.

- Order timeline lanes by when their work starts instead of by issue ID. Only 24
  lanes are drawn at a time, so a lane revealed later by the playhead could sort
  ahead of lanes already on the page and push one off it: recorded work appeared to
  vanish from the past simply because the playhead moved forward. Later work is now
  appended, and deep-linking to an event pages by the displayed order rather than an
  unfiltered issue-ID index.

- Fix work episodes sliding sideways on the timeline. A branch kept its side when a
  neighbour ended but was repacked inwards, so surviving branches drifted across the
  scene mid-flight — movement the recorded source never contained. A branch now keeps
  the side and the distance it was first given until it ends, and a new branch reuses
  the innermost free slot so sequential work still stays near the spine.

- Stretch the timeline's time axis and lane spacing up to 40×, from 10×. The camera's
  zoom-out limit now scales with the stretch, so **Fit view** at a high stretch is
  reachable by zoom and survives a reload instead of being discarded as out of range;
  reducing the stretch pulls a far camera back in rather than stranding it.

- Keep the selected node and the focused lane caption while the timeline plays.
  Every playback step was treated as a change of playback context, so a pinned event
  was dropped from the inspector, the callout and the URL several times a second, and
  a caption holding keyboard focus lost it as soon as the needle moved past that
  lane's work. Returning to live, seeking and restoring a location still clear the
  pinned event, because those really do change which events are reachable.

- Caption timeline lanes by the playhead needle rather than by lane count: the
  selected issue alone when one is selected, otherwise every work episode open at
  the needle, falling back to the most recently ended work. Uncaptioned lanes keep
  their accessible list entries.

- Keep timestamped comments visible during timeline playback, with a comment shortcut
  and clear coverage. Explain read-only playback and offer a visible return-to-live
  editing action, including refresh retry, without losing comment or label drafts.

- Replace the Activity tab's repeated snapshot cards with recorded status, label
  and note changes, keeping comments separately available. Move raw snapshots and
  source coverage into collapsed technical evidence, and merge history pages
  without discarding richer timeline history or duplicating changes.

- Label status changes inside grouped timeline nodes, prioritizing closures over
  comments and issue cards. Try nearby label positions before hiding overlaps,
  so a grouped closing event no longer silently loses its status annotation.

- Keep issue events, including closure nodes and comments grouped with entry,
  on their offset branch. Only the first recorded open/blocked → in-progress
  transition may sit on the spine; branch joins are unmarked. Preserve each work
  episode's side when other issues end, preventing return curves from dipping
  through the spine and doubling back. Both axis stretch controls now reach 10×.

- Space timeline work episodes by concurrent activity instead of arbitrary issue
  slots: sequential work reuses one offset, overlapping work alternates above
  and below the spine, and continuing lanes adjust smoothly as overlaps end.
  Keep line thickness and circular event-node proportions independent of axis
  stretching, enlarge nodes, and fit the actual layout. Old saved cameras refit
  once when switching to the new layout.

- Show meaningful issue changes on the timeline instead of raw database snapshots:
  status transitions, added/removed labels, new comments, and note changes with
  expandable before/after text. Unchanged snapshots and unrelated field edits
  no longer create nodes or inflate clusters. Keep snapshot history for status
  curves, hide technical evidence behind a disclosure, and avoid inventing changes
  from the first available or ambiguous snapshot. Git history remains in its inspector.
  Old links to raw snapshot/Git timeline markers no longer resolve; inspect those
  records in Activity/Git instead. Links to comments remain valid.
  Grouped-node popups preview up to five comments with commenter names and
  truncated text; full comments remain available in the inspector.

- Draw timeline curves from recorded issue-status work episodes: in-progress
  starts leave the activity spine, blocked spans are marked, and closed spans
  return at closure without extending to now. Git proof is not required to draw
  status curves; inactive gaps remain empty on the scene and minimap. Reopened
  work creates a separate segment, and playback updates at recorded status
  boundaries without revealing future closures.
  Timeline automatically reads bounded status histories with two concurrent
  requests and reports coverage/failures with an explicit retry control. Larger
  projects can navigate 64-issue history batches instead of leaving later issues
  permanently without history. Selecting a closed issue checks optional Git
  integration and shows confirmation separately from its status-based return.
  Closed issues without a recorded end time stop at their last known working
  state and explicitly report the missing timestamp instead of appearing active.
  Prioritize likely in-range issue histories before allocating 64-issue batches,
  rather than loading solely by ID. Changing the range returns to the first
  prioritized batch; uncertain and older histories remain available.


- Add a live “last N hours” timeline control with fractional-hour offsets, rolling
  bounds and shareable URLs; custom historical ranges remain fixed.
  Drag across the minimap to select a fixed time range with a highlighted preview,
  in either direction using mouse or touch. Escape cancels; clicks still seek.


### Dashboard navigation

- Show titles without issue IDs on floating timeline issue labels. Add **Maximize
  view** to fill the timeline panel with the scene and view controls, hiding time
  and issue settings while preserving the inspector and selected range. Restore
  with the same button or Escape; this is separate from browser fullscreen.
- Add independent Time width and Vertical spacing sliders for 2D/3D timelines,
  with remembered time stretching up to 10× and vertical stretching up to 10×,
  plus a reset control, without changing the time range.
- Move live workers and registered worktree controls into dedicated Workers and
  Worktrees tabs, keeping issue and branch views focused.
- Allow the timeline workspace to scroll; ordinary wheel gestures scroll the
  page, while Ctrl/⌘+wheel zooms the camera. Vertical touch swipes scroll without
  moving the camera.
- Add **First event → now** directly to the timeline toolbar: set the exact
  earliest available event as the start, return to live now, and fit the camera.
  Filters do not restrict the range; empty timelines do not invent a start date.


### Fixed

- Connect selected recorded timeline events to the time grid with subtle dashed
  guides in both 3D and fallback views, matching the reference without adding
  synthetic events or continuous animation. Fallback playback now clips branch
  lines at the exact playhead instead of stepping between geometry samples.

- Expose current-state and verified-containment timeline markers in the keyboard
  event list, clearly separated from recorded events and omitted during playback.
  Inspector details identify Beads working-set versus Git-ancestry provenance and
  source revision; containment callouts no longer look like unattributed comments.
  Current-marker timestamps are labeled as display time, not source observation time.

- Add a reference-style pause badge at blocked live-lane endpoints, with keyboard
  access to current state and no misleading historical waiting indicator.

- Fade timeline regrouping after range changes and preserve pinned recorded events
  that remain in range, without replaying their speech-bubble entrance.

- Give newly received events inside timeline clusters a restrained fade, keeping
  existing clusters visible and all individual events accessible. Cluster inspector
  details preserve each member’s author, exact timestamp and source provenance,
  with local 50-member pages so large clusters do not create unbounded inspector DOM.
  Source refreshes retain the selected page, expanded details and pager focus,
  clamping safely when members are removed.

- Fade a newly verified return connector into the main timeline without replaying
  the whole branch or inventing a merge timestamp; initial evidence loads stay static
  and transitions require the same recorded branch, target and start commit.

- Fade newly observed live issue lanes, beads and cards together without replaying
  snapshot baselines; reduced motion suppresses the effect and offscreen lanes
  do not accumulate an animation backlog. Finished effects release their draw
  ranges even with glow disabled or after switching to the 2D fallback.

- Fade timeline filter changes briefly while keeping recorded timestamps
  fixed; rapid changes replace the effect and reduced motion cancels it.

- Anchor timeline speech bubbles to their projected events as the camera moves;
  keep bubbles inside the scene and detach their pointers for offscreen events.

- Smooth timeline tube joins and rounded bead lighting without increasing scene
  geometry, keeping curved branches continuous around bends.

- Select timeline events directly from the minimap, sharing the issue, inspector
  and exact event time; respect event-kind filters and retain blank-space scrubbing.

- Add a persistent Glow toggle that disables scene halos and interface shadows
  while preserving status colors, borders, focus outlines and 2D fallback.

- Blend live current-status marker colors once while updating status text
  immediately; reuse geometry and suppress replay for identical updates,
  reduced motion, stale sources and reconnect baselines.

- Fade newly observed live event beads into their recorded positions once,
  without replaying loaded history or rebuilding geometry on animation frames.
  Reduced motion and hidden/playback views suppress these finite effects; finished
  or cancelled fades return immediately to batched rendering. Automatic stream
  reconnects refresh the snapshot baseline rather than replaying missed arrivals.
  Large live bursts animate visible lanes only, without an offscreen backlog;
  stale reads and the first recovered state never replay arrival effects.

- Add subtle inspector panel fades and a gliding tab indicator, with immediate
  selection, interruption-safe switching and reduced-motion support.

- Finish interrupted camera transitions immediately when reduced motion is
  enabled or the timeline is hidden, avoiding half-switched projections and
  delayed animation replay when returning. Hidden playback pauses at a saved
  playhead and returns with accurate Play/Pause controls. Playback updates filtered
  lanes as recorded states change and settles the inspector, scrubber and shared
  URL at the exact end of the range.

- Keep pinned timeline events synchronized with loaded source evidence, clearing
  invalidated selections and preserving event-detail focus across refreshes
  without replaying entrance animations. Show exact UTC timestamps in event
  evidence and tooltips alongside readable local display times.

- Make the desktop inspector resizable by dragging or keyboard, remember its
  width locally, and preserve the stacked layout on narrow screens. Author-first
  speech bubbles use local initials; expandable selected-event evidence keeps the
  overview readable without hiding access to full provenance.

- Restore recorded timeline event selections from shared links, with an explicit
  unloaded-history notice rather than inferred events; clear stale callouts when
  scrubbing or returning to live. Browser back/forward restores the time range,
  and live links stay live after reload rather than reopening as fixed playback.
  Remember the camera pose and pan/orbit preference locally, with shareable
  `camera=2d` or `camera=3d` links overriding the local projection preference.

- Preserve loaded Git inspector evidence while switching views or selecting events
  on the same issue; still invalidate it on source refresh or playback changes.

- Cull overlapping timeline labels at dense zoom levels, prioritizing selected or
  focused cards while preserving every visible lane in the accessible event list.
  Preserve logical keyboard focus when timeline cards or event controls rebuild.

- Reject ambiguous assignment journals and directory-shaped journal paths rather
  than accepting conflicting ownership fields or treating them as missing records.

- Reuse unchanged worktree patches after bounded content fingerprint checks,
  while detecting repeated dirty-file edits and Git attribute/config changes;
  report externally filtered content as unavailable rather than guessing its inputs.

- Close worktree subscriptions and reject new observers when dashboard shutdown
  starts; prevent late reads from republishing into retired observations.

- Submit only changed issue content fields from the web editor, preserving external
  changes during label-only edits and unsent edit drafts when comments complete.

- Reject worker/supervisor control requests once run shutdown begins, and reject
  requests to completed supervisors instead of accepting work that cannot run.

- Close rejected oversized HTTP/1 request connections explicitly so subsequent
  dashboard requests do not reuse a transport being discarded.
- Preserve leading-option text literally in standalone attention response comments,
  without treating it as Beads command-line flags.

### Added

- Refine the dashboard’s reference-led layout with a scene-first layout, keyboard-accessible filter/source
  disclosure, luminous navigation, flat inspector tabs and speech-bubble
  callouts, bounded selected-branch event captions, left-aligned lane cards that leave branch curves visible, compact inspector facts and label chips, bright-core timeline tubes and restrained depth-tested halos.
  Straight-segment simplification keeps glow geometry bounded; hover/focus
  transitions respect reduced-motion preferences. Deliberately selected callouts
  enter with a short, interruptible fade/slide without replaying identical selections.

- Navigate from recorded inspector snapshots and dated comments to their timeline
  event, with playback, lane focus and an expanded range when needed.

- Show explicitly loaded, validated Git branch and file-change summaries on
  timeline lane cards, with comparison provenance and current containment status;
  discard stale evidence and keep current Git facts out of historical playback.
  Explicitly loaded Git commits appear as dated, selectable timeline events with
  author/parent provenance, without implying issue authorship or merge time.
  Verified current containment curves the issue path back onto a central silver target rail;
  it is excluded from playback and never placed at the issue closure timestamp.
  Recorded start curves require the exact dated start commit in loaded history;
  commit timestamps are not presented as claim or branch-creation times.

- Publication review reports bounded transitive dependency coverage, distinguishing
  missing issues and unknown collections without treating coverage as approval.

- Document the proposed interactive HTTP dashboard in `ABACUS_WEB_SERVER_SPEC.md`,
  with a bundled visual reference, 3D issue/Git timeline, smooth accessible motion,
  configurable binding, and change-driven refresh. Specify in-process `run --dashboard` with live agent
  controls and standalone `dashboard` without agents. A standalone
  preview now provides issue/branch browsing, search, inspectors with recorded
  incoming/outgoing relationships and explicit coverage gaps, keyboard-accessible
  Overview/Activity/Git tabs, related ongoing tickets, and target-aware
  triple-dot comparisons and patches, explicitly loaded issue-binding evidence
  checked against target policy and recorded start ancestry with lazy bounded
  patches and reachable commit history, collected worktree clean/dirty/unknown
  states and bounded staged/unstaged/untracked content with shared live
  reconciliation for visible worktrees and read-only collection-cost diagnostics, bounded committed issue history with explicit
  coverage gaps, a WebGL timeline with 2D fallback, local playback and independent
  issue lanes, and shared live updates that reuse unchanged issue serialization
  and build aggregate snapshots only on demand, with configurable
  IPv4/IPv6 binding. Read-only Git commit history preserves topology and labels clock
  skew. History, comparisons and patches refresh after shallow-clone deepening
  without a branch-tip change. Comments, content edits and plain attention requests/resolution include revision checks, verified
  outcomes, explicit recovery of partial attention changes, and bounded same-request retries. Access is unauthenticated: use a
  trusted network. Other editing actions, complete Git/timeline integration and integrated run
  controls remain under construction. `run --dashboard` now hosts the same preview
  in-process, with inherited saved settings, explicit enablement, bind-before-worker
  startup and isolated web failures. Live worker/supervisor rows come directly from
  this run through bounded, change-driven updates, with separate manual-pause and
  schedule-gate visibility and session-scoped, retry-safe claim Pause/Resume.
  Headless `--start-paused` runs can resume through HTTP. Worker Stop/Restart and
  confirmed Clean Workspace use tracked acceptance/completion and bounded retries;
  confirmed Stop Run follows normal worker recovery and cleanup. Supervisor
  Stop/Restart report explicit acknowledgement; confirmed supervisor Force Run uses
  bounded same-request retries and tracks its own cleanup/verification. Runtime
  controls remain interruptible and no longer overlap timeline playback. Graceful
  dashboard shutdown drains accepted issue writes before closing the listener,
  rejecting new writes and preserving uncertain outcomes if the grace period expires. Ordinary
  label edits use verified add/remove deltas while protecting reserved control labels.
  A browser draft composer and creation API validate target/reasoning policy, stage
  new issues non-ready from creation, preserve input across navigation, and share
  mutation retry/shutdown safeguards. A read-only publication review API separates
  content/target/reasoning checks, lifecycle concerns, cycle evidence, dependency
  coverage and unverified ownership/integration, with a review revision covering
  the full observed source and policy rather than only the selected issue;
  explicit publication remains under construction.
  The issue table adds accessible column sorting, bounded pagination and URL-retained
  table settings without additional source reads. Shared view-only assignee, label,
  priority and attention filters persist in URLs and preserve unknown historical
  metadata rather than substituting current values. Available recorded metadata
  now drives historical filtering; conflicting same-time fields stay unknown.
  Type and explicitly declared target filters are also available. Issue/table search
  shares loaded note/comment text with explicit coverage, excluding future and
  undated current content during historical playback. Framework-dependent builds now require the
  .NET 10 ASP.NET Core runtime; self-contained packages include it.

- Force-run either supervisor from its dashboard menu with a typed one-run
  instruction, or via the `force-supervisor` stdio command. The instruction is
  appended to the normal prompt and policy; explicit continuation runs can
  proceed despite unfinished epics or claim gates.

- Add an independently optional empty-backlog continuation supervisor with its
  own model, arguments, timeout, and custom planning prompts. It can write specs
  and create epics under user policy when no unfinished epics remain. Process-local
  one-shot triggers prevent no-op/failure loops until unfinished epics appear and
  finish. Each new Abacus process allows a fresh attempt; the continuation row's
  Restart explicitly retries in-process. Legacy continuation.json flags are ignored
  and the offline `continuation retry` command is removed. Both supervisor
  roles share the main checkout safely and receive effective per-target merge
  instructions, including custom and empty overrides, without expanding
  maintenance authority.

### Changed

- Show when one supervisor is waiting for the other to release the main
  checkout, rather than leaving it in Starting. Delay maintenance's startup
  sound until its harness has actually launched.

- Increase the default maintenance and continuation supervisor timeouts to
  90 minutes (1.5 hours) per run; explicit timeout overrides remain unchanged.

- Add a wrapped, scrollable last-run report for both supervisors: select a row
  and press L, or choose Last run details from its action menu. The report
  preserves the latest summary and model while a new run starts.

- Rename the maintenance agent from `supervisor` to `maintenance` in the dashboard,
  events, stdio controls, and Beads actor identity. Update control clients to target
  `maintenance`; existing supervisor configuration, policy paths, and labels remain
  compatible. Prefer `maintainerModel` / `--maintainer` for its model; the old
  model-setting names remain aliases. `maintenance` is now reserved as a worker name.

- Give maintenance and continuation supervisors distinct startup announcements,
  replacing the shared supervisor clip while preserving TUI-audio controls and
  attention-sound preemption.

- Runs now allocate and reuse an external, repository-specific worktree pool;
  choose capacity with `--agents` / `agentCount` (default one). New project
  configs no longer require workspace paths. Existing explicit workspaces remain
  supported and are never automatically adopted or deleted. Manage pooled slots
  with `worktrees list`, `reclaim`, `remove --confirm`, and `prune`. Workers now
  lease independent slots per assignment; optional `--agent-name` / `agentNames`
  labels can change freely between runs. Recovery scans the whole pool even after
  reducing capacity and updates readable Beads assignees on safe takeover. Durable
  assignment IDs prevent duplicate issue reservations and stale updates. Uncertain
  execution after a crash remains quarantined until surviving processes are
  stopped and `worktrees recover <slot-ID> --confirm` is used. Existing `agent-N`
  slot paths remain valid; new slots use `slot-N`. Stop old controllers and
  surviving harnesses before upgrading; pre-upgrade executions have no journal.
- Maintenance supervision now diagnoses current pooled assignments instead of
  stale preflight paths. Both supervisor prompts and bundled skills explain pool
  ownership, historical assignees, protected runtime locks/journals, and operator-only
  crash confirmation. Planning honors explicit bounded continuation authorization
  without redundant interactive approval. Refresh existing bundled skill copies
  with `abacus skills install` after upgrading; replacement remains confirmed.
- Fix autonomous draft planning with Beads 1.2.2: use supported deferred creation
  before blocking drafts instead of requiring unsupported `create --status` or
  pausing dispatch. Incomplete plans stay unclaimable until explicitly published.
- Workspace cleanup now resets tracked changes only, preserving untracked files
  and ignored compilation caches. Resets that would overwrite untracked or
  ignored paths are refused for operator review.
- Add a linked README feature overview and move supervisor guidance alongside
  ticket recovery so capabilities and maintenance help are easier to find.

## [0.4.0] - 2026-09-13

### Added

- Add an optional maintenance supervisor enabled by `--supervisor-model` or
  `supervisorModel`. It runs through the selected harness in the main checkout
  to address user-attention issues and workspace/claim failures, then retries
  failed agents without repeated automatic failure loops. Its separate TUI row
  shows progress and the last outcome; startup/failure clips follow interactive
  TUI audio settings. Configure separate harness arguments, a timeout defaulting
  to 30 minutes, and a custom prompt file through CLI or JSON. Repository
  `.abacus/supervisor.md` instructions precede the custom file. By default it
  handles only workspace/Beads maintenance, including reopening blocked tickets
  whose primary attention blocker it resolved. Local Git maintenance, such as
  verified stale-lock removal, is explicitly authorized over Beads' blanket Git
  restrictions. Project/implementation decisions require explicit user-authored
  policy. Git pushes and target-branch merges or rewrites are prohibited by
  default, but additive prompts can explicitly authorize specific actions.
- Add `abacus attention retry-supervisor <id> [<id> ...]` to remove
  `abacus:supervisor-cannot-resolve` from selected issues without changing their
  attention labels, status, or assignee, allowing another supervisor attempt.
- Play a newly bundled attention clip whenever an issue starts needing user
  attention while TUI audio is enabled, including issues already labelled when
  the run starts. It plays once per newly observed issue, never restarts while
  the clip is still playing, and follows `tuiAudio`/`--tui-audio` in every run
  mode, so `--no-intro` still disables only the interactive entrance. Supervisor
  startup/failure clips take priority: they stop attention playback and suppress
  new attention sounds until the supervisor clip finishes.
- Add optional scheduled claim windows to run configurations. A `schedule` object
  names a timezone, recurring `"<days> <from>-<to>"` windows that block new
  tickets, and an optional `minWindowRemaining` duration a claim must fit inside
  the open hours, so a fleet can avoid a provider's published peak-price hours.
  Continuous runs wait for the next window and show the reason and next claimable
  time on the dashboard; `--once` and `--drain` exit `3` without claiming, so exit
  `0` keeps meaning the ready queue was drained. The schedule gates new claims
  only: running tickets finish, and pausing or resuming claims by hand never
  bypasses it. `schedule` is config-only, replaces wholesale when inherited, and
  is editable in `abacus config edit`.

### Changed

- Support `j`/`k` as down/up navigation in the `abacus config edit` menu.
- Polish the main dashboard with highlighted tabs and selections, quieter metadata,
  roomier agent rows on taller terminals, a compact run overview, and fixed-position
  status and keyboard hints. Compact and no-color layouts remain supported.
- Split the live dashboard into Agents, Attention Center, Latest Comments, and
  read-only Settings. Switch with 1–4, Tab, or Left/Right; unread `!n` tab badges
  flag new or changed content without relying on color. Each screen scrolls
  independently within the terminal height, keeping navigation and claim controls
  visible. Shift-Tab still toggles claims; arrow/j/k selection stays on its screen.
  The run's default model/effort now appears in Settings instead of the header.
- **Breaking:** Treat the current run's configured agents as the authoritative
  merge-slot participants. Automatically release unknown holders and remove
  unknown waiters, alongside configured agents without a running harness;
  preserve configured live agents and remaining waiter order. Claims from
  external agents or other runs are no longer protected: use a single
  controller per shared merge slot.
- Drop the blank ticket line from dashboard rows for agents that hold no ticket,
  so the name, state, and progress share one row and idle agents stop consuming
  vertical space.

### Fixed

- Keep the dashboard alert block compact and current. Alert text now wraps
  instead of being clipped, each agent or Abacus source keeps a single notice
  row, a notice that repeats or restates that source's persistent alert no
  longer adds a duplicate row, clearing an alert also clears its source's
  notices, and an unrepeated notice expires after about a minute. Attention Center
  scrolls wrapped alerts within its own viewport instead of competing for space
  with agents and comments.

## [0.3.0] - 2026-09-12

### Added

- Show Beads merge-slot ownership on the live dashboard's agent rows: the holder
  is marked and every waiting agent shows its position in the queue.
- Add `abacus info` for a concise read-only overview of Git state and worktrees,
  Beads/Dolt identity and commits, ticket counts, and project routing policy.
- Add `--extra-args` and repeatable `--reasoning-args <tier>` (or the saved
  `extraArgs` and `reasoningArgs` fields) to pass additional harness CLI
  arguments such as a provider selector. Per-tier arguments replace the default
  for tickets carrying that reasoning label, so one run can mix providers per
  reasoning level. Values are split with shell-style quoting and passed without
  shell interpretation.

### Changed

- Report each agent's branch, model, and reasoning effort on one dashboard
  metadata line, in that order. Every row shows the settings it is using or
  would use instead of hiding them between tickets; the compact header still
  reports the run's default model/effort.
- **Breaking:** Configure reasoning effort by appending `#effort` to `--model`
  and `--reasoning-model` values. The separate `--effort`, `reasoningEfforts`,
  and saved `effort` settings are removed; combine them into model strings such
  as `gpt-6-astra#high`. Unsuffixed fallback models default to `high`, while
  unsuffixed reasoning routes inherit the fallback effort.
- Generated harness configs now show reasoning routing explicitly by mapping all
  three tiers to the harness's default model and explicitly setting every model
  specification to `#high`; users can edit the examples directly.

### Fixed

- Reclaim merge slots and waiter entries that name one of the run's agents while
  that agent has no running harness. A crashed, stopped, or exited agent can no
  longer leave the merge slot claimed forever and block every other agent;
  ownership belonging to other agents is left untouched.
- Stop telling agents to wait for the Beads merge slot with a shell retry loop.
  The agent prompt now requires harness-native waiting and forbids shell retry
  loops and background or detached processes, which could otherwise keep
  running after a crashed agent and leave shared coordination such as the merge
  slot permanently blocked. The prompt also states that waiting is normal, is
  not by itself grounds for blocking a ticket, and that blocking is reserved
  for waits that look hopeless to resolve.
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
