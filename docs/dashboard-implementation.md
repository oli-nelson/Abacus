# Web dashboard implementation status

The delivery contract remains [ABACUS_WEB_SERVER_SPEC.md](../ABACUS_WEB_SERVER_SPEC.md).
The standalone `dashboard` entry point now serves a limited interactive browser preview;
`run --dashboard` hosts that preview in-process with runtime controls. Full acceptance remains open; see the [current 13-criterion gap audit](dashboard-acceptance.md). This is implementation tracking, not a reduced
scope or an operator guide. No active Beads workspace exists in this checkout.

## Implemented foundation

- Opt-in, per-stream subprocess output limits; cancellation/deadlines cover pipe
  reads as well as process exit. Existing callers retain unlimited output unless
  they explicitly request a cap.
- Internal read-only `bd --readonly export` collector, capped at 32 Mi characters
  per stream and 100,000 records. Default export excludes infrastructure/memories;
  merge-slot labels are additionally excluded. No HEAD-only change detector.
- Canonical per-issue hashes include comments, dependencies, notes and metadata;
  object keys and known unordered collections are normalized. Duplicate keys/IDs
  and unsafe IDs fail the complete snapshot rather than publishing partial data.
- Immutable incremental issue summaries, removals, source revision and export-byte /
  detection-duration measurements. Identical exports retain the same view instance;
  changed records preserve unrelated summary instances.
- Concurrent refresh callers share a probe; consumer cancellation does not cancel
  others. Invalid/failed source reads preserve last-good data and sanitized health.

Verified CLI help: installed Beads 1.2.2 says default export emits JSONL with
labels, dependencies and comments and excludes memories/infrastructure. Disposable embedded Beads fixtures now verify consecutive working-set edits,
comment-only and dependency-only changes with identical HEAD (and unchanged issue
updated_at for comments/dependencies). Shared-server verification remains.
The collector now runs once per standalone HTTP host. Integrated orchestration
monitor sharing remains unimplemented.

## Remaining delivery gates

The current requirement-by-requirement source of truth is
[dashboard-acceptance.md](dashboard-acceptance.md). The entries below are a
chronological implementation/evidence log, not a claim that every earlier
limitation still applies or that later targeted tests close an entire criterion.

Major open areas are complete typed issue management and ownership fencing,
reference-quality sourced integration topology, complete provenance/history,
real-harness acceptance, shared-source efficiency and scale/native release gates.
Live worktree patches now reuse content fingerprints; full combined idle and
scale measurements are still required before closing acceptance criterion 6.

## Verification so far

Focused dashboard/command-runner tests: 18 passed (2026-09-21), including the
existing Beads export fixture, repeated unchanged snapshots, consecutive note /
comment / dependency changes, deletions, malformed-source retention, concurrent
read coalescing, consumer cancellation and stdout/stderr limits. `git diff --check`
passes. The initial full regression run passed 885 tests; after HTTP integration,
901 passed. Partial HTTP/browser/live-Beads smoke checks are recorded below; no
full interactive or performance acceptance gate has passed.

### History and collection follow-up

- Added a 64-entry single-flight LRU detail cache; in-flight entries are not evicted,
  full in-flight capacity rejects additional distinct requests, failed loads are
  retryable, and caller cancellation does not cancel shared work.
- Added bounded `bd --readonly history <id> --limit N --json` reads (1–1,000 entries,
  4 Mi characters, two concurrent CLI calls). Cache keys include issue and Dolt
  history revisions plus limit; supplying refreshed revisions remains the host's
  responsibility. Commit snapshots preserve CLI order and stable IDs, label commit
  time/committer honestly, and never imply complete working-set history.
- Added host-owned poll loop with one pending hint, 250 ms burst debounce,
  dirty-again retention, bounded failure backoff and change/health-only publication.
  Identical successful probes do not publish collection timestamps as data events.
- Captured real CLI fixtures and their reproduction notes under
  `tests/Abacus.Tests/Fixtures/Beads/Dashboard-1.2.2/`.

Latest focused verification: 44 dashboard/command-runner tests passed after the
history/cache/scheduler additions. `git diff --check` remains clean.

### HTTP preview

- Added standalone `dashboard` options/help/dispatch, explicit IPv4/IPv6/DNS
  binding and no fallback port, Beads-to-Git identity checks, shared collector,
  Ctrl-C/SIGTERM cleanup, stderr-only diagnostics and unauthenticated-access warning.
- Kestrel runs in-process using Microsoft.AspNetCore.App; PLAN.md documents the
  shared-framework exception and release/runtime implications. No new NuGet package.
- Embedded dark navy/cyan issue browser with accessible table, search/status
  filtering, stable row reuse, selection/inspector and issue deep link. It explicitly
  labels absent editing, Git, history and 3D timeline capabilities; no fake data.
- Cached snapshot ETags and SSE cursor/replay (128 events / 8 MiB, 64 clients,
  32 queued events each), gap/restart resync, health-only events, heartbeats and
  best-effort final disconnect. Identical probes reuse serialized snapshot bytes.
- Host allowlist, mutation Origin/JSON/custom-header guards, 64 KiB body cap,
  CSP/nosniff/no-referrer headers, text-only UI content and embedded-only paths.
- Seven HTTP/stream/CLI tests pass, including real IPv4/IPv6 connections, occupied
  ports, headers, body size, ETags, immediate SSE connection/live data delivery,
  restart/gap/slow-client handling and command scoping.
- Real standalone smoke on disposable embedded Beads: external issue edit changed
  HTTP revision 1 to 2 and the displayed title. Ctrl-C returned 130. Headless Chrome
  screenshot reviewed at 1671x941; this only validates the issue-browser preview,
  not the required 3D visual/motion acceptance. Screenshot exposed delayed SSE
  startup feedback; fixed by flushing an immediate SSE comment and regression-tested.
- Full regression suite after HTTP integration: 901 passed. The final SSE flush and
  long polling-interval changes have separate focused coverage: 45 dashboard tests
  passed. JavaScript syntax and git diff whitespace checks passed. Temporary HTTP
  and headless-browser smoke processes were stopped and verified terminal.

Known gaps include full project health, validated issue/Git associations, issue-history
event projections, remaining typed writes, pagination/sort/filter breadth, runtime integration and
all scene/performance acceptance gates. See docs/dashboard.md for preview usage.

### Git evidence foundation

- Added read-only, bounded `DashboardGit` CLI helpers: canonical local ref/worktree
  probes, literal server-known ref resolution/revalidation, full-tip-keyed
  single-flight caches for triple-dot summaries, lazy patches and bounded topology
  history. Four concurrent Git commands maximum; all output is capped at 4 Mi
  characters. Cache caps: 128 comparisons, 16 patches, 64 history ranges.
- Numstat and worktree parsing use NUL boundaries; preserve tabs/newlines in names
  and binary counts as unknown. Renames are deliberately presented as delete/add
  under a fixed no-renames comparison policy. External diff/text conversion and
  replace objects are disabled. No LFS hydration, fetch/push, reset or merge occurs.
- Containment is ancestry of issue tip in target now, not exact merge time or proof
  of correct completion. Unrelated/multiple merge bases get explicit unavailable
  comparisons. Cache keys include both target and issue tips; ref movement triggers
  bounded revalidation rather than publishing a falsely coherent comparison.
- Real disposable Git tests cover divergent target-only changes excluded by
  triple-dot, cache coalescing/identity, target movement, merge containment and
  revocation, missing branches, packed refs, fast-forward-equivalent containment,
  binary stats, hostile filenames/worktree paths, unrelated history, topology log
  order/limits and malformed output. These helpers are not yet connected to HTTP.

Next Git steps: collector/health and target-policy association projection, execution
binding validation, demand-loaded dirty worktree content reconciliation, branches /
diff/history HTTP routes and inspector views. Source helper tests do not prove those
remaining end-to-end requirements.

Verification after Git helpers: all 49 dashboard tests passed; `git diff --check`
passed. The existing 901-test full-suite result predates these isolated helpers.

### Branches HTTP/browser integration

- A single Git collector per standalone host probes refs/worktrees and validates
  current target policy independently of the Beads collector. Identical content
  reuses the serialized Git publication; changed Git facts emit their own revisioned
  SSE event. Last-good facts survive source failures; invalid/missing targets disable
  comparisons without hiding branch/worktree facts or guessing main.
- Added `/api/v1/branches` (ETag) and typed, known-ref-only
  `/api/v1/branches/compare` with configured target validation, post-patch ref
  revalidation, cached response bodies, and sanitized failures.
- Branches UI now supports search, target selection, ahead/behind and full-tip
  comparison inspector, file statistics/binary indications, ancestry status and
  lazy bounded patch loading. Branch/target/view links round-trip through URL;
  convention-only issue associations link back to the issue. Worktree dirty state
  remains explicitly unknown, and execution binding associations remain pending.
- Real-Git+HTTP integration test verifies policy missing/valid/invalid transitions,
  warm publication identity, ETags, comparisons, rejected arbitrary refs/query
  fields, and Git updates without issue projection rebuilds. All 50 dashboard tests
  passed before the final UI accessibility/initial-error wording cleanup.
- Standalone disposable Beads/Git smoke verified actual HTTP ahead count, unverified
  ancestry and expected patch text. Headless Chrome DevTools smoke waited for the
  branch inspector, clicked Load patch, asserted text, and captured/reviewed the
  finished 1671x941 view. The browser and server were stopped (server Ctrl-C 130).
  This does not satisfy the remaining 3D/interaction/performance acceptance gates.

Final verification for Branches integration: 118 dashboard + command-syntax tests
passed, JavaScript syntax and `git diff --check` passed. The 901-test whole-suite
result predates this integration; no newer whole-suite result is claimed.

### Verified content writes and comment composer

- Enabled typed comments and title/description/priority/append-notes edits, with
  literal argv, configured Beads actor, strict bounded JSON, stale-source refusal,
  fresh canonical revision checks and post-write verification. Startup probes
  required CLI capabilities without mutating data.
- Server-owned serialized execution survives a disconnected caller; same-request
  retries share the result. Session/timestamp-qualified IDs and the bounded
  15-minute ledger reject expired/restarted requests rather than replaying appends.
  This is not durable idempotency or cross-process CAS.
- Explicit completed/rejected/partial/unknown outcomes, conflict current summaries,
  draft preservation during live updates, and explicit review before a new ID.
- Stored comments expose actual author/time and literal text. No dated history is
  inferred from current notes. Ownership/status/dependency/attention actions remain
  disabled pending their conservative safety checks.
- Real Beads 1.2.2 HTTP smoke verified exact hostile-looking literal comments,
  actor attribution, same-ID replay, content updates, notes append, stale conflicts
  and mismatched-payload rejection in the disposable fixture only.
- Headless Chrome submitted a comment, verified escaped literal HTML and cleared
  the draft only on verified completion. Screenshot reviewed at 1671x941.
  Temporary HTTP/Chrome processes stopped (server Ctrl-C 130, browser exit 0).
- Added service/HTTP tests for duplicate requests, stale data, timeout uncertainty,
  expiry/restart, partial writes, caller cancellation, strict schema, cross-origin
  rejection and chunked body limits. Full regression result recorded below.

Full regression after content writes: **916 passed, 0 failed, 0 skipped**. JavaScript
syntax and git diff whitespace checks passed. This does not close the remaining
integration, history, complete-action, 3D, or performance acceptance gates.

### Bounded activity endpoint and inspector

- Wired capability-tested committed history into the standalone host and inspector.
  Strict 1–100-entry pages, revision-bound continuation, a 1,000-snapshot / 4 Mi
  character source cap, and a 64-entry shared single-flight cache. Invalidated
  continuation returns 409 rather than combining different source revisions.
- One history revision probe accompanies each shared issue polling cycle. It does
  not replace issue exports. Separate history health/revision SSE invalidates
  demanded activity even when current issue fields are identical; unchanged probes
  preserve the snapshot and publish nothing.
- Cold reads validate the source revision before/after CLI history. Stale history
  disables new loads. Commit time/committer remain snapshot provenance, not
  invented edit times/authors; coverage always admits missing/working-set history.
- 66 dashboard tests pass, including ten concurrent readers sharing one history
  load, pagination, independent issue/history invalidation, stale/changed-during-
  read rejection, unchanged-source no-event behavior and real HTTP validation.
- This is committed snapshot browsing, not completion of the timeline event model,
  observation events, shared-server verification or full performance gates.

- Disposable Beads 1.2.2 HTTP smoke returned real commit snapshots and distinct
  older continuation pages. Headless Chrome loaded the history inspector and
  asserted coverage/committer caveats; its 1671x941 screenshot was visually reviewed.
  Smoke server/browser were stopped and confirmed terminal (130/0).


The first full activity regression run reported 921 passing / 1 failing test:
`CancelledSupervisorDoesNotRetryFailedAgents` read a StringWriter/StringBuilder
concurrently with background writes. Its fixture now uses synchronized output and
the same monitor for log snapshots; production supervisor behavior is unchanged.
Complete rerun: **922 passed, 0 failed, 0 skipped**. Final JavaScript syntax and
whitespace checks passed. The full dashboard goal remains in progress.

### Native WebGL timeline foundation

- Added embedded native WebGL perspective/orthographic rendering, mesh tubes,
  spherical/diamond markers, grid/NOW plane and a no-WebGL/context-loss Canvas 2D
  fallback. Actual recorded creation/comment/closure fields and lazily loaded
  committed snapshots supply stable source markers; no fake Git branches/merges.
- Stable lane assignment, per-issue projection reuse, lane/time clustering,
  24-lane pages, shared inspector selection, pinned event card, keyboard camera
  controls and accessible event list. Recorded timestamp fields are now explicit
  nullable public projections, never synthesized from collection time.
- Local playhead/minimap/speed/range/fullscreen controls and historical unknowns.
  Returning live rereads current issue content; editing is disabled while replaying.
  History source movement invalidates loaded browser history independently.
- Interruptible fit/focus/projection transitions, reduced-motion preference/override,
  settled/hidden-tab frame shutdown and camera redraw without geometry uploads.
  No camera/playback operation invokes a source CLI.
- 135 dashboard + command-syntax tests pass; six dependency-free Node model/camera
  tests cover no historical backfill, provenance, ambiguous equal-time snapshots,
  stable lane identity, 10,000-event clustering and real perspective vs orthographic
  scaling. No performance acceptance claim follows from these unit checks.
- Reusable browser fixture/check scripts live under tests/browser. The synthetic
  three-lane fixture is explicitly test-only and never contacts Beads.
- Remaining scene gates include validated Git trunk/curves/integration, observed
  transition history, all filters, complete motion/keyboard/drawer treatment,
  range handles, camera persistence and the documented full-scale benchmark.
- Chrome software-WebGL smoke passed at the reference's 1671×941 viewport:
  explicit perspective assertion, three lanes, lazy history, pinned comment,
  orbit geometry reuse, settled idle, 2D, playback refusal, current reread,
  draft preservation, zero camera/playback API requests, reduced motion,
  context-loss fallback and narrow viewport. The screenshot was visually reviewed;
  it is an independent-lane foundation, not the complete reference fixture.
  Temporary fixture server/browser were stopped and confirmed terminal (130/0).
- Final follow-up hides a pinned future/current-only callout during historical
  playback; JavaScript syntax/model tests and whitespace checks remain clean.
  Full-suite baseline before this scene work was 922 passing; the scene changes
  have the focused 135-test and browser/model verification above.

### Attention sequencing foundation (not enabled)

- Refactored ResolveUserAttentionAsync so the existing standalone CLI and the web
  action service can share its response-first, label-second sequence. Comment
  arguments now use an explicit option boundary; normal standalone behavior and
  optional reopen remain unchanged.
- Added internal request/plain-resolution sequencing, verified stored-comment IDs,
  post-failure rereads and explicit partial outcomes. A same-request retry returns
  the original result instead of appending again. Reviewed request completion can
  refer to an existing stored explanation and perform only the missing label step.
- Attention writes default to DISABLED, including the production ForRepository
  factory. HTTP submissions are rejected before source access. No attention
  controls were added to the browser and no real attention mutation smoke ran.
  Auto-review rejected expanding unauthenticated write access without specific
  approval; an asynchronous question asks Ollie to approve attention requests and
  plain resolution on trusted-network/non-loopback listeners. Await the response
  before enabling it. The full goal is not blocked: other work remains available.
- Existing attention/Git regression: 44 passed. Final action/attention verification:
  22 passed, including disabled-by-default behavior and a purely in-memory
  response-success/label-failure test with same-ID replay. The proposed broader
  UI, capability-probe and attention-test command was rejected and did not run.
- Still required: explicit enablement approval, UI/composer recovery controls,
  broader schema/failure tests, real Beads/browser smoke, and separately guarded
  Resolve and reopen (not implemented for HTTP).

### Attention and Git-history approval resolved

Ollie explicitly approved attention requests/plain resolution on unauthenticated
trusted-network/non-loopback listeners, then separately approved Git commit
author/message disclosure. Both prior auto-review holds are resolved.

- Production attention actions are enabled and capability-reported; direct test
  services still default off. The composer offers request/resolve and a reviewed
  missing-label-step operation using the verified stored comment ID. No reopen,
  terminal-status or assignment mutation was enabled.
- Real disposable Beads HTTP smoke verified exact hostile-looking explanations,
  actor attribution, label add/remove, same-ID replay and unchanged status/assignee.
  Real Chrome submitted request/response through the composer and safely rendered
  literal HTML text.
- Bounded read-only branch history endpoint/inspector exposes full commit/parent
  IDs, author, commit timestamp/message and explicit gaps. Full-tip/limit caches,
  before/after ref checks, ETags, strict literal-ref/query validation, topology
  ordering and clock-skew detection are covered.
- Chrome loaded actual main-branch history and its non-integration-time caveat.
  Same-tip shallow-boundary invalidation remains required for final acceptance.
- One compiler invocation exited 139; it was confirmed terminal and retried.
  The rebuilt 90-test dashboard/attention suite passed without analyzer warnings.
  A final real-Git skew regression was then added and is verified below.

Final verification: **91 dashboard/attention tests passed**, including real Git
clock skew. Both real-browser screenshots were reviewed at 1671×941; a small
commit-header spacing fix followed. JavaScript syntax and whitespace checks pass.
Smoke server/browser stopped and were confirmed terminal (130/0). Full dashboard
completion remains unproven; runtime integration, remaining writes, validated
Git bindings, complete scene/history semantics and performance gates remain.

### Same-tip Git history deepening

- The host-owned Git probe now fingerprints the canonical, bounded shallow-file
  contents using a Git-reported path (including linked-worktree shared storage).
  Boundary changes publish a new Git revision even when local refs stay fixed.
- History single-flight keys, serialized HTTP bodies and ETags include that
  boundary. Before/after checks reject detected changes during reads; the browser
  clears loaded history when its branch tip or boundary changes. This remains
  read-only: dashboard code never fetches or repairs a clone.
- A disposable real Git shallow clone regression covers depth 1, same-tip deepen
  to depth 2, and unshallow to all 3 commits. It verifies cache reuse when unchanged,
  new source revisions, an SSE Git publication, and HTTP 200/new ETag/new body
  rather than a stale conditional 304 after deepening.
- Validation: 92 focused dashboard/attention tests passed; dashboard JS syntax and
  `git diff --check` passed. No new browser interaction test was run for this
  invalidation path. Full-suite and broader acceptance remain outstanding.
- Remaining: comparison/patch caches also need shallow-boundary identity; this
  change only closes the recorded Git history invalidation gap noted above.

### Comparison/patch shallow-boundary follow-up

- Comparison and patch caches now include shallow-boundary identity, with checks
  before/after loading and after cache retrieval. Combined responses reject
  mismatched boundaries, and serialized HTTP comparison bodies use that identity.
- Extended the real shallow-clone regression with disconnected shallow tips that
  become provably contained after unshallowing, without either tip moving. Both
  collector results and HTTP bodies replace unavailable patches with available
  ones; unchanged cached results retain object identity.
- Git shallow-file I/O failures now preserve last-good collector state as stale.
- 92 focused dashboard/attention tests and 6 Node model tests passed. Whitespace
  checks passed. Full .NET suite started in exec session 81686 and remained live
  at the latest poll; its result is not yet known. This supersedes the comparison
  cache gap above, not the broader remaining acceptance requirements.

### Shared dashboard session lifecycle groundwork

- Extracted repository initialization, source projections, HTTP binding and the two
  collector loops into `DashboardSession`. It owns no process signals, controller
  leases, agent processes or parent cancellation source. Standalone now owns only
  signal/exit-code handling and explicitly disposes the shared session.
- Session completion observes all collectors; a failed or finished loop cancels
  peers, and disposal joins them before releasing the listener. Synchronous loop
  startup failures are also observed. Concurrent disposal shares a single task.
  Startup failure cleans up its listener and cancellation resources.
- This is groundwork, not integrated hosting: `run --dashboard`, saved settings,
  runtime publication/control, worker-safe failure handling and mutation draining
  are still outstanding. Project capabilities still correctly say standalone.
- The previously running full regression suite finished: **937 tests passed**
  (before this extraction). After extraction, **95 focused dashboard/attention
  tests passed**, including three lifecycle cases. A final cleanup-only adjustment
  is being rechecked in the lifecycle subset before closing this turn.
- Real disposable-repository smoke checks started the compiled standalone CLI,
  read its project endpoint and sent SIGINT/SIGTERM: exits 130/0 respectively,
  stdout empty and listener closed in both cases. Both processes were confirmed
  terminal. No writes were submitted. No new browser visual claims are made.

Final lifecycle recheck: **3/3 passed** after the cleanup adjustment; no running
smoke/test processes remain from this turn. No goal-completion claim is made.

### In-process issue/Git preview and saved run settings

- Added explicit `run --dashboard` with prefixed bind/port/actor/poll options,
  default disabled. Saved fields participate in normal inheritance, CLI overrides,
  and editor fields; dormant saved tuning is validated. Explicit tuning while
  disabled and prefixed options on other commands are rejected. Preflight reads
  saved settings without starting a host. `--dashboard=false` disables a saved
  enabled setting.
- Run startup creates the shared session after normal preflight and before pool
  ownership, claims and harnesses. Its owner adapter observes web failure, disposes
  web resources and emits sanitized stderr diagnostics without cancelling workers
  or restarting the listener. Finite run scope disposes the session after normal
  cleanup. Standalone behavior remains separate and uses the same host/collectors.
- This preview deliberately reports integrated mode but runtime state/control
  unavailable. The browser now displays that explanation rather than hardcoding
  standalone wording. Start-paused still requires TUI/stdio until web resume exists.
- Remaining integrated work is substantial: authoritative runtime snapshots and
  controls, monitoring reuse, source-independent runtime updates, mutation drain,
  final lifecycle events, full TUI/stdio/supervisor/schedule acceptance and host
  failure tests against live workers. This is not integrated acceptance completion.
- Initial focused configuration/dashboard/CLI run: 232 passed. Full regression:
  946 passed / 1 failed; failure was the lifecycle test rebinding its just-released
  port under parallel socket fixtures. That test collection now runs nonparallel
  to remove competing fixtures during its stop/rebind assertion. New fake-CLI
  end-to-end tests cover occupied-port/no-claims and normal finite run/listener exit;
  the expanded focused run is pending at this entry.

Expanded focused run initially finished 233 passed / 1 failed: an existing HTTP
security test hit a connection reset on its next GET after an oversized request.
The host now explicitly sends HTTP/1 `Connection: close` with an early 413, since
that request body is not consumed. The regression asserts the header and then
uses the same HttpClient for the next request. This is an HTTP cleanup fix, not a
retry masking the failure. Both new integrated fake-CLI cases passed in that run;
the final focused rerun is pending below.

Final focused verification: **234 passed**, including both integrated CLI cases,
configuration inheritance/disablement, lifecycle isolation and oversized-request
follow-up. JS syntax and whitespace checks pass. Full suite has not been rerun
after the two test-discovered fixes; do not claim a current full-suite pass.
All test sessions started this turn are confirmed terminal.

### Lazy aggregate publication and per-issue serialization

- Replaced eager full snapshot serialization on every source event with lazy,
  once-per-current-revision aggregation. SSE-only updates now emit their deltas
  without allocating an unused aggregate snapshot. GET/reconnect still pairs the
  latest source state and cursor atomically under the stream lock.
- Cached serialized issue bodies in ID order; only changed issue IDs serialize
  again, deletions remove bytes, and Git/history/health-only updates reuse issue
  bodies. Git uses its already serialized publication; history and health bytes
  are cached at their own source changes. Raw JSON composition uses only trusted
  serializer-produced bytes, not unvalidated CLI strings.
- Added counters and tests for 20 unrelated Git/history updates, ten concurrent
  snapshot readers sharing one body, exact cursor/source agreement, one-issue
  edit invalidation, escaped hostile text, deletion, health changes and no-op polls.
  **95 dashboard tests passed**. This does not substitute for the 60-second
  ten-client CLI gate or the 1,000-issue/10,000-event browser benchmark.
- Full regression rerun is active (exec session 53959); its result is not yet known.
- Next integrated work should extract the existing RequestAction/ForceSupervisor
  routing from AbacusApplication's ConsoleOutput-only block into a shared direct
  control boundary. Worker snapshots must use explicit safe fields from Output.cs
  state changes, not mirror EventReporter command/warning/raw-detail payloads.

Final verification for this increment:

- **951 full .NET tests passed** (3m 1s), including the earlier integrated CLI,
  lifecycle and oversized-request regressions. All test processes are terminal.
- Opt-in `tests/browser/noop-http.py` ran against the real disposable
  `abacus-web-contract` repository with Beads 1.2.2 / Git 2.55.0 / .NET 10.0.101,
  Python 3.14.6, macOS 26.6.2 arm64. Ten SSE clients observed **60.04 seconds**:
  **zero data events**, **zero repeated history/diff queries**, identical 3,416-byte
  snapshot and ETag, and no stdout output. The host then exited cleanly.
- Detection overhead was not zero: **94 source commands**, **10 issue exports**,
  **26,520 export output bytes**, and **3.9831 cumulative export-command seconds**
  (includes wrapper subprocess overhead). This is a small fixture, not a scale
  benchmark. The script records output sizes/durations, never raw command output.
- Production projection/hash CPU, peak memory and browser FPS are not measured by
  that script. Per-issue/aggregate counters are verified separately by unit tests;
  the full performance acceptance criterion remains incomplete. No runtime-control
  or full timeline-integration completion is implied.

### Authoritative read-only runtime worker publication

- Integrated hosting now connects a safe projection of this process's ConsoleOutput
  worker rows. Source changes enqueue one bounded hint while holding the output
  lock; a separate session-owned loop copies immutable safe fields and serializes
  outside that lock. No polling, subprocess, HTTP loopback or event-log parsing is
  involved. Detail-only/no-op updates do not replace the serialized runtime view.
- Workers/supervisors expose name, activity, issue ID, branch/dirtiness, execution
  active flag, observed exit code and retry count. Raw detail, prompts, command
  output, ticket titles and potentially credential-bearing run locations are not
  exported. Safe process location and claim/schedule/control/recovery fields remain.
- Added independent `/api/v1/runtime` with process-scoped conditional ETags, runtime
  SSE events and runtime snapshot component. Runtime updates do not serialize
  issues or build an aggregate snapshot until requested. Standalone remains
  disconnected/503; runtime controls remain false in all modes.
- Browser renders current worker rows explicitly independent of historical timeline
  playback. This is not the final runtime control interface or a visual acceptance
  claim. Subscription teardown follows the session's lifetime.
- **152 dashboard/output tests passed**, covering safe-field exclusion, no-op byte
  reuse, direct state transitions, independent publication, subscription cleanup,
  connected capability and HTTP conditional responses. The prior 951 full-suite
  result predates this increment; full regression remains to be rerun.

Final integration recheck: **2/2 CLI end-to-end cases passed**. The finite run
served `runtimeConnected: true` and its actual `alice` worker row over HTTP,
then exited and released the listener; occupied binding still prevented claims.
The one-second harness delay exists only in the disposable fake-CLI fixture.
JS syntax and whitespace checks passed; all test sessions are terminal.

### Direct manual and schedule claim-gate visibility

- Construct the existing ClaimGate (including StartPaused) before web startup and
  pass that same object to runtime projection and worker/supervisor orchestration.
  This changes no claiming permission, schedule policy or ownership behavior.
- Added manual-enabled, schedule-allowed, combined gate-allowed and stable reason
  fields. The UI distinguishes manual pause from blocked schedule/minimum-window
  restrictions and explicitly says allowance does not prove ready work or ownership.
- Gate changes queue bounded runtime hints. Configured schedules get a single
  shared one-second in-memory check for clock-only transitions. Timeout checks
  reuse worker rows and unchanged runtime bytes; no source CLI or countdown-only
  publication is introduced. No schedule means no timer.
- Tests cover manual pause during blocked/open schedules, minimum remaining time,
  unchanged reason/response identity, a clock-only schedule boundary, direct gate
  events, and subscription teardown without changing the authoritative gate.
- **128 dashboard/claim-gate/schedule tests passed**; JS syntax and whitespace
  checks passed. Full regression and visual review remain outstanding for these
  runtime increments. All sessions from this turn are terminal.
- Remaining runtime work: shared safe action routing and accepted/completed state,
  Pause/Resume and worker/supervisor controls, stop-run confirmation, safe process
  locations, recovery alerts, mutation draining and broader integrated acceptance.

### Session-scoped manual claim Pause/Resume

- Added strict typed HTTP pause/resume requests against the same ClaimGate used by
  workers/TUI/stdio. Expected manual state is checked/set atomically under the gate
  lock. Neither action stops running workers, overrides schedule/ownership policy,
  creates a child process nor changes Beads. Other runtime actions remain rejected.
- Requests require the current runtime session and an ID, share the JSON/origin/
  Host/body-size boundary, and retain original outcomes in a 1,024-entry run-local
  ledger. Same payload/ID is never replayed after another interface changes the
  gate; mismatched payload/session and stale expected state are rejected. Ledger
  exhaustion rejects new HTTP actions rather than evicting replay protection.
- Browser uses explicit confirmation and same-request retry after unknown outcomes,
  with separate current-gate display. Buttons disable on SSE disconnect. Cleanup
  disables new controls before joining run shutdown; stored retry outcomes remain
  historical, not claims about current state. Worker/supervisor/stop-run controls
  and their accepted/completed tracking remain unfinished.
- Runtime publication synchronizes the TUI claim indicator; web gate changes also
  emit a safe claims.changed event for stdio/event-log observers. Headless
  --start-paused is now valid with --dashboard (including verbose), because HTTP
  Resume exists; default claim enablement is unchanged.
- **176 dashboard/claim-gate/CLI/stdio tests passed**, including a real CLI fixture
  started paused without TUI/stdio: no claim before HTTP Resume, cross-origin Resume
  rejected, same-origin Resume released work and finite exit closed the listener.
  Unit tests cover stale/manual races, session mismatch, duplicate payload rejection,
  retry after external changes, stopping and blocked-schedule preservation.
- Final bounded-ledger plus CLI rerun is recorded below. JS syntax and whitespace
  checks pass. Browser interaction/visual review and a current full regression are
  still pending; the overall goal is not complete.

Final focused recheck: **8/8 passed**, including bounded-ledger retention and both
integrated CLI cases. A startup diagnostic wording correction followed (no behavior
change). All test sessions are terminal. Full-suite revalidation remains pending.

### Shared worker/supervisor routing boundary

- Extracted the actual TUI/stdio worker and supervisor routing from the
  ConsoleOutput-only branch into `RunControlRouter`. The same AgentControl objects,
  task-completion checks and configured supervisor methods remain authoritative;
  no subprocess or independent action path is introduced.
- Routing explicitly means request acceptance, not cleanup/restart completion.
  Unknown workers, invalid actions, completed workers, duplicate pending requests,
  disabled supervisors and supervisor main-checkout cleaning are rejected. Prompts
  remain literal strings forwarded to existing supervisor validation.
- Added cancellation checks for stopping runs and task-completion checks for
  supervisors: accepting commands into already finished loops is now rejected.
- **121 router/agent-control/stdio/dashboard tests passed** before the final
  completed-supervisor extension. Supervisor/stdio revalidation is pending below.
  Unit cases distinguish acceptance from completion and verify no calls occur on
  rejected shutdown/cleanup requests.
- HTTP still exposes only claim Pause/Resume. Worker/supervisor actions need this
  router attached to the runtime boundary plus request identity/confirmation,
  pending/completed reconciliation and browser affordances before enabling them.
  This is safety groundwork, not a claim that those HTTP features are complete.

Final router/supervisor/stdio revalidation: **110 passed** after the completed-
supervisor checks, including end-to-end supervisor and stdio fixtures. Whitespace
checks passed; all test sessions from this turn are terminal. No current full-suite
or new browser-control acceptance claim is made.

### Worker action completion receipts

- Added optional tracked AgentControl receipts while preserving legacy untracked
  Request behavior. A tracked request is reserved before cancellation signals;
  dequeueing it is not completion, and both tracked and untracked interfaces are
  prevented from overwriting it until worker acknowledgement.
- AgentLoop explicitly acknowledges Stop after reaching its stopped boundary,
  CleanWorkspace after the actual cleanup result (failed cleanup is failed), and
  Restart after resuming dispatch (not a promise that a new harness has started).
  Loop exit/disposal finishes any unacknowledged receipt as outcome-unknown.
- Shared router now offers tracked worker acceptance, retaining known-worker,
  pending-request and finished-run checks. Supervisors are deliberately excluded
  until their own completion points are wired; no inferred completion from row text.
- **128 control/router/stdio/dashboard tests passed** before a final synchronous
  cancellation-observer regression. Unit tests prove accepted/dequeued is not
  completed, matching explicit acknowledgements, cleanup failure, non-overwrite,
  shutdown uncertainty and eventual release of the pending reservation.
- This is not yet an enabled HTTP worker feature: runtime action-ledger attachment,
  typed target/confirmation contract, completion publication, browser controls and
  real end-to-end worker-action verification are the next steps. Existing HTTP
  claim Pause/Resume remains the only enabled runtime mutation.

Final receipt/router recheck: **16 passed**, including synchronous cancellation
observers consuming the request before the submit call returns. Whitespace checks
passed; all test sessions are terminal. Full regression and actual tracked worker
HTTP/browser lifecycle acceptance remain pending.

### HTTP worker action ledger and browser wiring

- Bound the shared worker router into runtime only after controllers/loops exist.
  Strict typed worker POST accepts stop/restart/clean-workspace, exact worker and
  current session/request ID; clean requires explicit confirmation. Existing HTTP
  JSON/origin/Host/body-size checks apply. Supervisor names cannot reach this path.
- Added separate bounded 1,024-entry worker retry ledger. Accepted receipts return
  202, later retries report real worker acknowledgement/failure/unknown without
  applying again. Runtime SSE includes all pending and latest 32 terminal outcomes;
  older accepted IDs retain retry protection. Completion callbacks only queue hints.
- Browser offers confirmed worker actions, disables competing pending actions and
  disconnected controls, preserves same-request retries after unknown responses,
  and labels restart completion as dispatch resumed rather than harness started.
  Focus is restored by worker/action key after live row updates. No visual/browser
  interaction acceptance is claimed yet.
- **127 dashboard/control/router tests passed** in the first run. Then **11 focused
  runtime/worker-action/CLI tests passed**, including actual AgentLoop Stop and
  Restart acknowledgements over HTTP while the disposable CLI remains claim-paused,
  followed by claim Resume, finite completion and listener cleanup. Cleanup refusal
  without confirmation and shutdown-unknown receipts are covered in unit tests.
- Real destructive-cleanup behavior, active-harness interruption, browser controls,
  ledger exhaustion under concurrent clients and full regression remain acceptance
  work. Supervisor actions and confirmed Stop Run are still not implemented. The
  original full goal remains active and incomplete.

Final safety follow-up: cancellation callback exceptions after request installation
are retained as outcome-unknown rather than allowing the same ID to reapply an
already installed action. **5 worker-ledger tests passed**, including that regression.
JS syntax/whitespace checks pass; all test sessions from this turn are terminal.

### Confirmed Stop Run through shared shutdown

- Added strict session-scoped `POST /api/v1/run/actions` for confirmed stop only.
  Integrated startup binds the same cancellation/recovery callback used by stdio
  shutdown, after worker controllers exist; standalone/unbound/stopping runs reject.
- One retained accepted request identity bounds memory and prevents concurrent or
  retried shutdown dispatch. Changed payloads are rejected. Cancellation callback
  failures never replay the signal. Acceptance never claims cleanup completion.
- Shutdown dispatch does not wait for HTTP response delivery: slow/disconnected
  clients cannot defer the accepted command. The response may therefore be lost;
  the UI retains same-request retries and explicitly treats disconnection as unknown,
  not proof that cleanup completed. The normal run owns listener disposal afterward.
- Added operator-attributed Stop Run confirmation and disconnected/pending disabling.
  Real browser interaction/visual acceptance remains pending.
- Initial focused tests: **12 passed**, including the actual compiled CLI started
  paused, cross-origin/unconfirmed refusal, accepted shutdown, no claim, zero exit,
  stdout purity, summary and closed listener. Unit tests cover concurrent dispatch,
  stale sessions, changed payloads and callback failures.
- Full regression: **984 passed in 3m11s**, before the final adjustment moving
  shutdown dispatch ahead of response delivery and an additional unavailable-control
  unit case. The final focused rerun for those changes is recorded below. Node's
  six timeline-model tests and JS syntax/whitespace checks also passed.
- Active-harness shutdown/recovery, mutation draining, full browser controls,
  supervisor actions and the remaining original specification gates are unfinished.

Final non-blocking-dispatch recheck: **13 passed**, including all three real CLI
lifecycle cases and unbound/stopping rejection. All test processes are terminal;
no browser/active-harness acceptance claim is made. Goal remains active.

### Supervisor Stop/Restart with authoritative receipts

- Maintenance and continuation expose tracked Stop/Restart through the shared run
  router. Stop acknowledges only at the disabled boundary after active execution
  cleanup. Restart acknowledges re-enabling/resetting existing retry policy, not
  harness startup or successful repair. Loop disposal leaves unacknowledged actions
  outcome-unknown; main-checkout cleanup is never permitted.
- Added a separate supervisor HTTP route and bounded retry ledger using the same
  strict session/request/target schema and security checks as worker controls.
  Runtime publication separates supervisor receipts from worker receipts; unchanged
  views remain cached. Worker targets cannot route through supervisor actions.
- Browser supervisor rows now offer confirmed Stop/Restart, no Clean Workspace,
  with operator attribution and existing pending/disconnected/same-ID retry logic.
  Force Run remains unavailable over HTTP pending tracked force-run lifecycle work.
- Initial supervisor/router/runtime tests: **120 passed**. Added further HTTP
  cross-origin/schema/cleanup/wrong-target tests, actual maintenance Restart receipt
  coverage, and both supervisors' active-cleanup-failure unknown-outcome tests.
  Final expanded result follows. JS syntax and whitespace checks pass.
- Real browser control acceptance and a fresh full suite after these supervisor
  changes remain pending. No claim is made that the full goal is complete.

Final expanded supervisor/HTTP/router/runtime/CLI check: **127 passed**. Both
active-cleanup-failure cases retained outcome-unknown rather than falsely reporting
Stop completion. All test processes are terminal. Full-goal work remains active.

### Supervisor force-run lifecycle receipts

- Added prompt-free force receipts and tracked routing into both actual supervisor
  queues. A receipt follows only its consumed prompt, not a concurrently active
  automatic run. Existing terminal/stdio force calls retain their queue semantics.
- Completion is reported after the owning check's harness cleanup and verification;
  failures are failed, continuation deferral is deferred, queued prompts explicitly
  discarded by Stop are cancelled, and abandoned/unverified outcomes stay unknown.
  Finishing a loop closes its force queue and prevents post-exit acceptance.
- This is lifecycle groundwork, not an enabled HTTP Force Run feature. Typed HTTP
  prompt validation/confirmation, bounded same-request retention, runtime outcomes
  and browser submission/interruption still need wiring before exposing it.

Force lifecycle verification: **123 supervisor/router tests passed**, covering both
supervisors' tracked and legacy force prompts, successful verification, crashes,
failed cleanup, queued Stop cancellation, closed queues, and a queued force request
remaining pending through the preceding automatic run. Final literal-prompt/router
check: **5 passed**. Whitespace checks pass; all test sessions are terminal. No
current full-suite or HTTP/browser Force Run acceptance claim is made.

### Confirmed supervisor Force Run over HTTP and browser

- Supervisor action schema now accepts confirmed force-run with a bounded nonempty
  literal prompt (16,000 characters, no NUL). Other actions reject prompts. The
  worker endpoint cannot route force commands. Prompt text never enters runtime
  snapshots/outcome publications; ledger content remains bounded per session.
- The existing supervisor ledger retains accepted force request identities and
  tracks actual force receipts. Concurrent retries dispatch once, changed payloads
  reject, ledger exhaustion never evicts accepted IDs, and terminal retries do not
  replay even after controls stop. Completed/failed/cancelled/deferred/unknown stay
  distinct. A second force request for a pending target rejects while Stop/Restart
  stay available for interruption.
- Browser uses a separate force draft from Stop/Restart, operator confirmation,
  policy warning and same-request retry after lost response. Terminal SSE wins over
  late accepted HTTP. Disconnection disables new controls without losing drafts.
- **249 dashboard/supervisor/router tests passed**, including HTTP prompt/refusal/
  interruption boundaries, 32 concurrent retries, 1,024-entry exhaustion, no prompt
  disclosure, terminal outcomes, strict schema and existing supervisor lifecycles.
- A real disposable headless Chrome test against a synthetic UI fixture passed
  lost-response retry, Stop during pending force, SSE/HTTP race and disconnected
  disabling. Screenshot inspection revealed timeline/runtime overlap; fixed flex
  sizing and scrolling, and added desktop/narrow nonoverlap assertions. Final
  browser recheck follows. This is not real-harness browser end-to-end acceptance.

Final browser recheck passed, including 1280px/390px nonoverlap assertions; the new
screenshot was visually inspected and runtime controls no longer overlap playback.
An intermediate rerun exposed nondeterministic Chrome transport retry after socket
closure in the fixture (not a production-controller failure). The fixture now
returns a deterministically unreadable accepted response to exercise client retry
without relying on browser transport heuristics. All fixture/Chrome/test sessions
are terminal. Production HTTP/lifecycle tests and synthetic browser evidence remain
separate; real-harness browser acceptance and a fresh full regression are pending.

### Graceful issue-mutation draining

- Mutation admission now closes atomically with a snapshot of all accepted ledger
  tasks. Same-ID retries still return their original task/result; new operations
  reject while draining. Idempotent drain waits 10 seconds for verification, then
  cancels outstanding CLI work and observes all accepted tasks before host exit.
- Repository mutations use a lifetime separate from read collectors. Session
  cancellation therefore stops polling without abandoning accepted writes. Host
  disposal disables runtime controls, drains writes, then completes streams and
  shuts down HTTP. Startup failure also disposes mutation resources. Host/session
  disposal remain idempotent.
- Grace expiry preserves outcome distinctions: attempted but unverified work is
  unknown; work cancelled before execution rejects. Explicit cancellation checks
  before commands/attention steps prevent already-cancelled operations from starting
  after a source read completes concurrently with shutdown.
- Initial dashboard check: **131 passed**. Direct drain tests cover admission,
  same-ID retries, queued work and grace expiry. Real HTTP tests cover both direct
  host and session-owned shutdown: accepted response verification finishes before
  the listener closes, while new requests reject. Full regression and final
  cancellation-after-read check results follow.

Full regression: **1,014 passed in 3m16s**, before the final explicit pre-command
cancellation check and cancellation-callback-failure join safeguard. The drain now
joins accepted operations even when a cancellation callback throws. Final focused
mutation/session verification follows; no full-goal completion claim is made.

Final mutation/session recheck: **32 passed**, including concurrent source-read
cancellation, throwing cancellation callbacks, grace expiry, and HTTP/session drain.
Whitespace checks pass. All test sessions are terminal; the original goal remains
active with remaining issue actions, historical/Git completeness and acceptance work.

### Ordinary issue label deltas

- Added typed edit addLabels/removeLabels arrays with bounded sizes, duplicate and
  overlap validation, and reserved abacus:/gt: protection. CSV separators/quoting
  cannot smuggle reserved labels through bd string-slice flags. Other action types
  reject generic label fields; dedicated attention flows retain their safeguards.
- Thin Beads label helper emits literal --add-label= / --remove-label= arguments,
  never whole-set replacement. Existing reread/revision, actor, serialized write,
  verified result and retry semantics apply. Verification preserves unrelated old
  labels while tolerating independent additions; racing removals report partial.
- Browser content editor offers separate newline-delimited label deltas and retains
  them in the existing pending/retry/playback-safe draft. Full browser composer
  interaction acceptance for the new fields is still pending.
- **145 dashboard tests passed**, including label validation, leading-option literal
  flags, reserved preservation, racing label additions/removals and same-ID retries.
  Upstream CLI reference and installed help were checked. No active Beads workspace
  exists in this source repo; none was initialized.
- Real bd/HTTP smoke in the existing disposable /tmp/abacus-web-contract fixture
  passed reserved rejection, unique leading-dash add, verified removal and original
  label-set preservation. Script retained as tests/browser/labels-http.py; fixture
  update history changes are explicit. Server/test sessions are terminal; JS syntax
  and whitespace checks pass. Other issue actions and full-goal gates remain open.

### Browser issue-edit acceptance and untouched-field preservation

- Added a real Chrome composer contract check against the synthetic three-lane
  fixture, with explicit racing source updates, conflict review and an unreadable
  accepted response. Label drafts survive navigation/playback/live updates and
  uncertain responses retry identical payload/ID rather than issuing another edit.
- Inspection exposed that label-only UI edits also sent untouched title/description/
  priority. The composer now sends only fields changed from its draft baseline,
  preventing stale untouched values overwriting external changes after conflict
  review. Empty edits make no mutation request. Successful results refresh stored
  values while completing a comment retains unsent title/label/append-note drafts.
  Review confirmation now includes operator attribution.
- Final issue-composer Chrome check passed, including all above cases, and its
  screenshot was visually inspected. Full timeline Chrome regression also passed
  (WebGL, three lanes, lazy history, camera reuse/idle, playback, reduced motion,
  context loss, narrow layout), plus **6 Node model tests**. An intermediate wrapper
  failed to connect to the next Chrome instance; it terminated, and the timeline
  check was rerun successfully on an independent disposable DevTools port. CDP_PORT
  is now configurable. All fixture/browser sessions are terminal.
- This is synthetic browser acceptance plus the prior real bd/HTTP label evidence,
  not a full production browser-to-Beads run. JS syntax/whitespace checks pass.
  Remaining issue actions and full original acceptance gates are still open.

### Sortable, paginated current issue table

- Added pure table projection with five sortable columns, deterministic ID ties,
  numeric priority and missing-last ordering. Renders 25/50/100 rows (default 50),
  clamps out-of-range pages and resets paging on filter changes. Source arrays are
  not mutated; paging/sorting does not request source data.
- Keyboard-operable header buttons expose aria-sort; pager announces ranges and
  counts. Selection/inspector survives leaving the page/filter with explicit notice.
  URL retains sort/order/page/size and supports navigation restoration/reload.
  Current table search remains IDs/titles; unifying richer timeline search/filter
  scope is still required for the complete specification.
- **3 pure model tests passed** with 1,000 issues, bounds/filter/tie/null checks.
  **7 compiled host tests passed**, including serving the new embedded module.
  Initial real Chrome table fixture passed 123-row pagination, sorting/aria, filtering,
  selection retention and no additional API requests. Final URL/reload recheck follows.
  This is not the full rendering scale/performance acceptance gate.

The URL/reload check exposed a startup race: project metadata could render before
issues loaded and clamp a deep-linked page to zero. Table normalization now waits
for the first authoritative snapshot. Final Chrome recheck passed navigation and
reload with sort/page/size preserved, plus all prior table assertions. Syntax and
whitespace checks pass; all browser/test processes are terminal. Goal stays active.

### Shared issue metadata filters

- Added shared exact assignee/label, priority and attention predicates to the issue
  table and live timeline. Native filter controls are view-only, combine with
  status/search, reset table paging and retain metadata filter values in URLs.
  Priority/attention have explicit Unknown choices; clearing never affects dispatch.
- Historical snapshots do not yet carry metadata fields. Historical filtering
  therefore uses unknown values, never today's labels/owner/priority. A coverage
  notice explains unmatched historical lanes; returning live restores current
  matches. Recorded historical metadata, type/target filters and common loaded-text
  search remain required follow-up, not silently treated as complete.
- **12 Node model tests passed**, including shared conjunctions and no historical
  backfill. Real Chrome table/filter fixture passed cross-view matching, playback
  unknowns, return-live, clear and URL reload alongside prior paging assertions.
  Real host module-serving recheck follows. This is not full filter/scale acceptance.

Final host check: **7 passed**, including embedded filter module serving. JS syntax
and whitespace checks pass. All browser/test sessions are terminal; goal remains
active with the full outstanding requirements unchanged.

### Recorded metadata projection and type/declared-target filters

- Historical DTOs now preserve present assignee/priority/labels/type/declared target.
  Missing/null labels stay unknown, distinct from a recorded empty set. Invalid
  typed metadata rejects history. Real CLI history fixture proves priority/type,
  but does not provide labels/assignee; no current-field backfill fills those gaps.
- Timeline snapshots carry recorded metadata, historical inspector shows it, and
  shared predicates use it at the playhead. Equal-time consensus is field-specific:
  disagreements become unknown instead of arbitrary source-order choices; agreeing
  fields remain usable. Label set order alone is not a historical disagreement.
- Current projection and both views support exact type and declared-target filters.
  Target means only recorded abacus_target, not a configured default, validated
  binding or Git destination. Effective-policy/binding filtering remains outstanding.
- **147 dashboard tests passed**, including source projection and real-fixture
  metadata omissions. **14 Node model tests passed**, including historical/current
  disagreement, missing-vs-empty labels and equal-time metadata conflicts. Final
  browser type/target/filter recheck follows; complete history coverage is not claimed.

Final Chrome cross-view filter check passed with type/declared-target conditions,
URL restoration, playback unknowns and return-live behavior. Syntax/whitespace
checks pass; all browser/test processes are terminal. The full goal remains active.

### Shared loaded-text search with explicit coverage

- Issue table and timeline now share recorded note/comment text matching from the
  same loaded projection. Range bounds apply to recorded text; historical playback
  additionally excludes future text and today's undated notes/comments/title.
  Current undated content is searchable only in live views, without generating
  events or pretending it was recorded inside the selected time range.
- Visible coverage reports range and number of loaded issue histories, explicitly
  excluding older/unloaded history. Explicit history loading refreshes table search
  results. Search/filter/view changes make no additional source reads. Branch view
  retains actual branch-name search without guessed issue execution associations.
- **15 Node model tests passed**. Real Chrome search fixture passed table/timeline
  parity, zero matches before explicit history loading, note/comment matching after
  loading, pre-playhead refusal, live-only undated notes and no implicit API reads.
  Compiled HTTP host regression follows. Full performance/history coverage and other
  original requirements remain outstanding.

Final compiled host recheck: **7 passed**. JS syntax/whitespace checks pass, and all
browser/test sessions are terminal. The full original goal remains active.

## Recorded relationship inspection

The preceding goal turn answered a scope/status question and made no implementation
progress. Revalidated the current source and continued the pending dependency
inspector work without changing the full completion criteria.

- Added allowlisted dependency DTOs from explicit export edges, retaining raw
  direction/type without inferring parentage or readiness from names/counts.
  Missing, malformed, duplicate, mismatched-owner or count-inconsistent data has
  unknown coverage rather than becoming a fabricated empty relationship set.
- Added incremental reverse-edge indexing and current inspector links, including
  missing-target text, export-scoped incoming coverage, and navigation. Reverse
  changes invalidate the relationship panel independently of the selected issue's
  revision. Playback explicitly hides current edges as historically unknown.
- Real Beads 1.2.2 export projection and malformed/unknown coverage tests added.
  **155 dashboard tests passed**, **17 Node model tests passed**, and real Chrome
  verified links, missing targets, navigation, incoming-only live updates, and
  historical coverage. A final malformed-count guard received a focused recheck.
- Relationship mutation, historical edge reconstruction, verified ownership,
  full Git integration, scale benchmarks and the other original acceptance gates
  remain pending. This is read visibility, not complete dependency management.

## Inspector panels and related ongoing tickets

The preceding goal turn made verified implementation progress on dependency
projection and browser inspection. Continued from current source/spec evidence.

- Replaced the decorative Overview label with semantic Overview/Activity/Git
  panels, roving tab focus and Left/Right/Home/End keyboard navigation. Panel
  selection survives issue navigation and URL restoration without fetching data
  or rebuilding/discarding editor drafts. The live editor remains outside panels.
- Activity groups existing comments and explicit committed-history loading. It
  does not yet claim a full reconciled chronological event feed. Git explicitly
  identifies missing validated execution evidence and offers repository browsing;
  it never guesses an issue branch from a name or assignee.
- Added labeled current header status, copyable issue ID with clipboard failure
  guidance, and scoped, deduplicated ongoing-ticket links from direct recorded
  edges. Related closed issues remain relationships, not ongoing work. Current
  ongoing links remain hidden in historical playback.
- Real Chrome passed keyboard/ARIA state, draft retention, no tab-triggered reads,
  explicit history, URL/popstate/reload, related-ticket navigation and narrow
  viewport checks. All **17 Node model tests passed**. Full regression and the
  existing editor-browser regression were started and are tracked to completion.
- Remaining inspector Git evidence, ownership/claim details, activity reconciliation,
  mutations, performance, packaging and all other full-spec gates remain required.

Final verification: **1,037 full-suite tests passed in 3m05s**, with no failures or
skips. The inspector and existing edit-composer Chrome checks both passed; the
390px inspector screenshot was inspected and shows separated, readable panels.
The updated HTTP asset/semantic-panel assertions receive a final focused run.
No production changes followed the full-suite build; only host test assertions
and this verification record were added. Full goal completion remains unproven.
Final focused host run: **7 passed**. All test and browser sessions are terminal;
whitespace and JavaScript syntax checks are clean.

## Issue-specific binding and comparison evidence

The preceding user-facing turn was a remaining-scope answer, not implementation
progress. Revalidated the existing routing parser, target validator, Git comparison
boundary and current worktree before continuing this pending requirement.

- Reused Beads routing parsing and `TargetRegistry.Validate`; kept routing objects
  internal/JSON-ignored on issue summaries. Added a read-only issue Git endpoint
  with no caller-selected refs. Unbound issues never acquire guessed branches.
- Explicit load validates current policy/binding, checks recorded start object and
  ancestry, compares actual refs using existing triple-dot caches, and rechecks
  policy/tips/history before returning validated evidence. Missing branches,
  unknown start history, stale sources and issue revision races stay explicit.
  Negative ancestry is divergence-or-incomplete-history, not proof of divergence.
- Git panel now renders effective target, recorded binding/start, current tips,
  ahead/behind, file summary and ancestry containment with no claimed merge time
  or live ownership. Selection/playback/source invalidation aborts stale requests;
  panel switching itself does not query sources.
- **156 dashboard tests passed**. Final expanded real-Git HTTP/host checks:
  **8 passed**, including no-binding despite existing branch, valid start ancestry,
  actual fast-forward containment, policy-file changes, malformed target,
  missing start object, unrelated start, missing branch, stale source refusal and
  non-disclosure of raw routing metadata. Real Chrome inspector test passed with
  explicit unbound-evidence loading added to its keyboard/draft/URL checks.
- The previous **1,037-test** full regression predates this increment. No broad
  full-suite claim is made for these new changes. All current sessions terminated.
- Still required: worktree/dirty-content evidence, issue Git commits/patches,
  historical reconciliation/connectors, remaining issue mutations, real-run
  acceptance, scale/release gates and the complete original acceptance audit.

## Bound issue Git patch and commit detail

The preceding goal turn made verified progress on issue binding evidence. Extended
that actual boundary rather than falling back to unverified branch-name links.

- Optional, strictly parsed `patch`/`history` booleans load detail only after a
  policy-valid recorded binding and positive start ancestry. No caller-supplied
  refs are accepted. Details use existing bounded, shared Git caches and repeat
  policy/tip/shallow-boundary checks before publication. Unbound/invalid/diverged
  evidence cannot produce patch/history content.
- Inspector buttons explicitly load a 4 Mi-character bounded triple-dot patch or
  latest 100 reachable commits. History preserves parents and author/message,
  reports bounds/clock skew/coverage, and identifies shared ancestors rather than
  attributing all branch history to this ticket. Content uses text nodes; details
  are replaced on a new detail load and cleared on source/selection/playback change.
- **156 dashboard tests passed**, including real-Git HTTP patch content, pinned
  history tip/commits, absent unrequested details, unbound refusal and malformed/
  duplicate query rejection. **17 Node tests passed**. Real Chrome detail checks
  passed literal HTML-like content, explicit loading, history warnings, source
  invalidation and historical disablement. All sessions terminated; syntax and
  whitespace checks pass.
- Worktree/dirty-content evidence, full timeline Git integration, remaining issue
  mutations, real-run acceptance, performance/release checks and the original
  all-requirement audit remain pending. The full goal stays active.

## Registered worktree status evidence

The previous goal turn made verified progress on issue Git detail. Added the
remaining read-only worktree summary boundary from current source/spec evidence.

- Shared Git reconciliation now probes registered checkout status with optional
  locks disabled. Checks common-repository identity before/after status and matches
  observed HEAD/branch against registration. Bare/prunable/unavailable/changed
  worktrees retain explicit unknown state without discarding unrelated Git facts.
- Status participates in stable Git publication identity; ignored cache changes
  do not create updates. Branches renders clean/dirty/unknown, and validated issue
  bindings show literal attached-branch registrations. A collected HEAD mismatch
  suppresses clean/dirty claims. Detached worker ownership remains unverified and
  is explicitly not inferred from branch names or matching commits.
- New real-Git tests cover tracked/untracked dirtiness, ignored caches, detached
  worktrees, missing registrations, stable no-op snapshots and unchanged index
  bytes/refs. Initial test runs exposed macOS physical-path expectations and an
  immutable-array identity assertion, corrected to Git-authoritative paths and
  element comparison. One initial drain test hit a terminal ephemeral-port bind
  collision; its following run passed. Final full dashboard rerun is tracked.
- Chrome issue-Git detail checks passed with dirty/unknown checkout labels added.
  Repeated already-dirty content revisions and demand-loaded uncommitted diff are
  still required; a dirty boolean is not presented as fulfilling that requirement.
  Additional status probes also require a refreshed idle/scale subprocess budget
  measurement. Other original mutation/runtime/history/release gates stay open.
Final result: **157 dashboard tests passed**, **17 Node model tests passed**, and
Chrome detail checks passed. All sessions are terminal; whitespace/JS syntax checks
are clean. No full-goal completion claim is made.

## Tracked worktree content revision foundation

The preceding goal turn made verified worktree-status progress. Added the actual
tracked-content read boundary needed for the remaining repeated-dirty requirement.

- Bounded staged/unstaged binary patches are separately read and fingerprinted;
  repeated text or binary edits invalidate content identity despite identical
  HEAD/status/path lists. Reversed staged/working changes cannot cancel into an
  apparently empty HEAD diff. Common Git directory, registration, HEAD, status
  and consecutive content observations are checked; changing reads are refused.
- Registered opaque IDs only, eight in-flight reads maximum, same-snapshot shared
  reads, caller cancellation isolation, combined 4 Mi-character bound and explicit
  HTTP content ETags. Unknown IDs/arbitrary paths/query extensions are rejected.
- Branches exposes explicit load/refresh snapshots with literal patch text and
  prominent limitations. This is **not yet automatic live reconciliation** and
  excludes untracked contents. Those original requirements are still open, not
  redefined as manual tracked-only success. No new per-browser polling was added.
- **158 dashboard tests passed** before the final same-registration source-key
  refinement. Expanded real-Git HTTP test then passed: repeated text/binary edits,
  staged reversal, unchanged HEAD/porcelain revision, fresh versus unchanged ETags,
  ten callers sharing one result, arbitrary path rejection and HEAD-change refusal.
  Real Chrome snapshot load/refresh/escaping/coverage checks passed.
- Remaining immediate work is shared visible-detail reconciliation and untracked
  content handling, then the unchanged mutation/runtime/history/performance/release
  acceptance scope. All observed test/browser sessions are terminal.

## Untracked content coverage

The previous goal turn made verified tracked-content progress. Closed its explicit
untracked-content gap while retaining the original automatic-reconciliation gate.

- Extended bounded snapshots with separate untracked patches from ignore-aware
  Git path enumeration. Git no-index binary patches fingerprint actual text/binary
  bytes and symlink link text, not merely the untracked path list. Literal filenames
  are argv values; arbitrary client paths, parent traversal, intermediate symlink
  directories and nested untracked repositories are refused.
- Combined staged/unstaged/untracked output stays within 4 Mi characters and a
  256-untracked-file ceiling. Failures do not produce partial successful content
  revisions. Double-read/status/registration/HEAD consistency checks remain.
- Verified Git's symlink no-index contract locally before using it. Expanded real
  tests prove repeated untracked text/binary changes, an option-like filename,
  symlink target non-disclosure, ignored-cache stability, file-count rejection and
  output-limit rejection, alongside existing ETag/concurrency/HEAD checks.
- **158 dashboard tests passed**. Chrome worktree checks passed explicit refresh,
  untracked pane replacement and literal HTML-like content. All sessions terminal;
  syntax and whitespace checks pass. Current user docs/changelog were revised,
  rather than preserving the obsolete claim that untracked contents are absent.
- **Still pending:** shared automatic visible-content reconciliation, detached
  worker association, remaining issue mutations, richer history/timeline evidence,
  runtime acceptance, scale/release verification and the full original audit.

## Shared live worktree reconciliation

The preceding goal turn made verified untracked-content progress. Connected the
content boundary to the real shared Git collection loop, replacing the manual-only
UI limitation rather than treating explicit refresh as acceptance completion.

- Added a bounded worktree watch hub: eight topics, 64 subscribers, one pending
  source read per topic, one shared serialized body, and one latest-state queue
  slot per client. Unchanged content is silent. Reconnect gets a full latest state;
  slow readers may skip snapshots, never apply partial deltas. Errors publish
  sanitized unavailable state without stale content; same-revision recovery emits.
- Existing Git reconciliation drives watched details once per server interval,
  including when ordinary Git ref/dirty-summary revision did not change. No client
  polling timers or client-triggered periodic Git subprocesses were added. Last
  subscriber removal drops interest; an already-running bounded read may finish.
- Added registered-ID SSE endpoint with heartbeat and linked host shutdown token.
  Browser uses one detail stream, clears disconnected/unavailable content, and
  releases/reconnects interest on view/page visibility changes.
- Five focused worktree tests passed, including ten-reader shared reads, no-op
  silence, newest-state backpressure, bounded capacity, stale recovery and real
  Git plus HTTP repeated-edit delivery. The HTTP disconnect test initially exposed
  HttpClient's default response-drain delay; disabling drain for the endless test
  stream and explicitly disposing its reader verifies actual immediate disconnect.
  This was a test-client cleanup correction, not a server retry workaround.
- Chrome passed automatic edits without Refresh, stale clearing/recovery, literal
  untracked content and view-scoped unsubscribe/reconnect. **17 Node tests passed**.
  Final full dashboard rerun is tracked separately below.
- Remaining gates include actual scale/subprocess/memory measurements for these
  bounded full-content reads, shutdown/failure acceptance across real harnesses,
  detached ownership association, remaining issue mutations, historical/timeline
  integration, packaging and the full original requirement audit.
Final result: **161 dashboard tests passed**. All test/browser sessions are terminal;
syntax and whitespace checks pass. Shared automatic content reconciliation is now
implemented and specifically verified, but the full objective remains active.

## Worktree stream shutdown and current acceptance audit

The preceding goal turn made verified live-reconciliation progress. Closed the
remaining stream-lifecycle gap and checked current scope against the original
numbered acceptance requirements rather than extrapolating from green tests.

- Host shutdown now completes worktree observation before waiting for accepted
  mutations. Completion rejects admission, closes subscriber channels, clears
  topic interest and fences late results. Deferred reads check whether their topic
  still exists before starting; already-running shared reads retain their bounded
  source lifetime. Idempotent disposal cannot decrement client counts twice.
- Added controlled pending-read completion and topic-generation tests, plus a
  real Git/HTTP stream that ends cleanly when the host disposes. **13 focused
  worktree/mutation-drain tests passed**. A current full regression is running.
- Added `docs/dashboard-acceptance.md`, mapping every original numbered criterion
  to present evidence and unresolved work. Updated misleading preview/index text
  and replaced obsolete top-level remaining-gate summaries with the current audit.
- Important audit finding: worktree watch reconciliation currently recomputes full
  patches even when content is unchanged. Event suppression alone does not satisfy
  criterion 6's zero-repeat-diff-query idle gate. Next priority is lightweight
  content identity plus patch-cache reuse, preserving repeated-dirty detection,
  followed by a fresh ten-client idle measurement with visible worktree details.
- No numbered criterion or full goal is declared complete by this audit. The
  original issue-management, topology/provenance, runtime, benchmark and native
  release requirements remain intact.
Final verification: **1,045 full-suite tests passed in 3m20s**, with no failures or
skips. All sessions are terminal and whitespace checks pass. This verifies the
current regression suite, not the unimplemented or unmeasured acceptance clauses.

## Idle worktree fingerprint/cache reuse

The preceding goal turn identified a real criterion 6 gap despite passing tests.
Closed the specific repeated-patch-query behavior without weakening repeated-dirty
detection or treating silent SSE as sufficient evidence.

- Added bounded fingerprinting from porcelain v2 index/blob/mode facts, raw Git
  hashes of changed regular files, symlink text, Git attributes and effective
  configuration. It does not use mtimes as content identity. A dedicated eight-key
  patch cache is checked only after source verification and rechecked before return.
  Changed content/config/attributes invalidate patches; returning to a prior exact
  identity may reuse its existing immutable result.
- Fingerprint passes cap changed paths at 1,024 and regular-file input at 64 MiB.
  Existing patch/untracked limits remain. Untrackable external clean/process
  filter inputs and nested repositories/submodules are explicitly unavailable,
  never silently cached. Config inputs stay internal. Hash bytes/stage time and
  actual Git diff command counts are measured separately.
- Tests cover rename-source filenames resembling porcelain records, malformed
  status rejection, attribute-driven binary changes, safe prior-cache reuse,
  external-filter refusal and existing repeated text/binary/index/untracked edits.
- **168 dashboard tests passed in 1m10s** on the final implementation. The included
  real-Git idle test ran **63.921 seconds**, with **ten server subscriptions**,
  **11 shared probes**, **zero repeat diff queries** and **zero data messages**.
  It executed **242 Git source commands**, fingerprinted **660 bytes**, and spent
  **2.302 cumulative seconds in fingerprint stages** (including their Git CLI
  latency, not pure SHA CPU). Artifact: `/tmp/abacus-worktree-idle.json`.
- Scope is one tiny dirty Git worktree, not ten HTTP browsers plus real Beads or
  the 1,000-issue/10,000-event benchmark. Full criterion 6 combined measurements,
  hashing CPU/memory costs and criterion 12 remain open. The current acceptance
  audit and user docs now reflect the implemented cache instead of the old gap.
- All sessions are terminal; syntax/whitespace checks pass. The full objective
  remains active with all original mutation, topology, runtime and release gates.

## Combined standalone HTTP / Beads / visible-worktree idle evidence

The preceding goal turn fixed repeated patch regeneration and measured a narrow
server-subscription fixture. Extended verification to actual HTTP clients and real
Beads without claiming the earlier narrow test proved the whole acceptance gate.

- Added an explicitly read-only diagnostics endpoint with cumulative source/hash/
  projection/serialization counters. Metrics are not pushed as events and reading
  them triggers no source work or aggregate snapshot building. HTTP tests verify
  stable reads and absence of raw issue content. Canonicalization/hash time is
  measured separately from export subprocess time.
- Extended `noop-http.py` to ten logical clients with both main and worktree SSE
  (20 connections), warmed Git/Beads history, and a unique disposable untracked
  file. Warmup finishes before measurement; cleanup closes the host/readers and
  removes the file even on failure. Source freshness is checked before running.
- Real measurement passed: **60.02s**, **zero data events**, **zero issue projection
  rebuilds/serializations**, **zero aggregate snapshot builds**, **zero repeat
  history/diff queries**, unchanged **3,698-byte** snapshot. **280 source commands**,
  **10 exports / 26,520 bytes**, **4.0809s export CLI time**, **0.001715s export
  canonicalization/hash time**, **100,400 fingerprint bytes / 2.753734s fingerprint
  stage time**, **20 hash-object calls / 0.1042s CLI time**. Times are elapsed stages,
  not isolated CPU. Temporary fixture cleanup was verified after process exit.
- Recorded artifact: `docs/measurements/dashboard-http-idle.json`, including
  Darwin arm64, Git 2.55.0, Beads 1.2.2, .NET SDK 10.0.101 and source fingerprint.
  This is a small standalone fixture, not integrated-runtime or scale acceptance.
- **20 focused host/collection tests passed**, then **168 dashboard tests passed**
  on the current implementation. All process handles are terminal; whitespace and
  Python syntax checks pass. The acceptance matrix now records this actual evidence
  while retaining integrated-runtime, isolated CPU/memory and scale gaps.

### Real draft creation/publication contract

- Added `tests/browser/create-draft-cli.py` and recorded a passing real Beads
  1.2.2 run in `docs/measurements/dashboard-draft-cli.json`. Twelve checks cover
  exact literal fields/labels/target, unassigned year-9999 deferred creation,
  separately persisted blocked status, safe clearing of deferral, a rejected
  mixed epic/task edge, and explicit publication preserving dependency readiness.
- Each intermediate stage is reread through a new CLI process. This establishes
  persisted non-ready states, not concurrent-dispatch or process-kill acceptance.
  No production issue creation endpoint or draft/publish UI is claimed yet.
- Actual CLI evidence changed two assumptions: `bd dep add <epic> <task>` is
  rejected by this version, and closing a dependent before its open prerequisite
  is refused. The fixture now verifies rejection without an added edge and
  cleans up prerequisites first. The earlier leftover dependent was explicitly
  closed after its prerequisite; every final-run issue was verified closed.
- The next implementation must preserve the verified staging sequence while
  adding target/reasoning policy checks, request-ledger recovery of unknown
  creation outcomes, and explicit reviewed publication with ownership safeguards.
  The CLI script does not substitute for those HTTP/runtime requirements.

### Typed draft staging primitive

- Implemented internal `Beads.CreateDraftAsync`: bounded typed content, allowlisted
  issue types, priority, CSV-safe labels, exact reasoning labels and configured
  target/default policy are validated before any command. Only target metadata is
  authored; arbitrary bindings/metadata and assignment are not accepted.
- The helper creates with a year-9999 deferral, rereads content/ownership/target
  and checks absence from ready work, persists blocked status, verifies again,
  then clears only the staging deferral and verifies the final blocked draft.
  It never publishes, retries a command, rolls back, or requests remote sync.
- Unknown creation receipts return outcome-unknown without guessing an ID;
  interrupted later stages retain the known ID and return partially-applied for
  review. Malformed/ambiguous source responses and changed ownership/content stop
  the sequence. This does not provide cross-process compare-and-swap.
- **38 focused checks passed**, including an explicitly enabled real Beads 1.2.2
  test of the actual C# helper with labelled and unlabelled tasks. Both were
  independently verified closed after testing. Normal runs explicitly skip that
  mutation-bearing fixture test rather than silently reporting a pass.
- This is still an internal primitive, not an exposed creation feature. HTTP
  admission/session retry handling, policy refresh, the draft composer and
  explicit publication/ownership safeguards remain implementation work.

### Draft HTTP admission and retry integration

- Added a policy-backed creation context and typed draft-only POST endpoint.
  `expectedRevision` identifies the displayed target/reasoning policy because the
  issue does not exist yet. The source is reread and policy reloaded under the
  repository mutation lock before creation; stale source/changed policy reject.
- Extracted the existing ledger admission into one shared path, not a second
  creation-specific cache. Drafts and edits share request-ID collision checks,
  bounded retention, caller-disconnect behavior, serialization and shutdown drain.
  Known created IDs survive partial outcomes without exposing source metadata.
- Added tests for same-ID replay/payload changes/cross-action ID collisions,
  source/policy changes, unknown receipt caching, disconnect plus shutdown drain,
  and real HTTP schema/header/Origin checks. Existing mutations still pass.
- **54 focused unit/HTTP checks passed** (one explicitly skipped real fixture
  test), followed by **7 enabled draft tests passed**, including actual HTTP/Beads
  creation with audit actor verification, unchanged receipt replay and an export
  proving exactly one issue with the unique title. The two helper fixtures and
  HTTP fixture were verified closed after testing.
- The endpoint intentionally reports blocked-draft mode and publication unavailable.
  Browser creation and reviewed publication remain required; this is not completion
  of the action table or its ownership-sensitive acceptance criteria.
- Final combined dashboard/draft regression: **210 passed in 1m11s**, with the
  two opt-in real CLI tests explicitly skipped (both passed in the enabled run
  above). `git diff --check` passes; all test process handles are terminal.

### Browser draft composer

- Added capability-gated New issue and a labelled native modal form with actor
  attribution, typed creation fields, target/reasoning policy choices and an
  explicit blocked-draft/not-published explanation. Input stays out of URLs and
  survives closing/reopening, view changes and incoming snapshot updates.
- Pending/uncertain payloads are frozen. Policy rejection allows reviewed policy
  reload without discarding fields; lost responses retry the identical request.
  Known IDs link to the collected issue, while starting a different draft requires
  confirmation and empty fields. Historical/stale views disable submission.
- Native Escape and Close restore focus to New issue. The mobile dialog is
  bounded, scrollable and has no horizontal overflow at 390px; inspected the
  screenshot `/tmp/abacus-create-draft.png`. Completed/partial receipts never
  silently switch to a new create request or auto-publish.
- New draft CDP test and existing issue-editor CDP regression both passed; the
  former includes policy conflict, truncated response recovery, exact request
  identity, literal HTML-like text, modal/view retention, keyboard focus, playback
  gating and confirmed reset. **17 browser model tests** and **13 focused host/
  draft/action HTTP tests** passed. Browser tests are synthetic; prior real CLI/
  HTTP evidence remains separately scoped. Reviewed publication and ownership
  safeguards are still required, not replaced by blocked-only creation.

### Pool reservation fence for ownership-sensitive mutations

- Added internal `WorktreePool.GuardUnreservedIssueAsync`, requiring the existing
  controller lease. It holds the allocation/recovery semaphore and fences only
  new claims of the selected issue while inspecting every slot's durable journal
  and verified Git workspace. Existing unrelated workers may still claim work;
  execution-stop and cleanup transitions are not blocked by the claim fence.
- Refuses journaled reservations even when their worker is stopped/released,
  clean issue branches without journals, unknown execution ownership, malformed
  or missing pool coverage, and disagreement with live assignment/run identity.
  It never deletes a journal, resets a worktree, or clears a reservation. Failure,
  cancellation and idempotent disposal release only the temporary fence.
- The guard is **not yet wired to a browser mutation** and is not a permission
  grant by itself. Publication still needs fresh issue/policy/graph validation,
  explicit review, and separately established external-writer quiescence. A pool
  diagnostic snapshot or paused claim gate alone must not stand in for that proof.
- Added real-Git pool tests for existing-claimer exclusion, unrelated claims,
  blocked allocation, cancelled waiters, released/stopped reservations, clean
  branch reservations, missing/corrupt journals, lease requirements and live-run
  journal mismatch. No production publication capability is claimed here.
- Final pool/draft regression: **36 passed in 21 seconds**, with no build warnings;
  whitespace validation passes and all test handles are terminal.

### Reservation coverage hardening

- Auditing the new guard found that a valid-looking but incomplete manifest could
  omit a live assignment, durable journal, or clean reserved workspace. The guard
  now refuses each of these coverage gaps rather than concluding “unreserved.”
- Assignment reads now reject duplicate/case-ambiguous JSON fields and a directory
  at the journal path. Previously, a later duplicate `IssueId: null` could hide a
  reservation, and `File.Exists` alone treated a directory-shaped journal as absent.
  No ambiguous state is repaired or deleted automatically.
- Added real-worktree tests for unlisted active/released assignments, orphan clean
  workspaces without journals, duplicate-field journals and directory-shaped
  journals. All preserve the original reservation/state for operator review.
  This hardens the prerequisite fence; publication remains disabled pending the
  separate external-writer, graph/policy and reviewed-action implementation.
- Final verification: **41 pool/draft tests passed in 29 seconds**, no build
  warnings, and `git diff --check` passed. Test processes are terminal.

### Full regression after creation and reservation changes

- Ran the complete .NET suite on the current worktree, not only the dashboard
  filters: **1,105 passed, 2 explicitly skipped, 0 failed**, in **2m59s**.
  The skips are the opt-in disposable real-Beads draft helper and HTTP tests,
  both separately exercised successfully earlier; no silent real-CLI pass is
  inferred from this run. Build and whitespace checks reported no errors.
- This refreshes cross-component regression evidence after the shared mutation
  ledger and pool assignment/journal changes. It does not close publication,
  external-writer quiescence, the remaining action table, real-harness acceptance,
  integration provenance, scale benchmarks or native release gates.

### Publication dependency-cycle preflight contract

- Verified the installed Beads 1.2.2 read-only `graph check --json` and
  `dep cycles --json` outputs against the disposable fixture. Added a thin
  `Beads.CheckDependencyGraphAsync` helper returning nullable `CyclesClear`, not a
  generic publication approval. It requires a supported, internally consistent
  graph-check receipt and a second empty cycle result; disagreement is unknown.
- The [version-pinned upstream implementation](https://github.com/gastownhall/beads/blob/v1.2.2/cmd/bd/graph.go)
  checks cycles despite the broader integrity wording in its help. A clean result
  does not establish missing-edge coverage, readiness, prerequisite integration,
  or external-writer quiescence. Those remain separate publication requirements.
- Unknown schemas, duplicate/missing fields, nonzero exits with success-shaped
  output, inconsistent counts, unreadable sources and malformed second responses
  fail closed. Cancellation starts no further command; diagnostics are sanitized.
  No mutation, automatic repair or retry is performed by this helper.
- **60 graph/draft tests passed**, including enabled real-Beads graph validation,
  helper creation and HTTP creation/retry checks. This preflight is still an
  internal primitive; publication remains disabled until the full reviewed action
  and its independent ownership/integration safeguards are implemented.

### Read-only publication review endpoint

- Connected cycle preflight to an explicit `publication-review` read endpoint.
  It returns the issue/policy revisions, recorded direct edges and separate
  unknown/empty/missing-target coverage, without leaking raw issue metadata.
  Closed dependencies retain `integration: not-verified`; ownership is likewise
  unverified and publication remains unavailable, even when cycle checks pass.
- Rereads the complete source and policy after checks, rejecting changed or stale
  observations rather than combining mismatched evidence. One admitted review
  bounds concurrent CLI work; cancellation releases admission. Review never takes
  a mutation request ID, changes an issue or marks collectors dirty.
- Tests cover unknown versus empty edges, missing targets, closed-but-unverified
  dependencies, source races, cancellation/admission, malformed HTTP paths/query
  fields, missing issues and raw-metadata non-disclosure. Full publication still
  requires ownership/external-writer safety and prerequisite integration evidence;
  this read endpoint is not a substitute or an authorization grant.
- **19 focused publication-review, draft and host tests passed**; whitespace
  checks passed and all test process handles are terminal.

### Publication candidate content and lifecycle evidence

- Extracted creation's existing bounded content/label/target/reasoning validator
  for reuse by publication review. The review reads raw source fields rather than
  a display projection that could omit malformed labels or default unknown values.
- Added separate content-policy and draft-lifecycle findings, with sanitized
  reasons and the configured target. Non-blocked issues, assignments, remaining
  deferrals and bound/uncertain execution metadata require separate reconciliation;
  review does not rewrite any of these fields. A positive candidate finding is
  still not a claim of ownership, quiescence, graph completeness or integration.
- Added tests for malformed/missing content, strict/optional reasoning policy,
  conflicting/reserved labels, target defaults/enforcement, invalid metadata,
  assignments, deferrals and non-draft states. The real HTTP fixture now checks
  candidate evidence for its newly created draft while publication stays false.
- **63 focused tests passed**, including enabled real Beads helper/HTTP tests and
  verified fixture cleanup. Whitespace checks passed; test handles are terminal.

### Publication review evidence identity

- Added a deterministic `reviewRevision` covering selected issue identity, all
  observed issue revisions, target/reasoning policy and nullable cycle verdict.
  It cannot silently reuse the same evidence identity after a prerequisite change,
  issue removal, policy change, different selection or unavailable cycle check.
  Export ordering alone is deliberately ignored.
- Added explicit tests that policy changes during a review discard its result
  and release admission, and that an unchanged selected issue cannot mask changes
  elsewhere in the dependency source. This identity is not an authorization grant
  or a substitute for writer quiescence and fresh checks at mutation time.
- **27 focused review/candidate/draft tests passed**; whitespace checks passed
  and all test handles are terminal.


### Synthetic large-scene rendering baseline

- Added a deterministic 1,000-issue/10,000-comment fixture and CDP benchmark.
  Comments span the explicit displayed range; the benchmark verifies 240 event
  list entries on the 24-lane visible page rather than timing empty lanes.
- Recorded [JSON evidence](measurements/dashboard-scene-scale-before-label-culling.json) and
  [screenshot](measurements/dashboard-scene-scale-before-label-culling.png), with benchmark/fixture/
  timeline hashes, machine identity and browser version. Apple M4 Pro, 48 GiB,
  Chrome 153 with SwiftShader: **101.7ms warm activation, 37.6 FPS** over eight
  seconds, p95 observed frame interval 28.5ms. Camera motion caused no buffer
  uploads or API requests; settled rendering added zero frames over one second.
- Scope is one rendered client plus nine stream-only clients against a synthetic
  Node fixture, not production HTTP/Beads/Git acceptance or ten rendered clients.
  Warm activation starts from an already loaded issue table. CDP task seconds
  and final JS heap are recorded, not misrepresented as isolated CPU/peak memory.
- Visual inspection found overlapping lane cards in the fitted 24-lane scene;
  this is an explicit readability gap despite acceptable numeric frame rate.
  Full source costs, integrated-runtime behavior and visual/motion acceptance
  remain open. Reproduction and interpretation are in tests/browser/README.md.
- Benchmark exited successfully; six pure timeline model tests and whitespace
  checks passed. No production behavior changed in this measurement step.

### Dense timeline label culling

- Added deterministic screen-space collision culling for lane cards and time
  labels, with focused/selected cards preferred over ordinary labels. Cull only
  text overlays, never source lanes, markers, events or accessible list entries.
  Hidden cards cannot receive keyboard focus; visible focused cards win collisions.
- Keep hidden overlay dimensions measurable, batch size reads before placement
  writes, and recompute on camera/viewport changes without rebuilding GPU buffers.
  The scene explains where to find labels omitted at dense zoom levels.
- Added pure collision tests for stable ties, selection/focus priority, spacing,
  invalid geometry and input preservation. The scale browser check now asserts
  zero label overlaps and all 24 paged lanes in the accessible event list.
- Updated [scale evidence](measurements/dashboard-scene-scale.json) and
  [screenshot](measurements/dashboard-scene-scale.png): seven visible labels,
  zero overlaps, 240 accessible event entries, 47.1ms warm activation and 39.0 FPS
  software rendering. No motion API requests/buffer uploads or settled idle frames.
  These single-run timings are not a statistically established speed improvement.
- Seven pure model tests and the existing full timeline browser smoke passed,
  including playback, reduced motion, context loss and narrow layout. Both browser
  process handles completed successfully; whitespace checks passed. Original
  pre-culling evidence is retained separately. Full acceptance remains open.


### Timeline logical focus preservation

- Timeline overlay/list rebuilds restore the focused lane, issue or event by
  logical identity without scrolling or taking focus from unrelated controls.
  If that control disappears, focus falls back to the keyboard camera control.
- Added browser assertions for issue-list, event and lane-card selection rebuilds.
  The timeline browser regression passed after correcting a CDP expression
  variable-scope error; the rerun completed successfully. Broader source-removal
  and refresh focus coverage remains part of the accessibility acceptance audit.


### Issue-management freeze and revised completion scope

- User requested freezing issue-management expansion in its stable current state,
  then completing timeline/inspector work and a full visual/animation polish pass.
  See the revised completion scope in dashboard-acceptance.md; deferred actions
  are not represented as implemented.
- Completed the in-flight read-only reachable dependency coverage check. It
  distinguishes missing issues, unknown collections and bounded/truncated walks
  from complete recorded coverage, including transitive edges. It traverses all
  recorded relation kinds without claiming readiness, cycle freedom, integration
  or ownership. Publication remains unavailable and no mutation path was added.
- Thirteen focused dependency/publication-review tests passed, covering cycles,
  transitive missing/unknown evidence, limits, cancellation and existing HTTP
  review behavior. The test process completed successfully.
- Issue-management freeze regression: **90 passed, two opt-in real-CLI tests
  explicitly skipped, zero failures**. Whitespace checks passed. No test process
  remains running. Next work is timeline/inspector, then visual/motion polish.

### Inspector-to-timeline Git evidence

- Explicitly loaded validated issue Git evidence now annotates the matching live
  lane with recorded branch, changed-file count, additions/deletions, binary-file
  caveat and current containment. Native hover provenance supplies comparison
  basis, pinned tips and warnings; full facts remain in the Git inspector.
- Bounded 64-issue evidence cache retains only comparison/binding facts, never
  patch/history bodies. Issue revision mismatch, source/Git invalidation and
  failed refresh remove evidence; historical playback never shows current facts.
  Identical comparison loads do not rebuild labels. No extra source calls or
  source mutation is introduced, and no merge time/connector is fabricated.
- Eight pure timeline/model tests passed. Git-inspector browser checks passed
  with new live-card, playback exclusion and source-change invalidation assertions.
  Both browser runs completed successfully; whitespace checks passed. Timeline
  integration topology and the visual/animation pass remain unfinished.


### Inspector activity navigation

- Recorded snapshot rows and dated comments now offer Show on timeline. Explicit
  navigation clears view filters, reveals the correct lane page, expands the time
  range when necessary, seeks to the recorded timestamp, pins the event and focuses
  its lane/camera. It reuses loaded source events rather than inventing transitions
  or fetching on camera movement. Undated comments do not receive a fake timestamp.
- Playback keeps mutations unavailable. Missing/unloaded/future events are refused
  rather than substituted with current state. The control explains filter clearing.
- Expanded timeline browser regression passed for both snapshot and comment links,
  playback mutation gating, camera keyboard focus and return to live. Existing
  geometry reuse, idle, reduced-motion, context-loss and mobile assertions passed.
  Browser process completed successfully. Integration topology and visual polish
  remain pending; issue-management capabilities were not changed.


### Recorded Git commits in the timeline

- Explicitly loaded issue Git history now produces dated, selectable Git events,
  with a dedicated event filter and inspector-to-timeline navigation. Commit
  author, object ID and parent evidence are shown alongside explicit current-
  reachability/shared-ancestor limitations; no issue status or merge time is inferred.
- The event cache is bounded to 16 issues/100 commits each, separate from current
  comparison summaries. Missing dates and duplicate IDs do not manufacture events.
  Tip/source/revision invalidation drops obsolete history; unchanged loaded data
  retains projection identity and does not repeatedly rebuild geometry.
- Nine pure timeline tests and the Git-inspector browser regression passed,
  including literal commit text, playback navigation, unknown historical Beads
  status and source invalidation. Browser/test processes are terminal and
  whitespace checks passed. Target topology/integration connectors still require
  source-backed implementation; the visual/animation pass has not started.

### Current target-containment connectors

- Reopened the user's original reference image before introducing curved target
  connections. Its silver trunk, luminous curves, speech bubbles and dense
  full-height scene remain the visual target; the full polish pass is not done.
- Validated live bindings now group into labeled neutral target rails. Only
  positive current containment adds a smooth dashed connection near NOW, with a
  selectable explanation and pinned tip IDs. Closed status alone adds no link;
  unknown integration time is not replaced by closed_at or a fabricated merge.
  These current-observation rails/connections are absent from playback.
- Added pure tests for evidence/revision gating, closure versus containment,
  playback exclusion and curve endpoints. Browser fixture explicitly changes
  containment, verifies zero/one connections and playback removal, and captures
  `/tmp/abacus-current-topology.png`. Visual inspection confirms the connection;
  composition, glow and callout styling still differ substantially from reference.
- Ten model tests and the Git browser regression passed. A screenshot-run failure
  came from switching views before existing inspector assertions; the terminal
  rerun starts in Timeline and passes. All handles are terminal; whitespace passes.


### Recorded execution start curves

- Added a curved start connection only when validated binding evidence and the
  bounded loaded history agree on the issue tip and contain the exact dated start
  commit. Its selectable marker distinguishes commit time from branch creation
  or claim time. Unknown/missing/out-of-range commits leave lanes independent.
- Current binding topology remains excluded from playback, rather than claiming
  the current binding existed at every historical timestamp. Unit tests cover
  exact commit/tip/revision evidence, missing dates and playback rejection.
- Expanded the browser fixture with a recorded base commit; the Git regression
  verifies both start and containment connections plus selectable commit history.
  Eleven pure model tests and the browser check passed; process is terminal and
  whitespace checks passed. Updated screenshot visually inspected. Full visual
  composition, callouts/glow, animations and remaining inspector review are next.

### Stable Git inspector navigation

- Fixed view changes and same-issue event selection clearing explicitly loaded
  Git evidence. Inspector evidence identity now follows issue/revision and live
  versus historical mode, independently of ordinary inspector rerenders.
- Explicit snapshot, source/Git changes, disconnect and playback invalidation
  remain intact; navigation does not turn cached evidence into permanent truth.
- Browser regression asserts loaded history/provenance survives Issues-to-Timeline
  navigation with no extra API requests, while source changes and playback still
  clear it. Final browser rerun passed; process is terminal and whitespace checks
  passed. Issue-management behavior remains frozen.


### Reference-led visual composition, first pass

- Reopened the supplied reference immediately before this visual pass. Reduced
  header/provenance chrome, enlarged the scene, refined navigation/inspector tabs,
  added gradient lane cards and speech-bubble callout styling. Source/search
  coverage remains available in a disclosure that opens automatically on staleness;
  the unauthenticated-network warning stays visible.
- Added shared hover/focus timing tokens and disabled those transitions for both
  system reduced-motion and the explicit reduced override. Responsive layout
  retains stacked mobile content rather than shrinking controls out of reach.
- Timeline browser regression passed including explicit reduced CSS, playback,
  keyboard focus, idle rendering, context loss and 390px layout. The scene is
  explicitly fitted before its reference screenshot. First screenshot inspected;
  luminous tubes, scene composition and broader animation/inspector polish remain
  unfinished. No issue-management expansion was made.


### Luminous timeline geometry

- Added bright tube cores and two restrained additive halo shells, using a
  depth-tested, non-depth-writing glow pass. Geometry stays buffered during camera
  motion and settled rendering still stops. The 2D fallback uses modest shadows.
- Simplify only collinear forward segments before tube construction, preserving
  curve bends/reversals and the existing dashed-path segmentation. This offsets
  glow geometry cost without removing source events or changing hit targets.
- Twelve model tests and the timeline browser regression passed. Inspected the
  fitted screenshot against the reference: lines now have luminous cores/halos;
  the independent-lane fixture intentionally does not invent source forks.
- [Updated synthetic scale measurement](measurements/dashboard-scene-glow.json):
  125ms warm activation, 60.0 FPS, p95 observed interval 17.6ms, zero camera API
  requests/buffer uploads and zero settled frames. Same one-renderer/nine-stream
  scope, not a production-server benchmark or isolated CPU/peak-memory proof.
  [Visual evidence](measurements/dashboard-timeline-glow.png) retained. Processes
  exited successfully. Further reference-scene and animation polish remains.


### Interruptible callout motion

- Deliberate new event selections use a 240ms ease-out fade and seven-pixel slide.
  Same-event selection and unchanged rendering do not replay the effect. Replacing
  or dismissing the event cancels pending motion immediately; dismissal restores
  keyboard focus to the camera. Animations cancel on hidden-tab/view changes and
  either system or explicit reduced-motion activation.
- Browser checks verify one active effect, completion, no same-selection replay,
  immediate reduced-motion cancellation and dismissal focus, alongside existing
  playback/idle/context-loss/mobile regression. An initial fixed-sleep assertion
  was replaced with bounded observed-completion waiting; the terminal rerun passed.
- Full visual/animation acceptance remains pending. This change adds no perpetual
  rendering, source writes or source queries.


### Compact overview and continuous trunk composition

- Finished the interrupted overview check: compact semantic fact rows, readable
  description/notes, status/label chips and priority names passed the inspector
  browser regression, including mobile width and existing keyboard/draft behavior.
- In response to the user's explicit composition question, moved the silver target
  rail into the scene and replaced disconnected overlaid connectors with continuous
  issue curves. Recorded start evidence leaves the trunk; verified current
  containment returns near NOW. Markers follow the same curve geometry. Unknown
  starts/unverified integration remain independent, never inferred from closure.
- The return still represents current containment, not an exact historical merge
  timestamp. Existing current-topology playback restrictions remain intact.
- Thirteen model tests and the Git browser regression passed; the curve screenshot
  was inspected and now visibly branches from/returns to the central rail. Card
  placement still obscures part of the fork and needs refinement. Processes are
  terminal; whitespace checks pass. The reference-matching polish is not complete.


### Three-lane reference composition and card placement

- Lane cards now anchor to independent lane heights left of the geometry rather
  than covering their fork on the trunk. Fit framing leaves more left-side space;
  compact card spacing retains readable content without hiding the middle lane.
- Added a reproducible reference-composition browser fixture: three explicit
  starts, two ongoing branches, exactly one verified current return, recorded
  snapshots/commits, pinned comment and UTC reference labels. This is synthetic
  visual evidence, not fabricated production Git evidence.
- The initial check exposed middle-card collision; kept the three-visible-card
  assertion and corrected spacing. The final browser run passed. Screenshot
  inspected and retained at measurements/dashboard-reference-scene.png; all three
  cards and the main branching/returning trunk are now visible. No perpetual
  animation or mutation behavior was added. Broader regressions/polish remain.

### Broad visual regression checkpoint

- All 24 pure browser-model tests passed. Eleven standalone browser scripts passed
  with fresh fixtures/profiles: timeline, inspector, issue Git, issue editing,
  draft composer, issue table, relationships, search, worktree, reference scene
  and scale. The separate runtime browser fixture also passed, including force
  confirmation, retry, stop, SSE/HTTP races and disconnected controls.
- Latest synthetic scale record is measurements/dashboard-scene-visual-regression.json:
  43.5ms warm activation, 60.1 FPS, zero visible-label overlaps, camera requests,
  camera buffer uploads or settled idle frames. It retains the explicitly scoped
  one-renderer/nine-stream-only setup, not real-server performance acceptance.
- Browser processes completed successfully. Reference screenshot refreshed after
  the matrix. The full .NET suite is checked separately below; no feature changes
  were made while this regression run was active.
- Full .NET regression remains running in exec session 5356 at this checkpoint;
  last authoritative poll confirms it is live, not failed or stopped. Log:
  /tmp/abacus-full-regression.log. Three real-CLI opt-in tests are explicitly
  skipped so far. Resume this handle before considering another full-suite run.


### Full regression result and selected-branch captions

- Resumed the existing full-suite handle rather than starting another run:
  **1,145 .NET tests passed, three opt-in CLI tests skipped, zero failures** in
  3m6s. Session 5356 is terminal. This supersedes the preceding pending note.
- Added at most six selected-branch note/comment/status captions, sharing the
  existing collision and playback culling. Captions use recorded times/content,
  keyboard-accessible event selection and full-text tooltip/ARIA descriptions;
  they do not add source queries or infer new events. The reference-scene check
  passes with readable captions and all three lane cards; screenshot inspected.
- Post-caption timeline browser regression also passed, including motion,
  focus, playback, idle, context loss and mobile layout. Its process is terminal.
  The full-suite result predates only these front-end caption changes, which were
  separately browser-verified; no new C# behavior was introduced afterward.


### Scene-first control layout

- Moved secondary issue filters/source information below the timeline in a native
  keyboard-accessible disclosure. Primary camera/event controls now sit directly
  below the header, giving the curves more screen area like the reference.
  Issues/Branches keep their normal controls visible. Stale source evidence opens
  the disclosure rather than hiding an important warning; user input is retained.
- Reference screenshot inspected: the main trunk, three readable issue cards,
  branch curves, return, event captions and speech bubble remain visible. Added
  explicit Space-key disclosure opening and status-control visibility assertions.
- Final affected browser matrix passed: reference composition/disclosure keyboard
  access, timeline, inspector and issue table. The initial Enter injection did
  not activate the native disclosure; Space activation with observed-state waiting
  passed without adding custom keyboard handlers. Processes are terminal.

### Recorded-event URL restoration — 2026-09-22

Selected recorded events now write an opaque `event` ID alongside the selected
issue. Reloading restores the inspector and pinned callout only when that exact
event is present in the loaded projection and at or before the playhead. Missing
events get an explicit unloaded/unavailable-history notice, without implicit CLI
queries or substitution of current observations. Explicitly loading the matching
activity/Git history can resolve the pending selection. Current observations and
view-dependent clusters are intentionally not serialized as recorded event links.
Dismissal clears the event parameter; scrubbing/returning to live also clears the
stale callout. No draft content enters URLs.

Verification: `event-link-cdp.mjs` passed against a fresh synthetic fixture in
headless Chrome; all 24 pure browser-model tests passed. This is bounded evidence
for reload-based selection restoration, not completion of all navigation or
source-provenance acceptance criteria.

The existing full timeline CDP regression also passed after the event-link change:
WebGL lanes, lazy snapshots, orbit buffer reuse, settled idle, 2D, playback safety,
reduced motion, context-loss fallback and narrow layout. Both browser processes
exited successfully; no visual styling was changed in this pass.

### Browser history and built-host verification — 2026-09-22

Initial loading and same-document Back/Forward now share a pure timeline URL
parser. Traversal restores the range, playhead and pending event, stops playback
and obsolete camera easing without refitting, and clears the previous callout.
Historical-to-live traversal uses the existing current-issue reread gate before
editing is enabled. Live range serialization omits `to` as well as `at`, preventing
a live link from reopening in fixed playback. Explicit malformed playback URLs
remain non-live rather than silently enabling edits.

Evidence from current sources:
- 25 pure browser-model tests passed, including bounded/invalid URL interpretation.
- Extended `event-link-cdp.mjs` passed real `history.back()`/`history.forward()`
  traversal, restored inputs, no historical source requests, exactly one live
  issue reread, selection clearing, and live-link reload.
- Full `timeline-cdp.mjs` regression passed again (WebGL, camera, idle, playback,
  reduced motion, fallback, narrow layout).
- `dotnet build src/Abacus/Abacus.csproj --no-restore --nologo -m:1
  -p:UseSharedCompilation=false` succeeded with zero warnings/errors.
- Rebuilt host launched against `/tmp/abacus-web-contract` on an ephemeral local
  port. Six served embedded assets (HTML, CSS, dashboard/timeline/model/GL scripts)
  matched source bytes exactly; `/api/v1/project` returned JSON. Read-only requests
  only; host exited 130 after SIGINT without needing a forced kill. Log:
  `/tmp/abacus-built-assets-smoke.log`. This is a packaging/HTTP smoke check, not
  full real-source browser or release-platform acceptance.

### Local camera preferences and projection links — 2026-09-22

Camera pose and pan/orbit control now persist locally. Saved poses are validated
for finite values, supported projection and bounded angles/distance/targets;
malformed or mismatched data falls back to the normal initial fit. Storage writes
are debounced, do not fetch data, and do not schedule additional animation frames.
The `camera=2d|3d` URL parameter overrides the local projection preference, with
WebGL failure still forcing the accessible 2D fallback. Explicit projection
buttons update this parameter; history navigation restores an explicit mode
without adding an animated camera transition.

26 pure model tests passed. `camera-preferences-cdp.mjs` passed against a fresh
synthetic fixture, verifying changed pose/control restoration across reload, URL
precedence, invalid storage recovery and zero requests for mode switching. The
first test attempt exposed property-order comparison and pre-frame observation
in the test; it was corrected to await the changed pose and compare numeric
objects, then rerun successfully. No visual styling changed in this pass.

The existing full timeline CDP regression also passed after camera persistence,
including settled-idle rendering, reduced motion and context-loss fallback.

### Reference-led resizable inspector — 2026-09-22

Reopened the original reference and compared the current rendered scene before
this visual/layout change. Added the required resizable right inspector with a
quiet blue divider and central grip. The default remains approximately a quarter
of the desktop width. Pointer dragging, Left/Right, Home/End and double-click
reset are supported; focus and separator value semantics are exposed to assistive
technology. Local width storage is best-effort and bounded against viewport size.
The divider disappears in the stacked mobile layout. Resizing neither queries
sources nor refits the camera. The separate module is embedded and explicitly
served by the production host and both browser fixtures.

Verification: inspector-resize CDP passed pointer/keyboard interactions, persisted
width, lower bound, no source requests and no 390px horizontal overflow. Reference
CDP passed three recorded starts, one current-evidence return, visible issue cards,
loaded snapshots and pinned comment. Viewed the new screenshot to confirm the
subtle divider preserves the reference-oriented composition. All 26 model tests
passed; application build passed with zero warnings/errors.

All seven DashboardHostTests passed, including the new module in the actual
embedded-asset HTTP test. Updated reference screenshot:
`docs/measurements/dashboard-reference-scene.png`.

### Author-first speech bubbles and compact selected-event evidence — 2026-09-22

Reopened the reference before editing. Speech bubbles now lead with recorded
author names and local initial badges, with date/issue/event metadata beneath,
closer to the reference's comment hierarchy. No portraits, remote avatar requests,
agent identity inference or fabricated resolution labels were added. Unknown
authors get an explicit unknown label. Full event text and provenance remain in
a keyboard-accessible native disclosure in the inspector; it starts compact so
the description and issue facts remain visible rather than duplicating the
large speech bubble above them. Same-event refreshes preserve expansion state.

All 27 pure browser-model tests passed, including Unicode and unknown-author
initials. Reference browser assertions now check the author badge/name and
opening the event evidence with native Space-key activation.

The extended reference browser check passed. Inspected and saved the updated
screenshot: the author badge is readable and compact event disclosure brings the
description, status, labels and attention facts back into view. The sequential
launcher then hit a DevTools connection refusal before the timeline test began;
the timeline regression was restarted on a fresh port rather than treating that
launcher failure as application evidence.

The fresh-port full timeline regression passed, including callout interruption,
reduced motion, settled idle, WebGL fallback and narrow layout.

### Current dense-scene motion verification — 2026-09-22

Re-ran the 1,000-issue/10,000-comment scene after layout, camera persistence and
callout changes. During review, found that the original benchmark repeatedly
pressed Right until yaw clamped, then kept measuring draws of a stationary pose.
The benchmark now reverses direction before the bounds and asserts changed
camera poses on at least 80% of rendered frames. Its existing 2-second warm
viewport and 30 FPS goals are now assertions, not merely report booleans. Source
hashes now include HTML/CSS, dashboard and inspector-resize code as well as the
renderer/model/harness.

The strengthened run passed: 1,253.5ms warm activation, 400 changed camera poses
in 400 rendered frames over 8.0046s (49.97 FPS), p95 observed frame interval
33.1ms, zero motion source requests and buffer uploads, and zero settled frames
in the one-second idle observation. Eleven visible labels had zero overlaps;
24 accessible lanes and 240 event controls were retained. Report and screenshot:
`measurements/dashboard-scene-motion-current.json` and `.png`.

This remains one rendered headless Chrome/SwiftShader client plus nine stream-only
clients against synthetic data, not ten rendered clients, native-GPU testing,
real CLI cost, peak-memory or full production acceptance. Earlier single-direction
FPS reports do not establish continuous-motion performance; use this report for
that claim. No application visual styling changed in this verification pass.

### Pinned-event source reconciliation — 2026-09-22

Pinned callouts previously retained an event object independently of projection
refreshes. They now reconcile against the loaded issue/event projection and scene
evidence: changed text/provenance refreshes both views without entrance replay;
removed evidence clears selection and its URL parameter. Selecting an issue
without an event also clears the prior bubble. Current observation timestamps
alone do not count as content changes. Inspector event disclosures preserve
expanded state and logical summary focus on same-event refreshes.

The new synthetic source-refresh browser check verifies changed comments,
no entrance replay, disclosure state/focus and removed selections. All 27 pure
model tests passed. These checks are not a substitute for the remaining complete
source/provenance matrix. No visual styling changed in this pass.

After adding issue-selection clearing, the extended event-refresh browser check
and full timeline CDP regression both passed on fresh fixtures/profiles.

### Broad post-polish regression — 2026-09-22

Current-source full .NET suite passed: 1,145 passed, zero failures, three explicit
opt-in real-Beads CLI skips, duration 3m29s. Command: `dotnet test
 tests/Abacus.Tests/Abacus.Tests.csproj --no-restore --nologo -m:1
 -p:UseSharedCompilation=false`. Terminal log:
`/tmp/abacus-current-full-regression.log`. The skips are the installed graph
contract and two actual draft-creation CLI tests; this run does not verify them.

Six additional fresh-fixture/profile browser regressions passed: issue-git,
inspector, event-link, issue-edit, create-draft and issue-table. These cover Git
history/provenance and literal text, source invalidation, historical gating,
keyboard tabs, draft retention, Back/Forward, live rereads, label conflict/retry,
draft policy/drop-response recovery, pagination and no-query view operations.
No frozen issue-management feature was expanded. Updated the user guide's stale
opening capability summary and documented camera, event-link, disclosure and
inspector resize interactions. Completion remains unproven against the open
source-provenance and complete visual/motion acceptance matrix.

### Exact event timestamps — 2026-09-22

Audited the spec's local-time/UTC-tooltip requirement. Timeline marker tooltips,
axis labels and accessible event controls now expose exact ISO UTC timestamps;
event controls also identify their source/revision when recorded. Speech-bubble
local timestamps use semantic `time` elements with UTC `datetime` and title.
Expanded inspector evidence exposes UTC text directly, distinguishing current
observation time from recorded event time. No merge/claim dates are inferred.

Extended event-link CDP passed exact UTC assertions for both callout and inspector
as well as event-control tooltips and its existing reload/Back/Forward/live-reread
checks. All 27 model tests and JS syntax/whitespace checks passed. This change
follows the broad full regression; the full .NET suite was not rerun for these
front-end-only timestamp additions.

### Camera interruption correctness — 2026-09-22

The motion audit found two gaps: enabling reduced motion mid-transition froze
the camera at an intermediate projection, while hiding the timeline retained a
transition that could resume later. Reduced-motion changes and hidden document/
view handlers now settle at the already-requested destination without animation.
Ordinary pointer/wheel/key interruption still takes priority over easing. No
source reads or additional animation loops were added.

The new CDP test observes an actual intermediate projection before interrupting
it, checks explicit and emulated system reduced-motion endpoints, then tests view
hiding/return. Its first idle assertion counted a legitimate ResizeObserver
redraw; the test now waits through the view-layout frames before measuring idle.
All 27 pure model tests passed. No styling or reference geometry changed.

The corrected camera-interruption browser regression passed with no JavaScript
errors: exact 2D/3D endpoints, no replay after returning, and no source requests.

### Playback interruption and transport consistency — 2026-09-22

Follow-up inspection found that hiding the timeline stopped playback without
updating its Pause button, while a hidden browser document retained the playing
flag. Both paths now use the same pause operation, persisting the actual playhead
and updating transport state. Explicit Pause also saves the stopped position.
Returning to the timeline does not silently restart replay.

Extended motion-interrupt CDP passed: start replay, switch to Issues, verify Play
and saved `at`, return to Timeline, and verify no playhead movement or URL drift.
Its reduced-motion/camera interruption assertions also passed. All 27 model tests
passed. Browser-document hiding uses the shared helper but this run exercises
view switching, not an actual OS-backgrounded browser tab.

### Playback-filter and endpoint correctness — 2026-09-22

Playback previously refreshed labels against the old visible-lane set, so status
and historical metadata/search filters could miss issues entering/leaving their
criteria. It now reevaluates the shared filter predicate at the bounded transport
update cadence and rebuilds geometry only when visible membership or filtered
count changes. A final transport/inspector update is forced at the endpoint,
regardless of the normal cadence, and saves the exact final playhead to the URL.
This prevents a finished replay retaining Pause or a stale scrubber/inspector.

New playback-filter CDP passed a recorded in-progress-to-blocked transition,
filtered lane/count updates, final Play state, scrubber=1000, exact final UTC `at`,
blocked inspector state and zero playback source requests. All 27 model tests
passed. No styling, source inference or mutation capability changed.

### Combined playback/reference regression and motion gap review — 2026-09-22

All four fresh-profile browser checks passed together on current source:
playback-filter, motion-interrupt, reference and timeline. All 27 model tests
passed. Reviewed the spec's motion table against actual CSS and animation calls;
added an explicit implemented/unimplemented checkpoint to dashboard-acceptance.md.
Remaining finite panel/tab, scene-event/status, connector-arrival and filter
transitions are not claimed delivered merely because camera/callout tests pass.

### Inspector panel and tab motion — 2026-09-22

Reopened the supplied reference before this pass. Added a 240ms shared-token
opacity/4px panel entrance and 180ms compositor-transform underline transition.
ARIA selection, visibility, focus and URL changes remain immediate; the outgoing
panel is hidden at once and its animation cancelled. Repeated selection does not
replay. Explicit/system reduced motion cancels effects, as do hidden documents
and switching to the Branches inspector. Existing fields are neither cloned nor
replaced, preserving drafts. Motion performs no source reads.

Focused inspector-motion CDP passed rapid switching, finite completion, identical
selection, reduced-motion cancellation, draft preservation and zero source
requests. All 27 model tests passed.

Existing inspector and reference-scene CDP regressions also passed. Visually
inspected and saved the updated reference screenshot; tab indicator and settled
panel preserve the reference-oriented composition.

### Inspector motion preference matrix — 2026-09-22

Extended the inspector-motion browser check and ran it successfully against a
fresh fixture/profile. While a panel effect is active, emulated system reduced
motion now demonstrably cancels it; subsequent tab changes remain immediate.
Explicit Full overrides the system preference and permits the finite effect.
Switching to Branches cancels an active inspector entrance, returning does not
replay it, and the unsent edit draft remains intact. Preference/tab operations
make no source requests (Branches' own data fetch is intentionally outside that
assertion). No application code changed in this verification pass. Actual
background-document cancellation is not claimed from this view-switch test.

### Actual background-tab verification — 2026-09-22

Added and successfully ran `background-tab-cdp.mjs` using two fresh headless Chrome
tabs. Activating the blank tab produced an observed real `document.hidden=true`
on the dashboard; no property override or synthetic visibility event was used.
The test verified that an active inspector entrance was cancelled, playback
stopped with a saved playhead, and rendered-frame count stayed unchanged over
500ms hidden. Activating the dashboard again restored exact 2D projection without
replaying the panel effect or restarting playback; the saved playhead stayed
unchanged. The temporary tab and browser closed successfully.

This closes the previously missing direct document-visibility check for these
paths; it is not evidence for OS process suspension or all browsers. No application
code changed in this pass. Syntax and whitespace checks passed.

### Finite live-event bead arrivals — 2026-09-22

Reopened the original reference before implementing this animation. Newly
observed dated events on already loaded issue lanes now receive one 350ms opacity
entrance at their authoritative positions. Only live change-stream upserts can
queue arrivals; initial/reconnect snapshots and explicit history loads cannot.
Queues are capped at 64; hidden/reduced/playback views suppress arrivals, and
entering those modes settles existing effects. Clustered/dense markers may omit
individual entrances rather than animating the backlog. No invented source dates.

WebGL records animated marker draw ranges during the normal scene upload and
changes only an opacity uniform during frames; the 2D fallback uses matching
alpha. Picking positions remain unchanged. Rendering stops after the finite
effects settle. Existing events do not reanimate on repeated updates.

The event-arrival CDP check passed new comment entrance, unchanged buffer-upload
and API counts during the effect, idle settling, no identical-event/snapshot
replay and reduced-motion suppression. All 27 model tests passed. Status-color
and verified-connector transitions remain separate unfinished requirements.

The existing full timeline browser regression also passed after the renderer
change, including camera buffer reuse, settled idle, reduced motion, 2D fallback,
playback safety and narrow layout.

### Arrival cleanup, interruption and fallback — 2026-09-22

Finished arrival draw ranges now retire without a geometry upload, restoring the
single batched solid draw rather than retaining per-marker draw calls until the
next source change. Cancelling arrivals also clears their draw ranges immediately.
Extended event-arrival CDP passed zero retained ranges at completion, interruption
of an active fade by reduced motion, and another finite arrival in the canvas
fallback after actual WebGL context loss. Existing no-query/no-upload, idle and
no-replay assertions still passed. All 27 model tests passed. No visual styling
or timing changed in this cleanup pass.

### Automatic reconnect arrival baseline — 2026-09-22

The arrival audit found that explicit resync already refreshed a snapshot, but
browser-managed EventSource reopen could resume missed events as fresh arrivals.
Disconnect now cancels existing arrivals. A reopened stream is closed and
reconnected through the existing snapshot/cursor path before accepting new live
changes, establishing a non-animated baseline and revalidating inspector evidence.
This adds a snapshot read on actual reconnect, not during idle or animation.

New reconnect-arrival CDP passed a real closed SSE connection, changed fixture
state while disconnected, observed reconnect/snapshot with zero arrival effects,
and a subsequent genuinely live comment entrance. All 27 model tests passed.
No application styling or animation timings changed in this correction.

### Post-arrival-renderer dense-scene regression — 2026-09-22

Current renderer passed the strengthened continuous-motion scale harness after
adding per-marker arrival fades and retiring completed draw ranges. Dataset:
1,000 issues/10,000 recorded comments, one rendered Chrome/SwiftShader client
plus nine stream-only clients. Warm activation: 1,037.7ms. Continuous orbit:
427 changed poses/427 frames over 8.0122s, 53.29 FPS, p95 observed interval
29.2ms. Camera motion caused zero source requests and zero buffer uploads;
settled idle produced zero frames over one second. Eleven visible labels had
zero overlaps; 24 accessible lanes and 240 event controls remained available.
Both enforced timing gates passed, with no JavaScript errors.

Source hashes, machine details and scope limitations are captured in
`measurements/dashboard-scene-arrival-renderer.json`; matching fitted screenshot
is `.png`. This measures camera motion on a loaded dense scene, not a live event
burst, ten rendered clients or real Beads/Git source cost. It does not close the
full performance acceptance criterion.

### Visible-only arrival bursts — 2026-09-22

Arrival scheduling now skips lanes outside the current visible page and stops
scanning once the 64-effect cap is reached, rather than reserving animation work
for offscreen issues. Authoritative issue/event projections still receive every
update; this only limits decorative presentation.

New arrival-burst CDP passed a 1,000-issue/10,000-comment fixture receiving 1,000
additional live comments: all 24 visible new events remained accessible, observed
effects never exceeded 24, rendering settled to idle, and paging to previously
offscreen lanes did not replay entrances. All 27 model tests passed. This is
burst correctness/boundedness evidence, not a measured burst-FPS guarantee.

### Source-health arrival suppression — 2026-09-22

Stale source health now cancels queued/running arrivals. Neither a stale change
nor the first successful change after stale state queues decorative entrances;
that recovery establishes current evidence rather than presenting missed history
as newly happening work. Subsequent healthy changes retain normal behavior.

Extended event-arrival CDP passed stale/recovery suppression along with existing
live entrance, idle, cancellation, no-replay and fallback checks. All 27 model
tests passed. This correction does not alter source data or mutation capability.

### Current-state marker color transitions — 2026-09-22

Reopened the reference before this visual change. Healthy live updates to visible
issues now blend current-state marker color over 350ms; status text and shape
remain immediately authoritative. The animation is a current observation, not a
new dated status event. Existing live-arrival gates suppress history/reconnect,
stale/recovery, hidden and reduced-motion effects. Repeated identical statuses
do not restart; rapid changes use the currently interpolated marker color as the
next start. WebGL changes color uniforms over existing draw ranges; fallback
uses equivalent color interpolation. Completed ranges return to batching.

Focused status-motion CDP passed immediate text, finite active transition, no
per-frame uploads/queries, cleanup, identical updates and reduced motion. The
first attempt did not observe an active effect; an instrumented rerun passed,
and the regression now waits for initial scene rendering before injecting a
status update. All 27 model tests passed. This covers current markers, not
historical status interpolation or connector arrival.

Fresh-profile reruns of both status-motion and the full timeline CDP regression
passed after removing diagnostic instrumentation.

### Status-motion interruption and fallback verification — 2026-09-22

Extended status-motion CDP passed rapid blocked→open changes while the first
blend was active, a bounded single affected marker, reduced-motion cancellation
and draw-range cleanup, then a fresh blocked transition in the 2D canvas fallback
after actual WebGL context loss. Current status text stayed authoritative. Added
a pure renderer test for interpolating from an existing visual color and cancelling
to the final source color; all 28 model tests passed. No application behavior or
styling changed in this verification pass.

### Post-status-renderer performance and reference verification — 2026-09-22

The current shader passed the strengthened dense-scene test: 1,000 issues/10,000
comments; 1,059.7ms warm activation; 383 changed camera poses/383 frames over
8.0063s (47.84 FPS); p95 observed interval 34.4ms; zero camera source requests or
buffer uploads and zero settled frames. Eleven visible labels had zero overlaps.
Report/screenshot: `measurements/dashboard-scene-status-renderer.json` / `.png`.
This remains one rendered SwiftShader client plus nine stream-only clients, not
full real-source or multi-rendered-client acceptance.

Reference CDP passed and its screenshot was visually inspected and refreshed.
`dotnet build src/Abacus/Abacus.csproj --no-restore --nologo -m:1
-p:UseSharedCompilation=false` passed with zero warnings/errors, embedding the
latest frontend. No application edits were made in this verification pass.

### Persistent no-glow accessibility setting — 2026-09-22

Reopened the reference before this optional visual setting. Glow remains enabled
by default; the toolbar toggle disables WebGL's additive halo pass, canvas path
shadows and DOM text/box shadows, retaining core lines, colors, borders and focus
outlines. It stores a local preference and redraws without rebuilding geometry
or fetching source data. Disabling glow also excludes box-shadow transitions so
the existing glow disappears immediately rather than fading slowly afterward.

The first browser assertion caught that CSS transition override; after correction
the glow-preference CDP passed immediate shadow removal, ARIA toggle state, reload
persistence, WebGL-loss fallback and no uploads/API requests. All 28 model tests
passed before the CSS-only correction.

### Glow-control layout regression — 2026-09-22

Fresh-profile full timeline and reference-scene CDP checks passed after adding
Glow, including the narrow-screen fallback path, camera/idle behavior, playback
safety, visible cards and recorded/current-evidence topology. All 28 model tests
passed. Viewed and refreshed the reference screenshot: the new control fits the
desktop toolbar without obscuring motion or camera controls. This supplements,
but does not replace, the dedicated no-glow preference test.

### Minimap event selection — 2026-09-22

Minimap event hits now share issue/event selection with the inspector and seek
the exact recorded timestamp. Event-kind filtering applies before clustering
and picking; empty positions retain local scrubbing. The accessible event list
remains the keyboard alternative. No source queries are added.

Fresh Chrome CDP passed real pointer event selection, the exact event URL/time,
playback write suppression, filtered-out event rejection and blank-space scrub.
The scrub assertion allows pointer pixel precision; an earlier assertion used
a stale layout coordinate and then required subpixel-exact time, both corrected
in the test. All 28 pure browser tests passed and diff whitespace checks passed.

### Continuous curve joins and rounded bead lighting — 2026-09-22

Reopened the original reference and compared the current screenshot before
editing. Tube segments now share tangent-oriented endpoint rings, eliminating
misaligned joins at bends. Radial vertex normals smooth the tube and sphere
lighting while retaining crisp status diamonds. Vertex counts and draw batching
are unchanged; no geometry is rebuilt on camera frames.

All 29 pure tests passed, including exact shared-ring position/normal equality,
unit radial normals and unchanged geometry counts. Reference-scene Chrome CDP
passed and its refreshed screenshot was visually inspected. The synthetic dense
scene passed with 1,095.2ms warm activation, 394 changed poses/frames in 8.0082s
(49.20 FPS), p95 interval 33ms, zero camera uploads/source requests and zero
settled frames. Eleven visible labels had no overlaps. Evidence is recorded in
`measurements/dashboard-scene-smooth-renderer.json` and `.png`. This is one
rendered SwiftShader client plus nine stream-only clients, not full real-source
or multi-rendered-client acceptance. Connector arrival and lane/filter motion
remain unfinished; this pass does not establish goal completion.

### Event-anchored speech bubbles — 2026-09-22

Reopened the reference before this pass. Pinned comments now position above their
projected event where space permits, or below near the top edge. A lightweight
SVG pointer connects the card to the actual bead (or its visible event cluster)
and follows camera motion without geometry uploads or source queries. Cards stay
inside the scene; offscreen/filtered events keep a detached card without a false
pointer. Dismissal clears the pointer and preserves camera keyboard focus.

Thirty pure tests passed, including bounded placement and detached edge cases.
Extended reference CDP passed anchored placement, pointer movement during camera
input, zero extra uploads/requests, bounded cards and dismissal. The original
reference camera is restored before checking three-card visibility. Full timeline
CDP passed WebGL, playback safety, reduced motion, settled idle, context-loss
fallback and narrow layout before the final tail-gap adjustment; reference CDP
and all pure tests passed again after that adjustment. Viewed/refreshed the
reference screenshot; the tightened tail keeps the comment above the cyan branch
rather than covering the main trunk. This does not close remaining motion gates.

### Anchored-bubble edge-case verification — 2026-09-22

Extended reference-scene Chrome CDP passed event-kind filtering (readable detached
card, no pointer), actual offscreen keyboard panning, Fit reattachment, reduced
motion and real WebGL context loss with the fallback camera. A settled 400ms
observation showed no extra animation frames, and the complete bubble interaction
sequence added no source requests. Dismissal still removes the pointer. No
application behavior changed in this verification pass.

### Embedded-server regression after visual changes — 2026-09-22

Current application build passed with zero warnings/errors. The initial test-runner
attempt was sandbox-blocked at its local socket before tests ran; rerunning with
local socket access passed all 183 Dashboard-filtered tests, zero skips/failures,
in 1m08s. Output: `/tmp/abacus-current-dashboard-tests.log`.

The rebuilt standalone host then ran read-only against the existing disposable
`/tmp/abacus-web-contract` repository on an ephemeral loopback port. Project JSON
loaded and all ten embedded frontend assets matched current source bytes exactly,
including the SVG speech-bubble markup and updated renderer/model modules. SIGINT
completed shutdown with exit 130, without forced termination. Host log:
`/tmp/abacus-built-assets-current.log`. This establishes current embedding/HTTP
and dashboard regression evidence, not a real-source browser animation benchmark
or full release-platform acceptance. Remaining motion/provenance gates stay open.

### Bounded timeline filter fades — 2026-09-22

Reopened the reference before implementing 180ms presentation fades for changed
event-kind and shared issue filters. The newly filtered scene and accessible
controls become authoritative immediately; stable lane slots and time anchors
remain fixed rather than being repositioned for decoration. Canvas/fallback and
labels fade together without an animation-frame geometry rebuild. Repeated kinds
are no-ops; rapid changes replace effects. Explicit/system reduced motion and
hidden views cancel effects. Pinned cards themselves are not faded or replaced.

Dedicated Chrome filter-motion CDP passed finite completion, rapid replacement,
identical-kind no replay/rebuild, reduced-motion cancellation/suppression, shared
status-filter fades and zero source requests. The test caught a missing shared
filter hook, corrected before the passing run. All 30 pure tests passed. New-lane
and integration-connector arrivals and clustering-specific transitions remain
open; these filter checks do not close the entire motion acceptance gate.

### Filter interruption and reference regression — 2026-09-22

Extended filter-motion Chrome CDP passed system reduced-motion suppression,
explicit full-motion override and switching to Issues during an active fade,
with cancellation and no replay on return. Reference CDP also passed against
the latest filter implementation, retaining three recorded starts, one verified
current return, three visible cards and the pinned-comment camera/filter/fallback
checks. All 30 pure tests passed. Updated the acceptance ledger to distinguish
implemented filter fades from still-unimplemented clustering transitions.

### New live-lane arrivals — 2026-09-22

Reopened the reference before this pass. Healthy live change events now recognize
previously unknown issue IDs and queue at most 24 lane arrivals. Rebuild immediately
drops lanes outside the active visible page. Their paths, path halos, markers and
identity cards fade in together over 350ms at fixed recorded positions. Shared
solid/glow draw ranges reuse uploaded geometry and return to static batches when
finished. Canvas fallback applies the same path opacity. Existing baseline, stale,
reconnect, playback, hidden-view and reduced-motion gates are retained.

Dedicated Chrome lane-arrival CDP passed new-lane presence and finite animation,
zero per-frame uploads/source requests, settled idle, repeated-ID no replay, reload
baseline no replay and reduced-motion suppression. All 31 pure tests passed,
including path solid/glow range accounting and cancellation. Further burst,
fallback and interruption regression coverage for this new path-animation code
is still needed; connector arrivals and clustering transitions remain open.

### Lane-fade interruption and fallback cleanup — 2026-09-22

Moved finished solid/halo animation-range cleanup ahead of the WebGL/fallback
and glow-enabled branches. Disabled glow and a lost WebGL context therefore
release finished ranges too, instead of retaining dormant range entries.

Extended lane-arrival Chrome CDP passed active reduced-motion cancellation,
restored card opacity, empty solid/halo ranges, completion with glow disabled and
a new lane fade after actual WebGL context loss. Canvas global alpha returns to
one after drawing. Existing event-arrival CDP also passed with the shared renderer
changes; all 31 pure tests passed. Lane burst/reconnect/hidden interruption and
connector/clustering work still require further evidence or implementation.

### Lane reconnect and burst verification — 2026-09-22

Extended reconnect Chrome CDP to add a missed issue while actually closing the
SSE stream. The new snapshot exposes that lane without any event/lane arrival
effects; subsequent live comment and lane arrivals still animate.

New lane-burst CDP adds 1,000 issues in one healthy change, verifies visible-page
retention, at most 24 queued lane effects, settled idle, and no replay when paging
to previously offscreen lanes. The first test sampled an old zero-effect frame
before the burst had drawn; synchronizing to the first post-update scene frame
corrected the assertion and the rerun passed. This is bounded-effect/idle evidence,
not a burst FPS measurement. All 31 pure tests passed. Hidden-tab lane interruption
and connector/clustering work remain open.

### Newly verified connector arrivals — 2026-09-22

Reopened the reference before implementation. A bounded presentation-only cache
remembers observed current topology across ordinary Git invalidation, never used
as render evidence. A fresh validated read changing the same target/start binding
from not-contained to contained fades only the return segment and its endpoint
over 350ms. The rest of the branch remains static. First loads and identical
containment never animate. Snapshot/stale/disconnect invalidation clears the
observation baseline; seeking historical playback clears it too. Reduced motion,
hidden views and offscreen lanes suppress arrivals. Labels retain “merge time
unknown”; this is no claim of a dated integration event.

Dedicated connector-arrival Chrome CDP passed false→true verification, a single
current return, provenance text, zero per-frame uploads/queries and no identical
or reload-baseline replay. After that run, added explicit queue removal for
withdrawn containment and offscreen connectors. All 31 pure tests passed after
those cleanup edits. More connector interruption, binding-change and real
Git-invalidation/reconnect coverage is still needed. Clustering motion remains
unimplemented; the goal is not complete.

### Connector Git-refresh and cancellation verification — 2026-09-22

Extended connector CDP now receives real SSE Git invalidation before explicitly
reloading evidence, rather than changing only the next endpoint response. It
passes the false→true presentation transition, withdrawal/re-verification, active
reduced-motion cancellation, retained authoritative connector and released
solid/halo ranges. The test explicitly waits for invalidated inspector evidence
before issuing the fresh read; both runs passed. No real Git process is involved
in this synthetic SSE fixture.

A subsequent code review tightened the transition identity check to include the
recorded issue branch as well as target and start commit, preventing another
branch's observation from serving as the animation baseline. All 31 pure tests
passed, including propagation of branch identity. End-to-end changed-binding and
connector reconnect/hidden-view cases remain open, as does clustering motion.

### Connector changed-branch and reconnect baselines — 2026-09-22

Extended the synthetic Git fixture with a replacement branch identity and used
a MutationObserver across the connector regression to catch even short-lived
arrival effects. Fresh validated containment for the replacement branch appears
without borrowing the original branch's false→true animation baseline.

The same browser run established not-contained evidence, changed containment
while dropping the actual SSE connection, waited for reconnection, and explicitly
reloaded Git evidence. The connector appeared with zero observed arrival effects,
confirming snapshot/reconnect reset rather than replay. Existing finite-arrival,
Git-refresh, reduced-motion and first-load checks also passed. All 31 pure tests
passed; no application edits were needed. Hidden-view interruption and clustering
work remain open, and this fixture does not establish real-Git integration timing.

### Actual hidden-tab lane and connector interruption — 2026-09-22

New `hidden-arrival-cdp.mjs` uses two real Chrome targets, not synthetic visibility
events. It backgrounds the timeline during a live lane fade and during a verified
connector fade, confirms `document.hidden`, adds another issue while hidden, and
observes zero rendered frames over 450ms. On activation, an attribute observer
records no replayed lane/connector effects, solid/halo ranges are empty and all
identity cards have full opacity. Verified containment remains present. The browser
check and all 31 pure tests passed; no application changes were needed. This
verifies browser-tab suspension, not OS process suspension. Clustering-specific
transitions and the broader visual/performance acceptance pass remain open.

### Post-connector dense-scene and reference verification — 2026-09-22

The current shared renderer passed the strengthened synthetic benchmark with
1,000 issues/10,000 events: 146.9ms warm activation, 402 changed camera poses/402
frames over 8.0132s (50.17 FPS), p95 interval 31.6ms, zero motion uploads/source
queries and zero settled frames over one second. Eleven visible labels had no
overlaps. Report and screenshot: `measurements/dashboard-scene-connector-renderer`
`.json`/`.png`, with machine/source hashes. This is one rendered SwiftShader client
plus nine stream-only clients, not ten rendered clients or real-source acceptance.
The warm timing is a single-run observation, not evidence of a speedup.

Latest reference CDP passed; the refreshed screenshot was visually inspected.
Curved returns, all three lane cards and the anchored speech bubble remain intact.
Clustering-specific transitions and remaining full-spec acceptance gates stay open.

### Clustered live-event arrivals — 2026-09-22

Reopened the reference before this pass. A cluster now inherits the newest queued
arrival time from its actual members rather than missing live arrival effects
because its aggregate ID is different. Cluster markers fade from 55% to full
opacity, so existing grouped history never disappears as though all its work
just happened. The renderer reuses geometry; individual events stay in the
accessible list at their recorded timestamps. Cancellation removes the opacity
floor along with the arrival marker.

Dedicated Chrome cluster-arrival CDP passed a new comment joining an existing
11:00 cluster, retained original/new event controls, finite completion, zero
per-frame uploads/queries, identical-member no replay and reduced-motion
suppression. All 32 pure tests passed, including member-time selection and the
cluster visibility floor. This covers live cluster membership arrivals, not yet
range-driven split/merge transitions or their full interruption matrix.

### Range-change fades and retained pins — 2026-09-22

Reopened the reference before this pass. Applying a different time range now
uses the existing bounded 180ms scene fade while the fitted camera transitions.
Events remain at their authoritative time coordinates; no temporary timestamp
is invented to animate regrouping. A pinned recorded event is resolved from the
new projection and retained if still in range and before the playhead, without
replaying its callout entrance. Current-state/out-of-range pins are not retained.
Identical ranges do not replay the fade; expanding loaded history also fades.

Dedicated range-motion Chrome CDP passed changed/identical-range behavior,
retained event URL and speech-bubble text, no entrance replay, out-of-range
clearing, reduced motion and zero source requests. All 32 pure tests passed.
A targeted fixture proving actual cluster split/merge membership across range
changes is still needed; this verifies the range presentation path, not every
regrouping combination or complete visual acceptance.

### Actual range-driven cluster split/merge verification — 2026-09-22

Extended range-motion CDP with comments at 10:59 and 11:00. The real renderer
reports one cluster in the 09:00–12:00 range, none in the 10:58–11:02 range,
and one again after widening. Each change starts the finite range fade while
the selected original comment ID stays in the URL and both underlying events
remain accessible. Existing speech-bubble no-replay, out-of-range clearing,
identical-range, reduced-motion and no-source-request assertions also passed.

Added only a cluster-count scene diagnostic to application code, computed during
rebuild rather than per frame. All 32 pure tests passed. This establishes the
actual split/merge path rather than merely a generic range-change fade, but does
not establish full visual acceptance or complete original server scope.

### Cross-feature browser regression after range motion — 2026-09-22

Fresh fixture/server/browser profiles passed all five scripts: timeline,
event-link, event-refresh, motion-interrupt and playback-filter. This combines
latest range/cluster motion with actual Back/Forward, explicit history loading,
pinned source refresh/disclosure focus, reduced-motion interruption, narrow
fallback and exact playback endpoint/filter membership. No application edits
were made in this verification pass.

The full .NET suite rebuilt successfully but was **not clean**: 1,144 passed,
three opt-in actual-CLI skips, and one failure in
`DashboardWorktreeDiffTests.RepeatedDirtyTextBinaryAndIndexEditsChangeContentRevisionWithoutChangingStatusOrHead`
(3m02s total). Its second SSE read at line 64 exhausted the shared 15-second
cancellation token after the intervening Git/watch operations. Full output:
`/tmp/abacus-full-animation-regression.log`.

An isolated rerun of the entire worktree-diff test class passed (one test, 17s):
`/tmp/abacus-worktree-timeout-recheck.log`. This suggests timing sensitivity but
does not prove the full-suite failure harmless or close the regression gate.
No timeout was widened or assertion removed. Investigate the shared timeout and
repeat broad verification before claiming a clean full regression.

### Worktree stream deadline isolation and full regression — 2026-09-22

Code inspection confirmed the test's connection deadline remained armed during
subsequent direct Git reads, collector refresh and nine other subscriber checks.
The long-lived HTTP subscription now keeps its explicit cancellation lifetime,
while connection setup and each complete SSE-event read retain separate 15-second
limits. The direct-watch phase similarly pauses its deadline during unrelated
Git work and rearms the same 15-second limit before consuming the next updates.
No delivery assertion, revision check, source-read count or production timeout
was removed or widened.

The rebuilt **full** .NET suite then passed: 1,145 passed, zero failures, three
opt-in installed-CLI skips, 3m10s. Output:
`/tmp/abacus-deadline-full-regression.log`. This supersedes the preceding failed
full regression as current evidence, but does not execute those three opt-in
CLI gates or establish completion of the remaining specification requirements.

### Blocked-lane waiting indicator — 2026-09-22

Reopened the reference and added its red circular pause/waiting glyph beside
blocked live endpoints. It participates in normal label culling, exposes an
explicit blocked/waiting-now accessible name and opens the current-state
inspector. It does not invent a transition time, and no current waiting badge
is shown in historical playback. No continuous pulse or extra render loop.

Reference CDP passed glyph count/label, native Space-key activation into the
blocked issue, source-local interaction and removal on historical seek. The
initial raw Enter CDP sequence did not activate the native button; the final
check uses complete Space down/up input. Viewed/refreshed the reference screenshot
and verified all three lane cards remain visible. All 32 pure tests passed.

### Waiting-marker dense-scene verification — 2026-09-22

Current synthetic scale check passed with 1,000 issues/10,000 recorded events,
1,100ms warm activation, 394 changed poses/frames over 8.001s (49.24 FPS),
p95 interval 32.2ms, no motion uploads/queries and zero settled frames over
one second. Sixteen visible labels, including waiting badges, had zero overlaps;
24 lanes and 240 event controls remained accessible. Viewed the dense screenshot.
Report/screenshot: `measurements/dashboard-scene-waiting-markers.json` / `.png`.

This remains one rendered SwiftShader client plus nine stream-only clients, not
a ten-rendered-client or native-GPU benchmark. All 32 pure tests passed. Dense
label-fit behavior remains a bounded usability compromise, not unlimited scene
capacity or complete keyboard-parity acceptance. No application edits this pass.

### Accessible current-observation parity — 2026-09-22

Added keyboard-list entries for current-state and verified-current-containment
markers already present in the visible scene. These are explicitly labeled
current observations, separate from recorded event controls and their 100-event
limit. They expose full provenance, preserve logical focus across rebuilds and
use the same inspector selection path as canvas markers. Historical playback
contains no current observations.

Extended reference Chrome CDP passed all three live observation entries, unknown
integration-time text, actual Space-key selection of containment into the correct
issue inspector, no extra source calls and removal in playback. Existing reference
geometry, bubble and waiting-badge checks also passed. All 32 pure tests passed.
This closes a concrete canvas/list content gap, not the full keyboard-parity audit.

### Current-observation source provenance — 2026-09-22

Current-state markers now carry their Beads issue revision and explicit working-set
provenance; containment markers carry the validated target-tip revision and explicit
current-Git-ancestry provenance. Inspector details expose those facts separately
from the full evidence text. Current-observation callouts use “Current issue state”
or “Git containment” headings instead of suggesting an unattributed authored
comment, and retain the warning that these are not recorded transitions/merges.

Reference Chrome CDP passed keyboard containment inspection, source revision and
Git-provenance text in the inspector, and the matching containment callout heading
and no-recorded-merge warning. All 32 pure tests passed. Historical integration
dates and full source/projection reconciliation remain separate unfinished work.

### Pinned-cluster refresh and member provenance — 2026-09-22

Cluster inspector details now render individual member entries with their author,
ISO timestamp, source/revision and available provenance or certainty. Unknown
authors/revisions remain explicit; members are not flattened into text that loses
source identity. Content continues to use DOM text nodes rather than HTML.

Extended cluster-arrival Chrome CDP selects a real three-comment minimap cluster,
verifies per-member authors and exact timestamps, then receives a fourth comment.
The pinned inspector and callout update to four members without entrance replay.
Existing arrival, geometry reuse, repeated-member and reduced-motion checks also
passed. All 32 pure tests passed. No source reads are introduced by member rendering.

### Bounded cluster member inspector — 2026-09-22

Large clusters now render at most 50 member entries per page, with explicit
previous/next controls and a live range/total label. Paging operates on already
loaded evidence and does not fetch or drop members. Controls retain usable focus
when reaching the first or last page. Small clusters retain the existing layout.

New cluster-pages Chrome CDP traversed all 121 equal-time comments, verified
50/50/21 rendered entries, compared the complete collected member set, checked
backward navigation and made zero paging source requests. An initial test assumed
numeric order for string IDs; the final test correctly verifies full membership
independently of their stable lexical ordering. All 32 pure tests passed before
that test-only correction. Page retention across source refresh still needs work.

### Retained cluster page and pager focus — 2026-09-22

The selected cluster now retains its local page index across inspector/source
refreshes, clamped to the new member count. Selecting a different event resets
the index. Expanded event details stay expanded; a focused pager button is
restored when still enabled, otherwise focus falls back to the event summary.

Extended cluster-pages Chrome CDP traversed all 121 members, focused Next on
page two, received a 122nd member and retained page two/focus/expansion, then
selected page three and shrank the cluster to 55 members. The view clamped to
the five remaining entries on page two without source requests. All 32 pure
tests passed. The retained-page gap recorded in the preceding entry is resolved.

### Inspector regression and live timestamp semantics — 2026-09-22

Live current-state and containment marker timestamps are scene display times,
not evidence of source collection or historical transitions. The inspector now
says “Displayed at,” and accessible observation tooltips explicitly distinguish
this from source observation and transition time. Recorded event timestamps are
unchanged. Reference CDP asserts both labels on verified containment.

The earlier regression process handle was no longer available, so its terminal
outcome was not assumed. A fresh sequential Chrome run completed successfully:
event-refresh, inspector, issue-edit, event-link, cluster-arrival and reference.
This covers pinned refresh/focus, keyboard tabs and narrow layout, draft/conflict
retention, deep-link history, clustered arrivals and current-marker provenance.
Per-script results are in /tmp/abacus-final-inspector-*-result.log. All 32 pure
browser tests passed and git diff --check was clean. This is fixture-browser
evidence, not proof of the remaining historical Git or full runtime gates.

### Reference-style event drop lines — 2026-09-22

Reopened the original supplied image and compared it with the rendered reference
fixture before changing visuals. Added quiet colored dashed guides from selected
recorded comments/status snapshots down to the time grid, matching the image's
spatial time cues. At most six guides are emitted; each WebGL guide uses twelve
line segments, clipped by the event's actual X/time during playback. Canvas
fallback renders matching dashed guides. These are decorative geometry, not
synthetic events, source reads, or continuous animations.

All 33 pure tests passed, including bounded guide geometry and timed clipping
attributes. Reference Chrome CDP passed, and the new screenshot was visually
inspected and copied to docs/measurements/dashboard-reference-scene.png. Existing
curved forks/current verified return, readable cards and anchored speech bubble
remain intact. This pass does not establish full reference fidelity or native
performance acceptance; the existing larger completion gaps remain.

### Exact fallback playback and guide parity — 2026-09-22

Added direct Canvas-renderer tests for selected-event guides: future guides are
absent, visible guides terminate at the projected time grid, and dash/opacity
state is restored before other drawing. Inspection also found fallback branch
playback stopped at the last geometry sample instead of the exact time plane.
It now interpolates the boundary segment, matching WebGL clipping without adding
geometry or source reads. A direct test covers partial and entirely future paths.

All 35 pure tests passed. Chrome timeline CDP passed again after the final change,
covering orbit buffer reuse, settled idle, playback/edit safety, reduced motion,
actual WebGL context loss into Canvas fallback, and narrow layout. Result:
/tmp/abacus-fallback-final-browser.log. git diff --check passed. These checks do
not close the remaining historical integration or broader release gates.

### Safe stopping checkpoint — 2026-09-22

At Ollie's request for a near-term stopping point, completed the already-running
Git verification without starting another feature. All nine DashboardGitTests
passed after a successful application/test build (zero failures/skips), including
new disposable-repository fast-forward, squash and cherry-pick cases. Identical
result trees do not substitute for ancestry: only fast-forward proves containment;
squash/cherry-pick retain triple-dot issue changes and unverified containment.
Recorded commit times remain explicitly distinct from integration times. Result:
/tmp/abacus-git-integration-matrix.log.

Current checkpoint: 35 pure frontend tests and the final timeline browser check
passed in the preceding pass. Six inspector/reference browser checks passed before
the later visual guide/fallback changes. No running verification is left from this
checkpoint. No commit or index changes were made by this work.

Remaining work is not a new issue-management expansion: source-history edge cases
(including reopened segments and retained prior containment observations), sourced
outcome/merge-summary presentation, a complete cross-view visual/accessibility/
motion review, and broad real-source/runtime/performance/security/package proof.
Unsupported issue-management actions remain deliberately frozen and unavailable.
The full specification remains unproven; this is a stable stopping checkpoint,
not a declaration that the goal is complete.

### Navigation and scrolling feedback — 2026-09-22 (in progress)

Added URL-restorable Workers and Worktrees tabs. Workers no longer occupy issue,
timeline or branch pages; standalone mode shows an explicit no-orchestrator state.
Worktree controls moved out of Branches, and their live subscription follows the
Worktrees tab (including document visibility). Operational tabs hide the unrelated
issue inspector and use the full workspace width. Worktree CDP passed repeated
edits, stale recovery, literal content and unsubscribe/reconnect in its new tab.

The workspace now allows vertical overflow, and the timeline does not shrink its
controls below its usable height. Ordinary wheel input is left for page scrolling;
Ctrl/Command+wheel retains camera zoom. The existing loaded-event range shortcut
is exposed as All entries in the main toolbar, with its coverage limitation stated
in the tooltip. All 35 pure tests passed and git diff --check passed.

This feedback goal is not complete: verify live-worker controls in their new tab,
short/narrow viewport scrolling with real wheel input, and range behavior across
filters/playback/old dates. The current range shortcut still covers exported and
loaded evidence only; it does not yet retrieve every older source-history entry.

### Navigation feedback browser verification — 2026-09-22

New navigation CDP passes actual worker SSE delivery and control visibility in
Workers, absence from Timeline/Issues/Branches, ordinary wheel scrolling over the
scene in a 1280×550 window, unchanged camera under ordinary wheel input, Ctrl+wheel
zoom, reachable workspace footer, and five-tab layout without horizontal overflow
at 390px. The initial test expected “Live” rather than the actual “● Live” label;
that expectation was corrected before evaluating the interactions.

All entries now resets the range to the earliest dated entry across all loaded
lanes (not the filtered/paged viewport), removes obsolete range padding and exits
playback through the existing safe return-live path. The browser test verifies
playback plus filtering followed by All entries restores the 09:00 lower bound
and live URL. The first implementation used seek(), which intentionally remains
historical; the failing test caught this and the final implementation uses
returnLive(). Final browser run passed: /tmp/abacus-nav-result.log. All 35 pure
tests passed before that final one-line correction; git diff --check is clean.

Remaining feedback acceptance: review complete range coverage semantics and rerun
adjacent timeline/range/worker-control regressions. No full-source-history fetch
is implied by All entries yet.

### Feedback regression checkpoint — 2026-09-22

Worktrees now has its own source-health/empty-state message. Entering Worktrees
no longer triggers a hidden branch comparison; selected worktree subscriptions
only resume in Worktrees. Five fresh Chrome fixture runs passed: navigation,
worktree, timeline, range-motion and inspector. These cover Workers isolation and
live updates, short-window actual wheel scrolling, Ctrl zoom, reachable footer,
narrow navigation, filtered-playback All entries, worktree stream lifecycle,
context-loss fallback, range animation/pin retention, and inspector draft/focus.
Logs: /tmp/abacus-feedback-*-result.log.

The application and embedded assets rebuilt successfully; all 186 Dashboard .NET
tests passed (zero failed/skipped), and all 35 pure frontend tests passed.
Log: /tmp/abacus-feedback-dotnet.log. git diff --check passed.

Asked Ollie whether “every single entry” means fitting currently available
entries immediately or fetching all older Beads/Git history too. Current behavior
is the former: all exported/loaded lanes regardless of viewport filters, earliest
known date through live now. The coverage tooltip and user guide state this
explicitly. No full-history retrieval is claimed. Remaining feedback ambiguity
is that coverage expectation, not the tested tab/scrolling changes.

### Touch scrolling and feedback completion review — 2026-09-22

Changed the scene's touch-action from none to pan-y, allowing browser-owned
vertical scrolling. Vertical touch movement no longer tilts the camera before
pointer cancellation. Navigation CDP now dispatches an actual multi-step touch
swipe in mobile emulation, verifies page/workspace scroll advances, and verifies
the camera remains unchanged. That browser check and all 35 pure tests passed;
the application rebuilt with the final embedded assets, zero warnings/errors.

Feedback review:
- Workers: dedicated URL-restorable tab, live updates/control visibility, explicit
  standalone state, absent from issue/timeline/branch layouts (navigation CDP).
- Scrolling: short-window wheel scroll reaches the footer; ordinary wheel does
  not zoom, modifier wheel still zooms; mobile vertical touch scroll does not
  alter camera; narrow navigation does not overflow (navigation CDP).
- Range: one-click All entries fits all currently available dated entries across
  lanes irrespective of filtering/paging, resets stale range bounds and exits
  playback (navigation CDP). This is a time-range operation, not a bulk history
  import. Default to the immediate range operation requested rather than introduce
  unrequested potentially expensive full-history loading. Coverage is explicit.
- Worktrees: separate tab; live edits, stale recovery and view-scoped subscriptions
  verified (worktree CDP); independent health message and no hidden comparison.

Five browser regressions and 186 dashboard backend tests passed before the final
touch-only adjustment; navigation, pure tests and build passed after it. This
completes the four-item navigation/scrolling/range feedback scope under the stated
immediate-range interpretation. It does not declare the earlier full web-server
specification complete or claim that unloaded historical records were fetched.

### Status-driven work episodes and rolling hours — in progress

Ollie clarified that the main spine is issue activity, not Git topology. Entering
in_progress starts a segment; blocked is shown only after work began; closure
ends the segment, and later activity creates another episode. Curves must not
require Git evidence; Git confirmation is a separate annotation when available.

Implemented the live “last N hours” form, fractional hours, rolling from/to bounds,
URL hours state and restoration. Manual historical ranges disable rolling; First
event → now clears the offset. Added a pure workEpisodes projection covering
backlog exclusion, blocked-only exclusion, repeated states, closure, reactivation,
playback cutoff and equal-time conflicts. This projection is not yet connected to
the scene renderer: the status-driven curves and automatic history acquisition
remain required next work; the old Git-conditioned rendering is not considered
finished.

All 39 pure tests passed. Extended navigation CDP passed fractional live hours,
server-clock bounds, clearing the offset, and existing scrolling/navigation checks.
An initial camera equality assertion ran before the existing 450ms fit transition
settled; waiting 600ms before measuring wheel behavior resolved that test race.

### Status-driven scene connected — in progress

Reopened the reference image before renderer work. Scene paths now use workEpisodes
rather than current Git containment: each recorded in-progress episode leaves the
activity spine, blocked intervals receive red dashes, and closed episodes return
at their own closure date. No lane/current marker continues past closure. Backlog
and blocked-without-prior-progress histories do not produce episodes. Historical
comments outside work episodes are omitted from scene/list/minimap; minimap lines
also stop at episode boundaries. Cards name the historical episode end separately
from present inspector state. Git facts remain available separately in inspector
and validated card summaries, not prerequisites for curves.

New episodes CDP loads source status histories without any Git evidence and passes
three starts, one closure return, no current closed endpoint, historical event
membership and absence of creation/backlog entries. Screenshot inspected at
/tmp/abacus-work-episodes.png. Initial expected count omitted a separately sourced
closure event (11 correct, not 10). The last run includes corrected card positioning
and minimap interval boundaries. All 40 pure tests passed; git diff --check clean.

Still required: automatic bounded status-history acquisition (currently manual),
full reopened-episode browser proof, playback boundary refresh, source/live history
reconciliation, and adjacent regression updates for intentionally changed topology.
Do not mark the goal complete at this checkpoint.

### Automatic episode history — in progress

Timeline now schedules status history automatically with two concurrent issue
loads, up to 1000 versions per issue using the existing continuation endpoint.
Loads are source/issue-revision fenced, aborted when the timeline/document is
hidden or history becomes stale, and failed keys are not retried in a loop.
Explicit Retry clears failed attempts. UI reports loaded/failed counts and source
unavailability. Attempt bookkeeping drops obsolete revision keys.

Initial automatic coverage is 64 issues, prioritized in-progress/blocked/closed,
matching the existing browser history cache bound. The coverage limit is explicit;
scalable access beyond this initial cohort remains required rather than silently
claiming every issue was loaded. Existing inspector history loading still works.

Extended episodes CDP now enters Timeline directly (no manual inspector loads),
first receives history failures, verifies retry visibility, retries successfully,
and proves status curves without Git evidence. All 40 pure tests passed. Remaining
work includes scalable history coverage, reopened-episode browser proof, playback
boundary geometry, revised stale/live reconciliation and broader regressions.

### Closure/reopening playback boundaries — in progress

Episode projection now uses the historical playhead, so future closure does not
bend an active historical segment back early. Rebuild computes the next recorded
status boundary; continuous playback rebuilds geometry upon crossing it rather
than rebuilding every frame. Reopened episode cards have distinct focus keys.

New episodes-reopen Chrome CDP uses two closed work episodes separated by an
inactive gap, with a comment inside the gap. It verifies four starts/two closures
across the fixture, omission of the gap comment, seek positions before/after both
closures and restart, and continuous playback across those boundaries. All passed,
along with all 40 pure tests. Result log is /tmp/abacus-episodes-result.log from
/tmp/abacus-run-reopen.sh. This establishes recorded-history playback; current
uncommitted status reconciliation and broad regression updates remain open.

### Live closure and independent history refresh — in progress

Fixed current in-progress visibility after reopening when the previous recorded
work episode has already closed. Keep the old closed segment, show a current-only
marker explicitly saying start time is not recorded, and do not invent a new
dated curve until a recorded in-progress snapshot arrives. Blocked-only issues
still do not acquire a work episode.

New episodes-live Chrome CDP passes source-driven closure with closed_at (current
endpoint removed), reopening without a dated start (old closure preserved), and
subsequent history-only revision update (new independent work segment). The fixture
initially changed history without emitting its revision event; corrected to publish
an actual history SSE revision while keeping issue revision unchanged, then the
complete test passed. All 40 pure tests passed and git diff --check is clean.

Still open: history coverage beyond the initial 64-issue cohort, current terminal
states without a trustworthy transition timestamp, optional automatic Git
confirmation, and broad regression/visual review of the changed episode semantics.

### Reachable history beyond 64 issues — in progress

Added explicit previous/next 64-issue history batches in stable issue-ID order.
Each batch sets the timeline's issue scope and resets lane paging; obsolete
requests are aborted and late responses outside the current scope are discarded.
Existing history cache and two-request concurrency bounds remain. The coverage
label names batch, issue range and total, rather than implying every issue's
history was loaded. This removes the permanent first-cohort exclusion.

New episodes-pages CDP generated 73 issues, loaded the first 64, navigated to the
remaining nine and verified nine closed status episodes including the last issue,
then returned to the first batch. Passed with no browser exceptions. All 40 pure
tests passed; git diff --check clean. Optional Git confirmation, missing-date live
terminal states and broad changed-semantics regressions remain unfinished.

### Optional Git confirmation for closed episodes — in progress

Selecting a closed issue now checks the existing revision-fenced Git endpoint and
shows a separate Overview confirmation. Validated current target containment is
labelled as Git-confirmed integration, with unknown exact merge time and an explicit
statement that the curve ends at status closure. Unbound/noncontained/error states
remain unconfirmed, and the operator can recheck. Historical playback does not
claim current Git facts as historical evidence.

Source invalidation clears the Overview confirmation and card facts, without
changing work-episode geometry. New episodes-git CDP passed automatic confirmation
on selecting a closed issue and actual Git SSE invalidation, retaining the status
closure return. All 40 pure tests passed. These tests do not finish missing-date
terminal-state handling or the broader regression/visual acceptance pass.

### Status-episode feedback completion audit

Implemented the clarified user scope (not the earlier full-server specification):

- Intuitive Live range: visible fractional-hour control, rolling lower/upper
  bounds, explicit rolling/fixed/historical label, URL restoration, and fixed
  manual ranges. Navigation CDP covers 2.5 hours and clearing the offset; pure
  tests cover moving now and preserved historical bounds.
- Status-driven curves: recorded in_progress starts leave the activity spine,
  blocked intervals are marked, closed episodes return at their closure. Git is
  not a prerequisite. Episodes CDP proves this without Git evidence.
- Historical closed work/restarts: no current closed endpoint or extension beyond
  closure; inactive gap comments/paths omitted; later work has its own segment.
  Reopen CDP covers seeks and continuous playback without future closure leakage.
- No fabricated dates: live closures with closed_at terminate correctly; unknown
  restart dates remain explicit until recorded history arrives. Undated terminal
  states stop at last recorded working evidence, with an unknown-end label, not
  a fabricated dated return. Live/undated browser and pure tests prove these cases.
- History availability: automatic two-request loading, failure/retry, revision
  invalidation and explicit 64-issue batches reach larger projects. The 73-issue
  browser fixture verifies both directions and its final closed episodes.
- Optional Git confirmation: selected closed issues show validated current
  integration in Overview. Revocation clears confirmation without altering status
  curves. Git CDP verifies this separation; exact merge time remains unknown.

Final evidence: navigation, episodes, episodes-reopen, episodes-live,
episodes-pages, episodes-git and episodes-undated passed in the combined batch.
Range-motion and inspector passed in the follow-up batch. The range test now
explicitly selects comments, because automatic status loading adds legitimate
status/closure clusters; new tests wait for DOM completion before querying controls.
All 41 pure tests passed. The rebuilt application/embedded assets and all 186
Dashboard .NET tests passed (zero failures/skips):
/tmp/abacus-status-episodes-dotnet.log. Browser results:
/tmp/abacus-feedback-*-result.log. git diff --check passed.

Known source limits are exposed, not missing feature claims: committed history
can omit uncommitted transitions; at most 1000 versions per issue are loaded;
64-issue batches keep memory bounded; Git ancestry confirms containment now, not
an exact historical merge timestamp. No automatic writes, commits or staging were
performed. The clarified timeline-feedback scope is complete with these explicit
source-coverage constraints.

### Minimap range brush

- Dragging across the minimap previews and applies a fixed time range in either
  direction; pointer capture supports release beyond the map and clamps bounds.
  Mouse and touch share the behavior. Escape, cancellation, hiding the view or
  document clears the preview; ordinary clicks retain event/time seeking.
- Range application shares the manual form path, including camera fit, retained
  in-range pins, finite transition and clearing the live rolling offset. No new
  source queries are needed. The date/time form remains the keyboard alternative.
- Verified: range-brush Chrome fixture (both directions, preview, Escape, click,
  touch, source-request count), range-motion and inspector browser regressions;
  31 timeline-model tests; JavaScript syntax and diff whitespace checks; .NET
  build succeeded with zero warnings/errors and refreshed embedded assets.

### Independent timeline axis stretching

- Added keyboard-accessible Time width / Vertical spacing sliders (0.25×–4×),
  separate browser-local preferences and Reset stretch. They leave time bounds
  and playback unchanged and carry between 2D and 3D.
- Apply world-axis scaling around the camera target in both the WebGL shader
  and CPU projection, keeping fallback paths, labels, callouts and picking on
  the same projection. Fit accounts for stretched extents; drag pan compensates
  for scale. Scale changes redraw without rebuilding geometry or reading sources.
- Verified 33 timeline model/projection tests, Chrome axis-scale checks (both
  modes, fit, persistence, reset, context-loss fallback, unchanged range and no
  extra requests), range-motion and inspector regressions. JavaScript syntax and
  diff checks passed; embedded-asset build passed with zero warnings/errors.

### Range-first history scheduling

- Prioritize likely range matches using available creation/closure/current-state
  fields before splitting the bounded 64-issue history cohorts. Old/unknown
  histories remain eligible; these hints never supply status-curve evidence.
- Manual/brush/live range changes notify the scheduler and reset to the first
  prioritized cohort. Keep useful reads in flight, abort out-of-cohort reads,
  retain the two-request bound and revision fences. Playback-only cursor changes
  do not repeatedly reset the queue. No claim of faster individual CLI reads.
- Verified 34 priority/model tests; Chrome 73-issue fixture requests the relevant
  high-ID issue first and checks paging/range reset; range-motion and inspector
  regressions passed. Build passed with zero warnings/errors.
