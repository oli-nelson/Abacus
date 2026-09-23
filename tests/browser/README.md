# Timeline browser checks

No browser/runtime package dependency is added to Abacus.

Pure model/camera tests (Node with ES-module syntax detection, tested on Node 26):

    node --test tests/browser/timeline-model.test.mjs

Visual/interaction fixture:

1. Start `node tests/browser/fixture-server.mjs` (loopback port 18081).
2. Start a disposable Chrome profile with `--remote-debugging-port=19222`.
   Headless machines may need `--use-angle=swiftshader --enable-unsafe-swiftshader`
   for software WebGL; this is test-only, not a user deployment requirement.

   Running `--headless=new` also needs the renderer kept awake, or
   `requestAnimationFrame` stops once the page settles and every `dataset.*`
   the scene publishes from a draw goes stale — checks then fail on timing
   rather than on behavior:

       --window-size=1671,1000 --disable-background-timer-throttling        --disable-backgrounding-occluded-windows --disable-renderer-backgrounding        --disable-features=CalculateNativeWinOcclusion

   Prefer waiting for the scene to publish a value over a fixed delay: software
   rendering regularly needs longer than a frame budget to land one.
3. Run `node tests/browser/timeline-cdp.mjs`. It closes that Chrome instance on
   success, writes `/tmp/abacus-timeline-3d.png`, and asserts rendering, history
   selection, inspector-to-event navigation, pinned callout, settled idle frames, orbit geometry reuse, 2D,
   historical edit refusal, current reread, reduced motion, context loss and narrow
   viewport behavior.
4. Stop the fixture server. On failure also close the disposable Chrome instance.

The fixture contains synthetic recorded snapshots, not production Beads data.
It intentionally shows unbound independent lanes, not invented integration.
This smoke is not the full reference-scene, ten-client idle or scale benchmark.

Branches view: with a fresh fixture and disposable Chrome, run
`node tests/browser/branches-cdp.mjs`. It checks comparison hierarchy,
optional commit evidence, bounded patch loading, and narrow-screen overflow.

Timeline View options: run `node tests/browser/view-options-cdp.mjs` with the
same fixture/Chrome setup to verify its popover fits right, left, and mobile
toolbar placements.

Open-to-resumed work: run `node tests/browser/resume-continuation-cdp.mjs` with
the same fixture/Chrome setup. It verifies separate recorded episodes use one
visual lane with a dashed inactive gap rather than a second trunk fork.

Responsive layout: run `node tests/browser/responsive-layout-cdp.mjs` to check
desktop, tablet, and phone scene/inspector arrangement and lower-control spacing.

## Real-CLI idle publication check

After building, with the existing disposable `abacus-web-contract` Beads/Git
fixture (main branch required):

    python3 tests/browser/noop-http.py /tmp/abacus-web-contract

This opt-in check requires a fresh build, starts a loopback standalone host, warms
Git and issue history, and connects ten logical clients to both main and worktree
SSE (20 connections). A unique untracked file exercises worktree fingerprints;
it is removed after shutdown. After warmup, 60 idle seconds must produce no data
events, issue projection rebuilds/serializations, aggregate snapshot rebuilds or
repeated history/diff commands. Snapshot bytes and ETag must remain unchanged.

Temporary wrappers record CLI duration/output-byte counts, not output contents.
Read-only diagnostics separate export hash and worktree fingerprint costs. Results
include tool/platform versions and a source fingerprint and are written to
`/tmp/abacus-http-idle.json`. A recorded result is in
[dashboard-http-idle.json](../../docs/measurements/dashboard-http-idle.json).
No API writes or agents are started. This is a small standalone fixture, not the
integrated-runtime or scale/rendering benchmark; elapsed stages are not isolated
CPU or peak-memory measurements.

## Runtime-control browser contract

Start `node tests/browser/runtime-fixture-server.mjs` (loopback 18082), launch a
**disposable** Chrome profile with DevTools on 19222, then run
`node tests/browser/runtime-cdp.mjs`. It closes that Chrome on success; stop the
fixture server afterward. It writes `/tmp/abacus-runtime-controls.png` and checks
operator confirmation, lost-response same-ID Force Run retry, Stop remaining usable
during force work, late accepted HTTP not overwriting terminal SSE, disconnected
controls, no supervisor Clean button, and nonoverlapping runtime/timeline layout at
1280px and 390px. This synthetic fixture never runs agents or invokes CLI tools;
production HTTP and supervisor lifecycle behavior are covered separately by .NET
tests, not inferred from this browser fixture.


## Real-CLI label delta check

After building, `python3 tests/browser/labels-http.py` uses only the existing named
`/tmp/abacus-web-contract` disposable fixture. It starts a loopback standalone host,
rejects a reserved label edit, adds a unique leading-dash label, removes it, and
verifies the original label set is preserved. This changes fixture issue update
history; it is not a read-only check and must never target a production repository.
The temporary server is stopped afterward. Browser composer interaction is not
proved by this HTTP/CLI test.


## Issue-edit composer checks

With a fresh `fixture-server.mjs` on 18081 and a disposable Chrome DevTools profile,
run `node tests/browser/issue-edit-cdp.mjs`. `CDP_PORT` overrides the default 19222
in all CDP scripts; use a fresh port when sequential browser instances overlap
shutdown. The script closes Chrome and writes `/tmp/abacus-label-composer.png`.
It checks label draft retention across issue switches, playback and live updates;
revision-conflict review; identical-ID retry after an unreadable accepted response;
delta-only payloads; refreshed stored content; no-op refusal; project-label add
and issue-label remove menus, custom label mode, description collapse, status-risk
confirmation, and separate label, reasoning, status, attention-and-block, and
resolve-and-reopen composer payloads;
and unsent title, label and note drafts surviving comment completion. The fixture is synthetic and
never starts workers or writes to Beads. Stop its server after testing.


## Issue table checks

`node --test tests/browser/issue-table.test.mjs` checks a 1,000-issue pure table
projection, deterministic ties, numeric/null ordering, page bounds and filters.
With a fresh visual fixture and disposable Chrome profile, run
`CDP_PORT=19222 node tests/browser/issue-table-cdp.mjs` for 123-row browser checks:
sort/aria state, bounded page sizes, filter resets, outside-page selection and no
additional source requests. This is not the full 10,000-event rendering benchmark.


`node --test tests/browser/issue-filters.test.mjs` checks shared metadata predicates
and historical unknown handling. The table CDP script also checks metadata filters
across Issues/live timeline/playback, clear and URL reload, using the same synthetic
fixture. It includes type/declared-target filters; it does not prove complete historical
metadata coverage or binding-aware effective-target filtering.


## Shared loaded-text search

With a fresh visual fixture and disposable Chrome, run
`CDP_PORT=19222 node tests/browser/search-cdp.mjs`. It verifies table/timeline search
parity, explicit lazy history loading, recorded-text range/playhead bounds, current-
only undated notes and no implicit history reads. It closes Chrome on success;
stop the fixture afterward. Pure search cases are in `issue-filters.test.mjs`.

Relationship coverage: `node --test tests/browser/issue-relations.test.mjs` checks
incremental reverse-edge updates/removals and unknown-versus-empty coverage.
With the visual fixture and Chrome CDP running, execute
`CDP_PORT=19222 node tests/browser/relations-cdp.mjs` to verify link navigation,
missing targets, an incoming-edge update without a selected-issue revision change,
and historical playback hiding current relationships. The fixture's
`/fixture/relations` endpoint changes synthetic data only.

Inspector checks: `CDP_PORT=19222 node tests/browser/inspector-cdp.mjs` uses the
visual fixture to exercise real keyboard tab selection/ARIA state, no source reads
on tab changes, draft retention, explicit history loading, URL/back-navigation
restoration, directly related ongoing-ticket selection and a 390px viewport.
The inspector check also explicitly loads synthetic unbound Git evidence. Actual
binding validation and ancestry behavior are covered separately by
`DashboardIssueGitTests` using a disposable real Git repository and the HTTP host.

Issue Git detail checks: `CDP_PORT=19222 node tests/browser/issue-git-cdp.mjs`
uses the visual fixture to verify explicit patch/history loading, literal HTML-like
source content, bounded/shared-ancestry/clock-skew warnings, live invalidation and
historical disablement. Synthetic details are activated only through
`/fixture/git`; actual Git and HTTP contracts remain in `DashboardIssueGitTests`.
The issue-Git browser fixture also includes dirty and unavailable registered
checkouts; the test verifies that unavailable status is not presented as clean.
`DashboardWorktreeTests` verifies actual clean/dirty transitions, ignored cache
stability, detached registrations, missing-worktree uncertainty and unchanged
Git index bytes/refs with real disposable worktrees.

Worktree content UI: `CDP_PORT=19222 node tests/browser/worktree-cdp.mjs` enables
synthetic registered-worktree facts in the fixture and verifies explicit loading,
refresh replacement, literal HTML-like text and the manual-snapshot coverage label.
Real tracked text/binary/index changes, conditional HTTP reads, invalid path inputs
and shared concurrent source reads are covered by `DashboardWorktreeDiffTests`.
The worktree fixture includes an untracked-content pane; refresh tests verify that
its content is replaced and HTML-like text remains literal. Real-Git tests also
cover untracked text/binary changes, option-like filenames, symlink target
non-disclosure, ignored-cache stability and the combined output/file-count limits.

Worktree content is now live SSE rather than manual-only. The browser check verifies
server-pushed repeated edits without clicking Refresh, literal untracked content,
stale clearing/recovery, unsubscribe when leaving Branches and reconnect on return.
`DashboardWorktreeWatchTests` verifies one source read for ten readers, quiet no-op
reconciliation, bounded latest-state backpressure, sanitized stale recovery,
idempotent disposal and topic/client capacity. The real-Git test includes HTTP SSE.

Idle worktree source measurement: run
`dotnet test --filter FullyQualifiedName~DashboardWorktreeIdleTests` with local
socket/process permissions. It watches one dirty real-Git worktree through ten
server subscriptions for at least 60 seconds and writes
`/tmp/abacus-worktree-idle.json`, separating diff queries, total Git commands,
fingerprint bytes and fingerprint-stage wall time. This is not the full combined
HTTP/Beads idle test or the large-scene benchmark.

### Draft creation CLI contract

Run `python3 tests/browser/create-draft-cli.py` with local Beads server access.
It is restricted to the existing disposable `/tmp/abacus-web-contract` fixture;
do not run a dispatcher against that fixture. It creates two tasks and an epic,
verifies far-future deferred creation, persisted blocked staging, clearing only
the staging deferral, and explicit dependency-aware publication. It also checks
Beads 1.2.2's rejection of `bd dep add <epic> <task>` without adding an edge.
Literal option-like text, labels, target metadata and unassigned state are read
back from the real CLI. Only created IDs are closed, prerequisites first; no
issues are deleted and no remote synchronization is requested.

Each run writes `/tmp/abacus-create-draft-<run>.json` before commands and after
checks, including failed-command output and cleanup results. On an unknown
creation outcome, inspect that run's unique title before retrying; the issue was
created with a year-9999 deferral, never an expiring short delay. This is CLI
contract evidence, **not** implementation or acceptance of the future dashboard
creation endpoint, policy validation, ownership fencing or retry ledger.

The C# staging primitive has a separate real-CLI integration check:

```sh
ABACUS_DRAFT_CLI_FIXTURE=1 dotnet test --filter FullyQualifiedName~BeadsDraftCreation
```

This opt-in creates two disposable tasks (with and without a reasoning label),
exercises the actual `Beads.CreateDraftAsync` helper, and verifies cleanup. The
test is explicitly skipped in normal runs. Created IDs and unique titles are
journaled to `/tmp/abacus-draft-helper-created.jsonl`. The ordinary unit tests
cover every command failure, cancellation, changed content/ownership, ambiguous
JSON, readiness checks and policy rejection without accessing Beads.

`create-draft-cdp.mjs` exercises the browser composer against the synthetic fixture:
policy conflict/review, retained text and target across modal/view changes,
truncated response with an identical request retry, literal rendering, actor
attribution, created-issue navigation, native Escape/focus restoration, playback
gating, confirmed reset and 390px layout. It writes `/tmp/abacus-create-draft.png`.
The fixture does not prove CLI semantics; the opt-in C# tests separately verify
the real HTTP/Beads creation path. Chrome may transparently retry a connection
reset, so the fixture truncates the JSON response to test application-level recovery
deterministically. Wait for the asynchronous native dialog close event before
checking focus restoration.

The same disposable-fixture opt-in enables the read-only cycle-check contract:
`ABACUS_DRAFT_CLI_FIXTURE=1 dotnet test --filter FullyQualifiedName~BeadsGraphValidation`.
This verifies the installed graph-check schema and two consistent cycle reads,
not readiness, missing dependencies or Git integration. Unit cases also cover
unknown schemas, conflicting observations, failure sanitization and cancellation.

## Large-scene rendering baseline

With a **fresh** fixture server and disposable headless Chrome profile started as
above, run `CDP_PORT=19222 node tests/browser/scale-cdp.mjs` from the repository
root. Use `--use-angle=swiftshader --enable-unsafe-swiftshader` for the documented
software-rendering baseline. The script closes Chrome; stop the fixture server
afterward. It replaces the fixture's three issues with 1,000 synthetic issues,
each carrying ten timestamped comments spread across the displayed time range.
Restart the fixture before running other browser checks.

This measures **one rendered browser and nine stream-only clients**, not ten
rendered browsers or a real Beads/Git server. It checks all 10,000 comments are
loaded, the visible page contains events, all ten streams remain connected,
visible labels do not overlap, all 24 paged lanes remain in the accessible list,
camera motion reuses geometry without API requests, and settled rendering stops.
It records warm issue-table-to-timeline activation, eight seconds of actual
renderer frame counts, observed frame intervals, CDP task time and final JS heap
size. Task time is not isolated process CPU; final heap is not peak memory.
Numerical speed targets are reported, not asserted, so slow runs remain useful
evidence rather than silently disappearing.

Outputs are `/tmp/abacus-scene-scale.json` and `/tmp/abacus-scene-scale.png`.
The JSON includes machine/browser details and hashes of the benchmark, fixture
and timeline sources. Review the screenshot separately: speed does not prove
label readability or reference-design fidelity. Real-source cold costs, combined
integrated-runtime behavior, peak memory and native-GPU measurements remain
separate acceptance work.


## Lane page stability

`CDP_PORT=19222 node tests/browser/lane-page-stability-cdp.mjs` against a fresh
fixture. `/fixture/staggered-work` adds earlier work with late-sorting IDs and
later work with early-sorting IDs, so issue-ID lane order would push drawn lanes
off the 24-lane page as the playhead reaches the later work. It asserts the page
holds the earliest work, that advancing the playhead only appends, that no drawn
lane is dropped, and that scrubbing back restores the same page.

## Playback selection and focus

`CDP_PORT=19222 node tests/browser/playback-selection-cdp.mjs` against a fresh
fixture. Advancing the playhead is not a change of playback context: it asserts a
pinned recorded event survives playback in the inspector, the callout and the URL,
that **Return to live** still clears it, and that a lane caption holding keyboard
focus is never culled when the needle stops reporting on that lane.

## Whole-project history fallback

`CDP_PORT=19222 node tests/browser/project-history-fallback-cdp.mjs` against a
fresh fixture. `/fixture/project-history-unsupported` makes the fixture advertise
the whole-project history capability and then refuse the read, standing in for a
`bd`/storage that cannot answer the query. It asserts the refused read is tried
once and not retried in a loop, that every issue is then read individually, and
that recorded episodes still arrive. Set the fixture route before navigating:
capabilities are read once at page load.

## Three-issue reference composition

Run `CDP_PORT=19222 node tests/browser/reference-cdp.mjs` against a fresh fixture
and disposable Chrome. It explicitly loads all three recorded bindings, commit
histories and issue snapshots, pins the recorded comment, then fits the scene.
UTC matches the reference clock labels. It asserts three recorded starts, exactly
one verified current return and that the selected lane is the only captioned one
(lane captions follow the playhead needle: the selected issue when there is one,
otherwise every episode open at the needle), and saves
`/tmp/abacus-reference-scene.png`. The fixture is synthetic visual evidence, not
proof of production Git integration or worker ownership. Restart it before other
browser tests because this mode changes containment facts.

### Recorded-event deep links

With a fresh `fixture-server.mjs` and headless Chrome DevTools endpoint, run
`CDP_PORT=<port> node tests/browser/event-link-cdp.mjs`. Checks comment selection
across reload, explicit-history-only snapshot restoration, unloaded-event messaging,
dismissal, clearing the callout/event URL on return to live, actual browser
back/forward range restoration, zero historical-navigation source requests, and
the required current-issue reread before live editing. Live URLs are also reloaded
to verify they remain live. This script does not exercise a real Beads server.

### Camera preferences

`CDP_PORT=<port> node tests/browser/camera-preferences-cdp.mjs` uses a fresh fixture
and Chrome profile to verify saved camera pose/control restoration, URL projection
precedence, malformed preference fallback, and no source requests for camera
mode changes. Pose assertions wait for the changed rendered frame and storage
write, then compare numeric objects rather than JSON property order.

### Inspector resizing

`CDP_PORT=<port> node tests/browser/inspector-resize-cdp.mjs` checks real pointer
dragging and keyboard adjustment, saved width across reload, minimum width,
zero source requests during resizing, and the stacked 390px layout.

The scale harness now oscillates the camera within its orbit bounds and requires
changed poses on at least 80% of rendered frames. It fails on warm scene activation
over two seconds or average motion below 30 FPS, after writing its report for
diagnosis. Current continuous-motion evidence is recorded in
`docs/measurements/dashboard-scene-motion-current.json`; older single-direction
runs could spend most of the test at the camera clamp.

### Pinned-event source refresh

`CDP_PORT=<port> node tests/browser/event-refresh-cdp.mjs` uses fixture source
changes to verify updated comment text in both callout and inspector, preserved
expanded-detail focus, no entrance replay, clearing when selecting the issue, and
removal of invalidated selection/URL state.

### Camera interruption

`CDP_PORT=<port> node tests/browser/motion-interrupt-cdp.mjs` interrupts active
2D/3D transitions with explicit and emulated system reduced-motion preferences,
and switches away/back during easing and playback. It verifies a saved stationary
playhead and accurate Play/Pause state after returning, exact projection endpoints,
no source requests, and no resumed animation after the view-resize redraw settles.

### Playback filters and completion

`CDP_PORT=<port> node tests/browser/playback-filter-cdp.mjs` loads recorded history,
plays across a blocked transition with a status filter, and checks lane/count
updates, final inspector/transport state and exact URL playhead with no new source
requests.

### Inspector motion

`CDP_PORT=<port> node tests/browser/inspector-motion-cdp.mjs` checks finite panel
entrances, rapid-tab cancellation, no replay on the same tab, explicit reduced
motion cancellation, live system-preference changes, full-motion override,
Branches-view cancellation/no replay, preserved edit drafts and no source requests.

### Real tab visibility

`CDP_PORT=<port> node tests/browser/background-tab-cdp.mjs` creates a second
disposable Chrome tab and activates it. It requires the dashboard's real
`document.hidden` state (no mocked visibility property or synthetic event), then
checks stopped frame counts, paused/saved playback, cancelled inspector effects,
and settled 2D return without replay. The extra tab is closed afterward.

### Live event arrival

`CDP_PORT=<port> node tests/browser/event-arrival-cdp.mjs` introduces a new
recorded comment through the fixture's live stream and verifies a finite bead
fade, unchanged uploads/API requests during animation, settled idle, no repeated
event/snapshot replay, reduced-motion suppression/cancellation, draw-range
cleanup, and finite fallback rendering after WebGL context loss.

### Reconnect arrival baseline

`CDP_PORT=<port> node tests/browser/reconnect-arrival-cdp.mjs` ends the actual SSE
connection while adding an unseen fixture event, waits for automatic reconnection,
and observes that the fresh snapshot does not animate missed events. A subsequent
live comment must still receive its normal finite entrance.

### Large live-event burst

`CDP_PORT=<port> node tests/browser/arrival-burst-cdp.mjs` starts with 1,000 issues
and 10,000 comments, adds one live event per issue, and checks retained visible
events, bounded effects, settled idle and no offscreen replay after paging. It
does not measure burst FPS or backend source costs.

### Current-status motion

`CDP_PORT=<port> node tests/browser/status-motion-cdp.mjs` waits for the initial
render, changes a live issue status, and verifies a finite marker color transition
with immediate status text, no animation-time geometry uploads/API calls, cleanup,
no identical-update replay, rapid status retargeting, reduced-motion cancellation
and status blending after WebGL context loss.

### No-glow accessibility preference

`CDP_PORT=<port> node tests/browser/glow-preference-cdp.mjs` checks the accessible
toggle, immediate DOM shadow removal, persistence across reload, fallback
compatibility, and zero geometry uploads/source requests when toggled.

### Minimap selection

`CDP_PORT=<port> node tests/browser/minimap-selection-cdp.mjs` checks real pointer
selection of a recorded event into the inspector, exact event/time URL state,
playback write suppression, event-kind filtering, blank-space scrubbing and no
extra API calls.

The reference-scene test also verifies event-anchored speech bubbles: camera
tracking without uploads/queries, in-scene bounds, filter/offscreen detachment,
Fit reattachment, reduced-motion 2D, WebGL-loss fallback, settled idle and dismissal.

### Timeline filter motion

`CDP_PORT=<port> node tests/browser/filter-motion-cdp.mjs` verifies finite
event-kind and shared-status filter fades, rapid replacement, identical no-op,
reduced-motion interruption, settled uploads and no extra source requests.

### Live lane arrivals

`CDP_PORT=<port> node tests/browser/lane-arrival-cdp.mjs` verifies new live lane
fades, no per-frame geometry uploads/source requests, settled idle, repeated-ID
and reload-baseline suppression, and reduced-motion suppression.

`lane-burst-cdp.mjs` adds 1,000 new issues and checks bounded visible effects,
settled idle and no replay after paging. `reconnect-arrival-cdp.mjs` also checks
missed lanes become a fresh snapshot baseline and later live lanes still animate.

### Verified connector arrival

`connector-arrival-cdp.mjs` verifies newly observed false→true current containment,
finite return-connector fade, unknown merge-time labeling, no per-frame uploads
or queries, and suppression for identical evidence and first-load baselines.

Connector coverage also includes actual SSE Git invalidation, reduced-motion
cancellation, changed-branch baseline isolation and actual disconnect/reconnect
without replaying newly loaded containment.

`hidden-arrival-cdp.mjs` backgrounds a real Chrome tab during lane and connector
fades, receives another lane while hidden, then verifies no hidden frames, no
replay on activation, released draw ranges and retained verified containment.

### Cluster member arrivals

`cluster-arrival-cdp.mjs` adds a comment inside an existing event bucket and
checks finite cluster fade, retained individual events, geometry reuse, no extra
queries, identical-member no replay and reduced-motion suppression.

### Range motion and pinned selection

`semantic-activity-cdp.mjs` checks meaningful Activity cards instead of repeated
snapshots, separately retained comments, collapsed raw evidence and deduplicated
reloads that preserve already-loaded timeline history.

`closing-label-cdp.mjs` verifies visible, clickable status labels for clustered
closures in 2D/3D and narrow layouts, while respecting event-kind filters.
`timeline-annotations.test.mjs` checks cluster expansion, status priority over
comment volume and alternative placement before collision hiding.

`event-placement-cdp.mjs` checks that only an initial open → in-progress event
uses the spine, while same-time comments, resumes and closures remain on their
branch even after narrowing the range. `event-placement.test.mjs` covers entry
classification and event-aware curve endpoints. Concurrency tests also cover
side preservation after another issue ends to prevent return-curve overshoot.

`panel-maximize-cdp.mjs` checks title-only floating labels, expanded scene height,
view-only controls, preserved range/inspector, Escape and button restoration,
tab switching, narrow layout and no additional data reads.

`concurrent-layout.test.mjs` checks sequential reuse, mirrored overlap offsets,
smooth survivor repositioning and deterministic multi-issue ordering.
`timeline-geometry.test.mjs` verifies circular marker radii and perpendicular
tube cross-sections at 10× time stretch with compressed vertical spacing.

`meaningful-events-cdp.mjs` verifies that unchanged snapshots do not create nodes,
four meaningful event kinds filter correctly, clusters count actual changes,
notes show before/after text, technical evidence starts collapsed, and status
curves survive. `meaningful-events.test.mjs` covers baselines, duplicates,
closure deduplication, label order, note clearing and equal-time ambiguity.

`history-priority-cdp.mjs` checks that a range-relevant issue beyond the old
64-issue boundary is requested first, all batches remain reachable, and changing
range resets the prioritized batch. `history-priority.test.mjs` covers ordering,
uncertain/spanning dates and range changes without excluding older issues.

`axis-scale-cdp.mjs` checks independent time/vertical sliders, 2D/3D switching,
fit, persisted settings, reset, canvas fallback and no extra source requests.
`node --test tests/browser/timeline-scale.test.mjs` checks orthographic and
perspective projection consistency and independent scaling around the target.

`range-brush-cdp.mjs` checks forward/reverse minimap drag selection, highlighted
preview, Escape cancellation, retained click seeking, touch selection and no
additional source requests. Run against the fixture server with Chrome CDP.

`range-motion-cdp.mjs` checks range-change fades, identical-range suppression,
retention of in-range pinned events without callout entrance replay, out-of-range
clearing, reduced motion and local-only range changes.

The range-motion test includes actual grouped→split→grouped neighbouring comments
and checks both retained individual selection and accessible member controls.

`cluster-pages-cdp.mjs` verifies all 121 members of a dense equal-time cluster
remain reachable with at most 50 rendered entries and no paging source requests.

`node tests/browser/navigation-cdp.mjs` checks dedicated Workers visibility and
live updates, short-window real wheel scrolling, Ctrl+wheel camera zoom, reachable
footer, narrow five-tab navigation, and All entries from filtered playback.

`playback-comments-cdp.mjs` checks comments up to the playhead, exclusion of later
comments, the maximized inspector's live-edit recovery, visible refresh failure
and retry, preserved drafts and restored label editing.
