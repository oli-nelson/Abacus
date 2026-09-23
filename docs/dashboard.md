# Web dashboard (implementation preview)

`abacus dashboard` currently serves an **interactive issue browser**, with shared
Beads/Git collectors, live updates, search/status filtering, issue selection and
deep links. Branches lists actual local refs and registered worktrees, with
target-aware triple-dot file summaries, ancestry checks, and lazy bounded patches. The complete interactive 3D/Git dashboard in
[ABACUS_WEB_SERVER_SPEC.md](../ABACUS_WEB_SERVER_SPEC.md) is still under construction.
Comments, title/description/priority edits, ordinary label deltas, append-only notes and plain attention
requests/resolution are available.
Bounded committed history is available on demand. A real WebGL timeline now offers
perspective/orthographic cameras, recorded-start branch curves and event playback.
Explicit Git loading validates recorded binding/start ancestry and current tip
containment; a return curve means contained **now**, not a known merge timestamp.
Branches also offers bounded dirty-worktree diffs. Other issue mutations, verified
current worker/claim associations, and complete historical integration topology
are **not available yet**.
`run --dashboard` now hosts the same issue/Git preview in-process alongside workers.
It exposes live worker rows and manual claim Pause/Resume from this run.
Worker cards show the current issue ID and its title from the Beads snapshot;
long titles truncate within the card, with the full title available on hover.
Worker Stop/Restart and confirmed Clean Workspace are available with tracked
completion. Confirmed Stop Run uses the same normal cancellation/recovery path as
stdio shutdown. Supervisor Stop/Restart use explicit acknowledgement; confirmed Force Run tracks
its own execution, cleanup and verification. `POST /api/v1/run/actions`
accepts only `{session, requestId, command: "stop", confirm: true}` with the usual
JSON/origin/request-header checks. Its 202 means accepted, not cleanup completed.
Shutdown is signalled without waiting for response delivery, so an accepted
response may be lost during disconnection. Accepted stop dispatch is exactly once. Retry the
same ID only in the same session. The listener closes with the owning run; check
its terminal result and logs rather than treating disconnection as success.

### Dependency Tree

The **Dependency Tree** tab shows the current Beads issue graph as a left-to-right
tech tree: blocking prerequisites lead to dependent issues. Hierarchical
parent-child and non-blocking related links do not become dependency lines. Each card opens the existing
issue inspector and displays its current state. Connected groups are laid out
separately; issues without recorded links remain visible below them. Search and
status selection dim nonmatches rather than removing graph context. Drag empty
space to pan. As in the timeline, unmodified wheel scrolls, Ctrl/⌘ wheel or a
trackpad pinch zooms at the pointer, and with the tree focused arrow keys pan,
`+`/`-` zoom, and `F` fits the view. Use the Zoom slider, Fit view, or Focus
selected as alternatives. Unknown or out-of-snapshot
relationships are counted rather than inferred, and this view is current-only,
not timeline playback.

### Attention Center

The **Attention Center** lists every issue with the
`abacus:needs-user-attention` label, including closed issues. Each card shows
its three newest recorded comments (author, time, and full text) in chronological
order, with the newest at the bottom, so the latest conversation is visible
without opening the inspector. **Resolve attention** and
**Resolve attention & reopen** select that issue and action in the existing
review-and-submit composer; an optional response and the usual revision and
retry safeguards still apply. The tab's red badge counts pending issues. The
**Workers** shows a yellow badge while the manual claim gate is paused, or a
green badge when claims are enabled and the schedule allows them. Schedule
closure alone shows neither badge.

### Timeline and inspector navigation

- Drag to orbit; Shift-drag (or Pan mode) to pan. Wheel/pinch zooms. Focus the
  scene for arrow-key camera controls, `+`/`-` zoom and `F` to fit.
- Camera pose and pan/orbit preference stay local. A shared `camera=2d` or
  `camera=3d` parameter overrides the local projection choice.
- Selecting a recorded event stores its ID in the URL. Reload and Back/Forward
  restore loaded evidence; unavailable history is named explicitly and is loaded
  only on request. Draft comment text never goes into shared links.
- Scrubbing is read-only. Return to live rereads the selected issue before editing
  becomes available, and clears the previous event callout.
- The inspector's event disclosure expands full text/provenance without crowding
  Overview. Author badges use recorded initials, not verified identity or portraits.
- Drag the divider to resize the inspector. Focus it for Left/Right adjustment,
  Home/End bounds; double-click resets the width. Narrow screens stack the panels.
- The Glow toggle removes scene halos and interface shadows without changing
  event data, status colors or focus outlines; its preference is stored locally.
- Motion defaults to the system preference and can be explicitly reduced. The
  accessible event list remains available when labels are culled or WebGL fails.

```sh
abacus dashboard --repo /path/to/main-checkout
abacus dashboard --repo /path/to/main-checkout --bind ::1 --port 8081
abacus dashboard --bind 0.0.0.0 --port 8080 --actor ollie --poll-interval 5s
```

Default binding is `127.0.0.1:8080`. Both IPv4 and IPv6 are supported. Hostnames
resolve to explicit addresses; binding never silently broadens to a wildcard or
selects a different port. No browser opens automatically. Operational output goes
to stderr. The standalone command never starts or adopts an agent, takes a
controller lease, or mutates Git. Beads writes happen only on explicit client actions.
It validates the Beads project's Git identity
(including explicit redirects) before serving. Ctrl-C exits 130; SIGTERM stops
collection/listening without affecting external workers.

## In-process run preview

```sh
abacus run --config abacus_codex.json --dashboard --dashboard-port 8081
abacus run --config abacus_codex.json --dashboard=false
```

Saved fields `dashboard`, `dashboardBind`, `dashboardPort`, `dashboardActor` and
`dashboardPollInterval` follow config inheritance/editor conventions. Enablement
is false by default. Saved tuning remains validated but dormant when disabled;
explicit tuning flags require effective enablement. `preflight --config` validates
saved settings without binding. Unprefixed listener flags remain standalone-only.

A run binds after normal preflight but before controller/workspace ownership or
worker startup. Binding errors fail startup. Later web collector/host failures
close web access and report a sanitized diagnostic without cancelling workers or
restarting the listener. Finite runs dispose the listener when the run exits;
stdio diagnostics stay on stderr. No child Abacus process is started.

This is not the complete integrated capability: recovery snapshots, shared
orchestration monitoring and full control/lifecycle acceptance are still pending. `--start-paused` can now use the web claim Resume
control without a TUI or stdio controller. The project endpoint labels the mode
`integrated`; runtime state is connected when the run supplies its output state,
with a separate capability indicating claim-control availability.

## Trust boundary

There is **no authentication**. The server exposes issue content edits and comments
to every client able to reach it, including on non-loopback listeners. Use a firewall, trusted VPN, or independently managed secure proxy.
HTTP provides no confidentiality. Dashboard writes default to the repository's
effective Git `user.name` (or `user.email` if no name is set); configure one before
starting the server. `--actor`/`--dashboard-actor` override that attribution and
are not a login.

Host headers must name the configured host or bound IP. For wildcard listeners,
local interface IPs and localhost are accepted, not arbitrary DNS names. A remote
client should use a bound interface's IP or configure the intended hostname.
Cross-origin writes are rejected; mutation clients must supply JSON and
`X-Abacus-Request: 1`. No CORS, automatic network discovery, public file serving,
source editing or raw shell execution is exposed. Assets are embedded locally and
issue text is inserted as text, never HTML.

## Runtime dependency

The same .NET executable hosts Kestrel using the Microsoft.AspNetCore.App shared
framework, an explicit exception documented in PLAN.md. Framework-dependent builds
need the .NET 10 ASP.NET Core runtime; self-contained release packages include it.
No runtime Node server, CDN or production NuGet package is used. Endpoint setup uses
explicit IP listeners to avoid Kestrel's hostname URL wildcard semantics, as
explained in [Microsoft's endpoint documentation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0).

## Current protocol

- `/api/v1/project`: self-declared actor, hosting mode and actual capabilities.
- `/api/v1/snapshot`: shared cached snapshot, ETag, source health and stream cursor.
- `/api/v1/issues/{id}`: current public fields and resource ETag, or 404.
- `/api/v1/issues/activity`: committed snapshots for every current issue in one
  read, each stamped with the issue revision it was fenced against, plus the
  shared history revision and coverage. Takes no query fields. Requires the
  `projectHistory` capability (503 otherwise); a history revision that moves
  during the read, or a payload labelled with another revision, is refused (409).
- `/api/v1/issues/{id}/activity?limit=50&after=<cursor>`: committed snapshots,
  coverage caveats and continuation for one issue. Page size 1–100, default 50;
  at most 1,000 source rows loaded per issue revision, collapsed to distinct
  recorded states. Unknown/duplicate query
  fields are rejected. A changed issue/history revision invalidates pagination
  (409); restart at the first page rather than merging mismatched histories.
- `/api/v1/branches`: shared Git refs/worktrees, target choices and source/policy health.
- `/api/v1/branches/history?branch=<full-local-ref>&limit=100`: bounded reachable
  commits with full IDs, parent IDs, author, commit timestamp and message. Limit
  1–1,000, default 100; topology order is preserved and detected clock skew is
  explicit. Only known local branch refs are accepted, not paths/revision expressions.
- `/api/v1/branches/compare?target=<full-local-ref>&branch=<full-local-ref>&patch=true`:
  configured-target comparison with full tip IDs and an optional bounded patch.
  Unknown query fields/refs are rejected; invalid policy or stale Git disables it.
- `/api/v1/events`: SSE changes/removals/health, 15-second comment heartbeats.
  Git changes use the `git` event, independent of Beads issue upserts.
  The `history` event carries Dolt history revision/health, independently of
  current issue fields; it invalidates demanded activity, not unchanged issues.
  Pass the snapshot cursor as `after` initially, then use standard Last-Event-ID.
  `resync` means fetch one fresh snapshot and reconnect with its cursor.

Stream IDs contain server-instance ID and monotonic sequence. Replay is bounded to
128 events / 8 MiB, clients to 64 with 32 queued events each; slow clients disconnect
and resync. Identical source reads reuse the same serialized snapshot and publish
no data events. Closing the server retains the browser's last displayed snapshot.
HTTP mutation bodies are capped at 64 KiB, including chunked requests.
Source errors preserve last-good issue data and a sanitized stale indicator.

Implementation state and remaining acceptance gates:
[dashboard implementation](dashboard-implementation.md).

Branch comparisons use `target...issue`, showing merge-base-to-branch-tip changes,
not remaining work after a merge. Ancestry proves containment in the target now,
not exact landing time or task correctness. A matching `abacus/<issue-id>` is
labelled naming convention only until execution binding validation is implemented.
Dirty state is explicitly not loaded; no clean-state claim is made. Invalid or
missing target policy leaves branch browsing available without inventing a default.

## Issue edits and comments

The inspector displays stored comments with their actual author/time and preserves
drafts across live refreshes. Notes are current content, not invented dated events.
Startup checks the installed Beads CLI for the required comment/update capabilities.

- GET `/api/v1/mutations/context` supplies the server session, server timestamp and
  retry window.
- POST `/api/v1/issues/{id}/actions` accepts strict JSON with `requestId`,
  `expectedRevision` (the issue's canonical revision), and `action`.
- `action: "comment"` requires `text`.
- `action: "edit"` accepts one or more of `title`, `description`, `priority`
  (0–4), and `appendNotes`. Other fields/actions are rejected.

Request IDs have the form `<session>:<serverUnixMilliseconds>:<random-guid>`.
Use the context timestamp rather than the browser clock. A bounded 1,024-entry,
15-minute in-memory ledger shares duplicate requests only when their payloads
match. Pending entries are never evicted. Expired IDs and IDs from an earlier
server session are rejected, not silently executed again. This is not a durable
transaction log.

Writes are serialized within this server, re-read current Beads data before
checking the expected revision, pass literal arguments without a shell, and
re-read to verify stored results. Stale sources reject writes; conflicts return
the current issue for review. This is not cross-process atomic compare-and-swap:
external clients can still race. The configured actor is passed to Beads, and no
automatic remote synchronization is requested.

Results distinguish `completed`, `rejected`, `partially-applied` and
`outcome-unknown`. A disconnected browser does not cancel an already submitted
write. Retry an uncertain response with the **same ID and payload**; never blindly
retry an append with a new ID. The composer retains uncertain submissions and
requires explicit review before using a new revision/request. Server shutdown or
restart can leave an uncertain result that must be inspected manually. No rollback
or cross-process exactly-once guarantee is claimed.

Graceful dashboard shutdown closes mutation admission and waits up to 10 seconds
for accepted writes and their verification. Existing same-ID retries remain valid
while HTTP is open; new requests receive a stopping rejection. Mutation execution
has a separate lifetime from read collectors, so stopping those collectors does
not cancel an accepted write. At grace expiry, the shared CLI runner cancels and
joins command work using its existing bounded process termination path. An already
attempted write then reports unknown if verification cannot finish; queued work
that never attempted a write is rejected. The listener and final disconnection
follow draining. Abrupt process termination still cannot guarantee completion.

Status, assignment, dependencies, target and
Resolve-and-reopen actions are not enabled yet. Reservation-sensitive writes remain unavailable.

### Create a blocked draft

Choose **New issue** in live mode to open the draft composer. It shows the configured
actor and target/reasoning choices. Enter title, description, type, priority,
target, an optional/required reasoning tier, and ordinary labels (one per line).
The form preserves input when closed, across view changes and during source
updates; draft text never enters URLs. Escape/Close returns focus to New issue.

A policy conflict offers **Reload policy for review**, preserving entered fields.
An uncertain result freezes the submitted payload and offers only a same-request
retry; it never silently starts another creation. A known created ID links to the
issue once its snapshot arrives. **Start a different draft** requires confirmation
and clears fields so an old submission is not accidentally repeated. Historical
playback and stale source state disable creation. Reloading the page does not
persist unsent input or the in-memory receipt: inspect issues before recreating.

### Draft-only creation API

`GET /api/v1/issues/drafts/context` reads the configured targets and reasoning
requirements and returns their `revision`, the mutation session/time and bounded
creation choices. It does not create an issue. `POST /api/v1/issues/drafts` accepts
only `requestId`, `expectedRevision`, `title`, `description`, `type`, `priority`,
`labels`, and optional `target`, with the same JSON/custom-header/Origin/body-limit
rules as other mutations. Since a new issue has no existing issue revision,
`expectedRevision` is the policy revision from the creation context. A changed
policy is rejected with 409 before creation; invalid/reserved fields are rejected.

Creation is **draft-only**, in standalone and integrated mode. The API creates
with a year-9999 deferral, verifies stored content and absence from ready work,
sets blocked status, verifies again, and only then clears the staging deferral.
A successful 201 receipt includes `createdIssueId`; it does not mean the issue is
published or dispatchable. The context explicitly reports `publicationAvailable:
false`. Reviewed publication is not implemented yet.

Creation shares the repository-wide write lock, bounded session request ledger,
disconnect-independent execution and shutdown drain with existing issue edits.
Repeat the same request ID and unchanged payload only; unknown outcomes never
automatically rerun `bd create`. A known partially staged ID is returned for
review. After an unknown receipt or server restart, inspect existing issues before
starting a new creation operation. No automatic rollback or remote push occurs.

`GET /api/v1/issues/{id}/publication-review` explicitly loads read-only cycle and
direct-dependency evidence. It distinguishes unknown dependency coverage, known
empty edges and missing targets; a closed dependency still has unverified Git
integration. Ownership remains unverified and `publicationAvailable` remains false.
This endpoint is not approval to open the issue. It rereads the source and policy
after the CLI checks and discards the review if either changed. Concurrent reviews
are bounded to one; busy/unavailable/changed-source requests return 503, missing
issues return 404 and unsupported query fields return 400. No background review,
repair, publication or collector-dirty signal is triggered. The browser does not
yet expose this review as a publication action.

The review's `candidate` separates `contentPolicyValid` from `draftLifecycleClear`.
The former validates raw content, type, priority, labels and configured routing
with the creation validator; malformed fields are not replaced by display
defaults. The latter flags non-blocked status, assignment, remaining deferral or
bound/uncertain execution metadata. Neither is a permission grant or proof that
external writers stopped. Review never reopens, unassigns or clears deferrals.

`reviewRevision` identifies the selected issue, complete observed issue source,
target/reasoning policy and cycle verdict. Changes to a prerequisite or another
issue therefore change this revision even when the selected issue's own revision
does not change. Export ordering alone does not. This is an evidence identity,
not a permission token, a cross-process lock or proof that the source cannot
change after the response.


## Committed history coverage

History uses the installed `bd --readonly history <id> --limit 1000 --json`
contract, checked at startup. The inspector loads history on demand and displays
meaningful field changes; raw **committed snapshots**, commit hashes and committer
information are kept under collapsed technical evidence. A committer is not
necessarily the person who edited a field. Current comments
retain their separately recorded author/time; current notes do not become dated
events. Missing/compacted history and uncommitted edits remain explicit gaps,
even when a page is empty or shorter than the limit.

One host-owned `bd --readonly vc status --json` probe accompanies each export
cycle to detect history revision/branch movement. It does **not** replace the
export or detect working-set edits. History caches include both canonical issue
and history revisions, coalesce concurrent requests, and hold at most 64 entries.
Revision checks around a cold history read reject a moving source. Identical
probes publish no history events and do not reload histories. The inspector
refreshes previously demanded activity on relevant changes; drafts remain intact.
A stale history probe disables loading without pretending current issue data
provides historical evidence. The timeline event model and run-local observation
events are still under construction.


## Timeline preview

Timeline is the default view; `?view=issues` and `?view=branches` retain the table
views. The embedded renderer uses native WebGL (no CDN/runtime package), with
triangle-mesh tubes, spherical comment markers, status diamonds, a perspective
grid and a server-clock-anchored NOW plane. Independent lanes are deliberately
**not** connected to a fabricated Git trunk. Lane colors identify issues, not
historical status. Closed does not imply merged.

Drag to orbit, Shift-drag/middle-drag to pan, wheel/two-pointer pinch to zoom.
Orbit/Pan, Fit view and Focus issue controls are explicit. With canvas focus,
arrows orbit, Shift-arrows pan, +/- zoom and F fits. 2D uses an orthographic
camera over the same source projection; unavailable/lost WebGL falls back to
Canvas 2D and the accessible event list. The same inspector selection works
across views. Selected events can stay in a dismissible pinned scene card.

Creation/comments/current closure fields retain recorded times; missing timestamps
remain unknown. Loading history in the inspector adds committed snapshot markers,
not asserted exact status/note-edit times. A current status marker says transition
time unknown. Loaded history is invalidated when its source revision changes or
becomes stale. The browser retains at most 64 demanded histories.

Playback/scrubbing is local presentation only. Historical status comes from the
last loaded recorded state (equal-time conflicts are unknown); other historical
fields are not backfilled from current data. Editing is disabled during playback.
Return to live rereads the selected current issue before enabling the composer.
The inspector provides a return-to-live editing button even in maximized view,
plus a retry if that read fails. Comment and label drafts are preserved.
Use **View comments** to open Activity: playback shows timestamped comments at
or before the playhead, while live mode includes all current comments.
Camera and playback do not run Beads/Git commands. Updates continue to arrive
during playback, with an issue-update count rather than a forced viewport jump.

The transport includes local date/timezone, playback speed, minimap, scrubber,
explicit range inputs and fullscreen. Range/playhead links use UTC ISO timestamps.
Filters/search operate on loaded data only. Dense markers cluster within each
lane/time bucket; 120 lanes render per page and the event list shows at most 100
events per lane (narrow the range for older events). Lanes are ordered by when
their work starts, so moving the playhead forward only appends later work to the
end of the list: a lane already on the page stays there, and newly revealed work
appears on a later page rather than displacing history already drawn. The lane
count beside the pager grows as the playhead reveals more work.

The page is deliberately larger than the scene's vertical spread needs, because
that spread follows peak simultaneous work rather than the number of lanes: lanes
that do not overlap in time share an offset. A small page combined with
start ordering would crowd every drawn lane into the earliest part of the range
and leave the rest of the width looking empty. Branch paths are sampled by their
on-screen length, so a short episode does not carry the point count of one
spanning the whole range. Undated comments stay in the
inspector. This bounded preview is not the final 1,000-issue performance result.

Fit/focus and projection changes use interruptible 450 ms easing; dragging takes
precedence. System reduced motion is respected, with a persistent override and
persistent 3D/2D preference. Rendering stops when settled and in hidden tabs;
live NOW refreshes every 30 seconds. Camera redraws reuse uploaded geometry.
The complete reference-style scene, validated Git connectors, observation events,
full filters, camera persistence and complete motion/accessibility acceptance
remain under construction.


## Attention actions

`attention-request` requires an exact explanation in `text`: the server verifies
the stored comment before adding `abacus:needs-user-attention`. Plain
`attention-resolve` accepts optional response `text` and reuses the standalone
response-first/label-second helper. Comment failure prevents label changes.
Neither action changes status, assignment, target or worker ownership. There is
no implicit blocked transition or reopen.

Results include `recordedCommentId` when the explanation is verified. A failed
label step reports partial progress, retains the request for same-ID replay, and
offers an explicit **Review and finish label step only** operation. After review,
request completion uses `existingCommentId` (instead of `text`) to identify a
still-present explanation; resolution completion omits response text. No append
is blindly repeated, and no racing status/assignee is rolled back. Expired IDs
still require manual review. Attention actions share the same unauthenticated
trusted-network access as comments and content edits.

## Git history disclosure and coverage

Git history exposes commit authors/messages to all clients who can reach the
server, including on non-loopback listeners. The Branches inspector loads its
latest 100 commits only on explicit request. Full-tip/shallow-boundary/limit caches coalesce reads;
conditional responses use ETags. Refs are resolved before/after loading, and
moving/deleted refs fail instead of mixing tips. This is a read-only operation:
no fetch, pull, push, merge, reset or repair.

Reachability does not establish an issue's integration time. Shallow/missing
history and the configured count/output caps may leave gaps; a short response
does not claim complete history. The shared source probe fingerprints the Git shallow boundary. Deepening or
unshallowing invalidates history caches, HTTP ETags and loaded browser history
even when the branch tip does not move. Reads overlapping a detected boundary
change fail for retry rather than returning mixed history. The same boundary
identity protects comparison/patch caches and serialized comparison responses;
combined results must agree on both tips and their history boundary.

### Publication cost

Live issue/Git/history events do not eagerly rebuild the full HTTP snapshot.
Issue JSON is cached per issue and only changed issues are serialized again;
Git/history/health-only updates reuse it. The next snapshot reader materializes
one coherent aggregate with the current cursor, and concurrent readers share
those bytes. Building that response still copies its cached components: this is
not a claim of zero allocation for a changed snapshot or a completed scale benchmark.

### Live worker state

Integrated hosting displays current worker/supervisor activity, issue ID, branch,
execution-active flag, observed exit code and retry count directly from this run's
in-memory state. Bounded change hints trigger immutable publications, not CLI
polls; no-op changes retain the cached response. `/api/v1/runtime` supports ETags
and returns 503 without a connected runtime (including standalone mode). Runtime
SSE updates do not rebuild issue projections or serialize unchanged issues.

The worker list is explicitly current, even during historical timeline playback.
Raw status detail, command output, prompt, ticket title and run-location strings
are excluded from this projection. Safe process locations, pending controls and recovery alerts remain to be integrated.

Claim state separately reports the actual manual ClaimGate, schedule eligibility
and their conjunction. Allowance is not a promise of ready work or available
ownership. Manual changes publish through a bounded hint. With a configured
schedule, one shared in-memory check per second catches clock-only boundaries,
including minimum-remaining-window restrictions, without CLI queries or worker-row
rebuilds. Stable reasons avoid countdown-only events. Reading this state never
resumes claims or changes the schedule.

### Claim Pause and Resume

The integrated worker panel submits only `pause` and `resume` to
`POST /api/v1/runtime/actions`. It uses the same ClaimGate as TUI/stdio and workers;
Pause does not stop active work, and Resume never overrides a schedule or ownership
restriction. Controls require the current run session and expected manual-gate
state, JSON and the same-origin mutation header. Standalone cannot control runs.

Requests have a bounded 1,024-entry per-run retry ledger. Repeating the same ID and
payload returns the original outcome without applying it again; conflicting payloads
or another run's session are rejected. Entries are not evicted: if full, use TUI or
stdio for that run. An uncertain browser response offers same-request retry, not an
automatic new operation. The controls disable when the live connection drops, and
new server-side controls are rejected when run cleanup begins. Retained retry
results describe their original operation, not the latest gate state.

### Worker actions

After the run binds its existing worker controllers, each worker row offers Stop,
Restart and confirmed Clean Workspace. These submit to
`POST /api/v1/runtime/workers/actions` with this run's session, a request ID, exact
worker name and explicit confirmation for cleanup. No repository path or arbitrary
command is accepted. Supervisors cannot use this endpoint or clean the main checkout.

HTTP 202 means **accepted**, not finished. Completion is acknowledged by the worker
loop after stopping/cleanup; Restart completion means dispatch resumed, not that a
new harness is already running. Cleanup failure and unacknowledged worker exit are
reported as failed/unknown. An in-flight tracked action cannot be overwritten by
another HTTP, TUI or stdio request. Existing cleanup/ownership checks still apply.

A separate bounded 1,024-entry per-run ledger retains worker retry protection. A
same-ID/same-payload retry returns its current receipt status without reapplying it.
The runtime stream shows all pending actions and the latest 32 terminal outcomes;
older IDs remain retryable within that run. Unknown browser responses offer a
same-request retry, and buttons disable on disconnection. Browser visual review,
real cleanup acceptance and active-harness interruption testing remain pending.


### Supervisor Stop/Restart

Configured maintenance and continuation rows offer Stop/Restart, never main-checkout
Clean Workspace. The supervisor route `/api/v1/runtime/supervisors/actions` uses the
same session/request ID/command/worker/confirm body as worker controls, but accepts
only configured supervisors and Stop/Restart or confirmed Force Run. Worker and supervisor ledgers and
runtime outcome arrays are separate. Rejected cleanup/unknown targets never route
to a controller. Accepted retries do not reapply an action.

Stop completes only when the supervisor reaches its disabled boundary, after any
active harness cleanup. Restart completion means supervision and its existing
retry state have been reset, not that a harness started or repairs succeeded.
If cleanup or loop exit prevents acknowledgement, completion is unknown, never
inferred from display text.

Force Run adds `command: "force-run"`, `confirm: true`, and a nonempty `prompt`
(maximum 16,000 characters, no NUL). Prompts are literal supervisor instructions,
never shell command arguments, and do not appear in runtime snapshots or outcomes.
Existing supervisor policy may bypass automatic trigger/claim-gate eligibility;
checkout coordination and configured permissions remain unchanged. Browser
confirmation explains this. Stop/Restart remain usable while force work is pending.
A second dashboard force request is refused until that target's pending action
settles. Force outcomes distinguish completed verification, failed verification,
queued cancellation, policy deferral and unknown completion. Same-ID retries never
queue the prompt again; the bounded session ledger retains accepted IDs without
eviction. Do not assume a lost response means the prompt was not accepted.


### Issue status, attention and reasoning actions

The issue inspector offers explicit **Change status** (Open, In progress,
Blocked, Completed), **Request attention & block**, and **Resolve attention &
reopen** actions alongside plain attention requests and resolutions. The combined
attention actions append an optional/required exact comment first, then update
the attention label and status together; reopen also clears the assignee. A
failed comment prevents the following update. A partial result names any
verified stored comment so the operator can review the issue and retry only
the missing step, without appending it twice. Status changes do not start or
stop a worker, or prove a merge. General status and combined attention/status
changes warn before submission when they may affect assigned or reserved work;
they are not rejected for that reason. The verified response repeats the risk.
These changes can disrupt an agent or leave its workspace reserved, so review
worker/workspace state afterward. Standalone operators
must also stop competing writers and reconcile offline reservations.

**Set reasoning level** replaces only the three Abacus reasoning labels, leaving
ordinary labels untouched. Clearing the level is refused when project policy
requires one. It affects future routing, not a worker already running.

### Ordinary label edits

**Add/remove labels** is a separate inspector operation with checkbox menus:
add from unique ordinary labels on currently loaded project issues, or enable
custom mode to type new labels; remove from ordinary labels on the selected
issue. Edits retain their typed add/remove boxes. The overview description is
collapsed by default and can be expanded independently for each issue. HTTP
`labels` and `edit` actions accept `addLabels` and
`removeLabels` string arrays (at most 32 each,
100 characters per label). These are deltas: no `--set-labels` replacement, so
unrelated and concurrently added labels are not overwritten. Verification checks
requested additions/removals and preservation of previous unrelated labels; a
racing removal prevents a false completed result. Other issue fields are untouched
unless explicitly included in the same content edit.

The generic editor refuses `abacus:` and `gt:` labels, including attention/control
labels, plus duplicates, overlapping add/remove sets, leading/trailing whitespace,
control characters, commas and double quotes. CSV-sensitive characters are refused
because Beads label flags parse string slices. Use dedicated attention actions
instead of bypassing their explanation/verification flow. Label-only edits use the
same revision checks, actor attribution, pending drafts and retry ledger as content.
The CLI delta flags are documented in the [upstream Beads reference](https://github.com/gastownhall/beads/blob/main/docs/CLI_REFERENCE.md)
and were checked against the installed CLI help and a disposable real-CLI fixture.


The browser sends only content fields changed from the draft's baseline. Reviewing
a new revision does not silently resend untouched stale title/description/priority
values alongside a label delta. Verified success refreshes submitted/untouched
fields from the returned issue, but completing a comment preserves unsent content,
label and append-note drafts. Empty edit submissions make no HTTP mutation request.


### Current issue table

Issues has keyboard-operable sort headers for ID, title, displayed status, numeric
priority and assignee, with `aria-sort` announcements. Unknown values sort last;
ID breaks ties consistently. Pagination defaults to 50 rows, with 25/50/100 choices.
Filters reset the page, source changes clamp it to a valid page, and sorting or
paging never rereads Beads/Git. The selected inspector remains open when its issue
is outside the visible page/filter, with an explicit notice. Sort, direction, page
and page size are retained in the URL alongside issue selection. Search uses the shared loaded-text scope described below.


### Shared metadata filters

The view-only filter panel applies exact assignee and label matching, priority and
attention state to both Issues and the live timeline. Filters combine with the
existing status/search controls, reset table paging, and persist in shareable
`filter-*` URL parameters. Clearing them never changes dispatch configuration.
Priority and attention offer explicit Unknown choices.

During playback the status comes from the newest recorded change at the playhead,
while every other field comes from the newest recorded *snapshot* at or before it.
Those can differ: a closure is recorded from second-precision `closed_at` and often
lands a moment after the millisecond-precision snapshot that closed the issue. The
inspector names the snapshot time under **Fields recorded at**, and the coverage
line says when the status is newer than the fields.

Historical snapshots project assignee, priority, labels, type and declared target
only when the source records them. Recorded labels are not available from the
issue-history source at all, so they read as unknown during playback. Missing/null labels are unknown, not evidence
that attention was clear; an explicit empty label list is distinct. The installed
history fixture records priority/type but omits related labels and assignee. Filters
use loaded snapshots at the playhead, never today's values. Conflicting equal-time
snapshots make the affected fields unknown rather than selecting an arbitrary row.
The historical inspector shows last-recorded metadata with coverage caveats.

Exact type and declared-target filters are shared by both views. Declared target is
only the recorded `metadata.abacus_target` value—not a validated execution binding,
resolved configured default or proven Git destination. Missing declarations remain
unknown. Effective-policy/binding-aware target filtering remains unfinished.


### Loaded-text search coverage

Issues and the live timeline share ID/title and loaded note/comment matching.
Recorded note/comment text is searched only inside the selected timeline range;
historical playback further excludes text after the playhead. Current undated notes
and comments are searchable in live views only, without inventing historical events
or timestamps. Historical title matching uses recorded state, not today's title.

A visible coverage line reports the indexed range and number of issue histories
loaded, explicitly excluding older/unloaded history. Loading committed activity
refreshes table results as well as timeline results. Search itself never loads
history or invokes source commands. Branches continues to search actual local
branch names; unvalidated names are not invented as issue execution associations.

### Recorded relationships

The live issue inspector shows outgoing and incoming exported dependencies with
literal relation types and links to issues present in the current snapshot.
Missing targets remain visible as missing, not fabricated issues. Incoming means
another issue records this issue as its `depends_on_id`; outgoing means the selected
issue records the target. These links are not a dispatch-readiness calculation.
Only explicit, valid edges are projected; missing or inconsistent dependency data
is unknown, while an explicit empty array or zero dependency count is known empty.
Incoming coverage is limited to the exported issues and is marked incomplete when
any of them lack valid dependency data. Current relationships are not shown as
historical facts during playback. Relationship editing remains pending.

### Inspector navigation

Overview, Activity, and Git are separate keyboard-accessible panels. Use Left/Right
or Home/End while focused on a tab. The selected panel is retained across issue
selection and shared through the `inspector` URL parameter; changing panels does
not fetch sources or reset edit drafts. The editor remains available below the
panels in live mode. Activity contains current comments and explicitly loaded
committed snapshots; it is not a complete chronological edit log yet.

The header labels current status even during historical playback and provides
Copy ID (with a manual-copy fallback when clipboard access is unavailable).
Overview lists directly related in-progress/blocked issues from recorded edges,
not unrelated matches inferred from issue titles. Git supports explicitly loaded issue-binding evidence, with a route to repository
branch browsing. Verified detached-worker associations and full historical Git integration remain pending.


### Issue binding evidence

`GET /api/v1/issues/{id}/git` uses the current exported routing metadata and the
existing target-registry validator. It does not accept caller-supplied branch refs.
Missing bindings are unbound even when a conventionally named branch exists.
Invalid targets or policy-identity conflicts require review; missing branches do
not prove integration. Before returning a validated comparison, the server checks
that the recorded start commit exists and is an ancestor of the current issue tip,
then rechecks comparison tips/history and the target policy. Missing objects,
stale issue/Git sources and concurrent issue changes cannot produce a success
claim. A negative start-ancestry result is explicitly divergence-or-incomplete
history, not a definitive diagnosis in a shallow repository.

The Git inspector's **Load binding evidence** button shows the effective target,
recorded binding/start, current tips, triple-dot file summary and current ancestry
containment when validated. Containment has no claimed integration timestamp and
is not proof of live ownership. Loading is explicit; source/selection/playback
changes invalidate displayed evidence. No raw execution metadata or routing error
is added to issue snapshots. Source-backed timeline connectors still require further implementation.


The issue Git endpoint accepts optional `patch=true` and `history=true` flags
(no arbitrary refs or ranges). These details are loaded only on explicit request
and only after binding/start validation. Patch output uses the existing bounded
4 Mi-character triple-dot reader with external diff/text conversion disabled.
History returns at most the latest 100 commits reachable from the validated issue
tip; shared ancestors are included, so it is not an attribution of every commit to
that issue. The inspector displays history bounds, shallow coverage and clock-skew
warnings, commit parents, and literal commit/patch text. Each detail request
revalidates binding/policy/tips/history; source changes discard displayed details.
Loading another detail replaces the previous detail to keep the inspector bounded.


### Collected worktree status

Shared Git probes now inspect registered worktrees with read-only porcelain status,
including staged, unstaged and ordinary untracked changes while respecting Git
ignore rules. Bare/prunable registrations, inaccessible paths, foreign common-Git
directories and changing HEAD/branch observations are **unknown**, never clean.
The common directory is checked before and after status collection. Optional Git
locks remain disabled; probes do not refresh the index on disk.

Branches shows each registration's collected status. A validated issue binding
shows attached checkouts for its literal recorded branch; a mismatched collected
HEAD makes its dirty state unknown. This is a collected checkout association, not
proof of an active worker or lease. Detached worker association still requires
verified runtime/pool ownership evidence and is not inferred from matching HEADs.
Clean/dirty transitions publish Git changes; a dirty boolean is deliberately not
claimed as a content revision. Bounded content with shared live reconciliation is available below.


### Worktree content snapshots

Branches offers **Load worktree content snapshot**, followed by shared live
reconciliation while the worktree detail is visible. The
`GET /api/v1/worktrees/diff?id=<registered-id>` endpoint accepts only a registered
opaque worktree ID, not an arbitrary path or ref. It checks common repository,
registration and HEAD before/after reading, compares two consecutive bounded
reads, and refuses a changing observation. Snapshots are not atomic filesystem
transactions. Eight concurrent source reads are allowed; concurrent requests for
the same collected registration share a read, and disconnecting a caller does not
cancel it for others.

Staged, unstaged and untracked patches are separate, with external diff/textconv
disabled. Their combined limit is 4 Mi characters and at most 256 untracked files. Binary Git patch payloads participate in
the content revision, so repeated edits to an already-dirty tracked or untracked file change its
ETag even when HEAD, status and changed-path lists remain the same. Reversing a
staged edit in the working tree does not hide both changes as an empty HEAD diff.

Untracked paths come from Git's ignore-aware `ls-files` enumeration, not client
input. Binary payloads and symlink text participate in content identity; a symlink
is shown as its link target text rather than reading the target file. Intermediate
symlink directories and nested untracked repositories are refused. Changes to
ignored caches do not affect content identity. Reads exceeding a file-count or
output limit fail explicitly rather than silently fingerprinting partial content.

### Live worktree content

`GET /api/v1/worktrees/events?id=<registered-id>` subscribes to full latest-state
SSE updates. It shares one reconciliation read per visible worktree across all
clients, driven by the existing server Git collection interval—not browser timers.
Limits are eight distinct watched worktrees and 64 subscribers. Each subscriber
holds one latest-state slot; slow clients may skip intermediate snapshots but
never reconstruct an incomplete delta. Identical content produces no event.
Reconnecting gets a full latest snapshot, with no claim of an exhaustive edit log.

The browser clears content when a source read fails or the stream disconnects,
then restores it on recovery. Changing away from Branches, hiding the page or
closing it releases the watch; returning reconnects. The final subscriber's exit
removes server interest, so subsequent intervals do not read that worktree's
content. An already-running bounded read may finish. Server shutdown cancels the
HTTP stream. **Reconnect live content** restarts the subscription, not a separate
per-browser polling loop. Performance/subprocess budgets at scale still need
measurement; shared reconciliation is not a claim that those gates have passed.


### Idle worktree patch reuse

Visible worktrees still receive shared periodic identity checks, but unchanged
identities reuse an eight-entry patch cache rather than rerunning `git diff`.
Identity combines porcelain v2 index/blob/mode facts, raw hashes of changed regular
files, symlink text, attributes and effective Git configuration. It does not rely
on mtimes or the dirty boolean. Fingerprints are rechecked before returning cached
content; cold reads retain the consecutive-patch consistency checks.

Fingerprint work is bounded to 1,024 changed paths and 64 MiB of changed content
per pass; patch output remains bounded separately. Nested repositories/submodules
and externally clean/process-filtered changed files are reported unavailable,
not treated as safely cacheable: such filters can depend on arbitrary external
inputs. No raw Git configuration is returned to clients. Fingerprint bytes and
elapsed time are measured separately from patch subprocess counts. Broader idle
HTTP/Beads acceptance and scale/peak-memory measurements remain open.


### Read-only measurement counters

`GET /api/v1/diagnostics` returns process-local cumulative counters without
starting collection or rebuilding a snapshot: successful issue exports/bytes,
issue canonicalization/hash elapsed time, rebuilt issue projections, serialized
issues/aggregate snapshot builds, Git commands/diff commands, worktree fingerprint
bytes/stage time and active worktree topics/reads. Missing collector connections
are null. No raw issue content, config, commands or diagnostics are returned.
Counters are observational rather than an atomic cross-source transaction; stage
times are elapsed time (including CLI latency where applicable), not isolated CPU.
They are not pushed as data events, so measurement does not create idle churn.


### Timeline Git summaries

Loading validated evidence in an issue's Git inspector also annotates its live
lane card with the recorded branch, compared file count, additions/deletions and
current target containment. Hover the card for comparison basis and pinned tips;
the Git inspector retains full provenance. Binary files are counted without
invented line totals. These facts do not establish worker ownership or a dated
merge, and do not draw an unsupported historical integration connector. Verified current
containment is shown separately as a continuous curve returning near NOW to a central silver target rail, with
unknown integration time stated explicitly; it disappears during playback. The
rail represents current target association, not fabricated target commit history.

Evidence is retained for at most 64 issues, without caching patch bodies
in the timeline. Explicitly loaded commit events have a separate 16-issue,
100-commit-per-issue bound. Issue revision changes, Git updates, stale/disconnected sources,
new snapshots and failed explicit refreshes invalidate applicable evidence.
Historical playback never backfills current comparison facts. No extra source
query is triggered by camera motion or lane rendering.


Loading Git history also adds recorded commit markers and a Git commits event
filter. Each commit can be opened on the timeline from its inspector row. Commit
callouts show recorded author, object ID and parents, explaining that current
reachability can include shared ancestors and does not establish when work was
performed for this issue or when it integrated. Unlike current comparison facts,
commit timestamps can be shown in playback; they never supply Beads status.
Missing dates are not invented, duplicate commit IDs are collapsed, and source
invalidation removes loaded commit events as well as current lane summaries.


When the bounded loaded Git history includes the exact recorded execution start
commit, the live scene adds a start curve from its dated target-rail marker to the
issue lane. The marker identifies this as **commit time**, not branch creation or
claim time. No start curve is inferred from an issue's creation field, a missing
commit, an unmatched tip or a branch naming convention. Current binding-based
curves disappear in historical playback; independently recorded commit markers
remain available with their provenance caveats.

### Dedicated operational tabs

**Workers** contains live run controls and worker state; standalone mode explains
that no orchestrator is connected. **Worktrees** contains registered checkouts and
live uncommitted-content inspection, separate from **Branches**. Leaving Worktrees
pauses its live content subscription.

The timeline workspace scrolls vertically. Use **Ctrl/⌘+wheel** to zoom the camera;
ordinary wheel gestures scroll. **First event → now** in the toolbar sets the start to the earliest of all dated
entries in the current export and already-loaded history. It does not imply that
older unloaded history has been fetched.

On touch screens, swipe vertically over the timeline to scroll the page without
moving the camera. Horizontal drags remain available for camera interaction.

### Lane captions

Lane captions report what is happening at the playhead needle, not every lane in
the scene. With an issue selected, only that issue is captioned. With nothing
selected, every work episode open at the needle is captioned — all of them, so
parallel work stays legible instead of one arbitrary lane winning the space. When
no work is open at the needle, the most recently ended episode is captioned.

Every lane keeps its card in the accessible event list and in the DOM regardless,
so keyboard and screen-reader access to uncaptioned lanes is unaffected.

Playing the timeline does not change the selection: a pinned event stays in the
inspector, the callout and the URL, and reappears as the needle reaches it. A
caption holding keyboard focus stays readable even once the needle has moved past
that lane's work. **Return to live**, seeking and browser navigation do clear a
pinned event, because they change which events are reachable at all.

### Status-history work episodes

Timeline loads recorded status history automatically. Work starts at a recorded
`in_progress` state, blocked intervals stay on the episode, and closure returns
to the activity spine. A later restart creates a separate episode. If work moves
from blocked to Open and later resumes, the Open gap is dashed on the same visual
lane rather than making the issue fork from the project trunk again. These are
status curves, not assertions of Git integration. Unknown restart times are
explicitly marked rather than invented.

Recorded history for every issue is read in one request, so no lane is hidden
behind a batch that has not loaded. The coverage label reports how many issues
are loaded; **Retry timeline history** retries an unavailable read.

Beads keeps one row per issue per database commit, so an untouched issue repeats
its whole body once per commit. Repeated identical states are collapsed to the
earliest commit that records them — the earliest time the state is known to have
existed, never a guessed edit time. A state that is left and later returned to
stays two recorded changes. Recorded label history is not available from this
source, so historical labels read as unknown rather than as an empty list.

Sources that cannot answer a whole-project query fall back to reading one issue
at a time, in **Previous histories / Next histories** batches of 64 in stable
issue-ID order, with lane paging inside the current batch. That fallback
prioritizes likely matches for the selected time range: closures within the
range and currently working/blocked issues come first, followed by potentially
overlapping closed histories. Issues created after the range or closed before it
load later, not never. These are scheduling hints, not proof of historical
status; recorded history still controls the curves. Changing the range returns to
the first prioritized batch. Two reads remain in flight at most; changing range
does not restart useful in-flight reads.

**Live · last N hours** accepts fractional hours and follows now. Applying a
manual historical range freezes its bounds; **First event → now** clears the
rolling offset.

Drag across the small timeline map to select a time range: the highlighted span
becomes the new fixed range when released, in either direction with mouse or
touch. Press Escape before releasing to cancel. A normal click still seeks to
an event or time. The date/time form remains available for keyboard entry;
**First event → now** expands back out after selecting a narrower range.

**Time width** and **Vertical spacing** independently stretch the timeline's time
axis (0.25×–40×) and lane spacing (0.25×–40×) in both 2D and 3D. They change the view,
not the selected dates or playback time. Preferences are remembered in this
browser; **Reset stretch** restores both to 1×. **Fit view** fits the stretched
scene without resetting these settings. The sliders also support keyboard arrows.
Zooming out is bounded in proportion to the stretch, so a high-stretch **Fit view**
stays reachable by zoom and is remembered; lowering the stretch pulls a far camera
back to the smaller scene's limit. The WebGL depth range expands with the camera
and visible scene, so zooming out does not clip away distant paths.

Vertical placement follows concurrent work among the displayed episodes, not
issue IDs: one issue uses +X, two use +X/−X, and additional issues alternate
outwards. A work episode keeps the side **and** the distance it was first given
until it ends: it never switches sides and never slides inwards when a neighbour
finishes, so the only vertical movement on a branch is its own fork and return.
A finished inner branch therefore leaves a visible gap until a later episode
reuses that slot, which is why a busy range sits further from the spine. A lone
survivor can remain below the spine. Blocked work keeps its place until its work
episode ends.

Placement is computed from the episodes currently displayed, so changing the time
range, the filters or the lane page can place the same issue differently. It is
also per work episode, except Open pauses reserve the same lane across the gap:
an issue that closes and starts new work later is a new branch and may be placed
elsewhere. Line
thickness and round event-node proportions no longer stretch with the axes;
nodes are larger in both WebGL and fallback views. Existing saved cameras refit
once for this layout migration; subsequent camera preferences remain saved.

### Meaningful timeline events

Floating issue labels show titles only; IDs remain available in the inspector
and accessible control names. **Maximize view** expands the scene within its
panel, leaving camera, 2D/3D, stretch and other view controls above it. Time-range,
playback and issue-setting controls are temporarily hidden, not reset; the
inspector remains available. **Restore panel** or Escape restores the normal
layout. This does not enter browser fullscreen.

The timeline shows only **status changes, label changes, new comments, and note
changes** within recorded work episodes. Event filters use these four categories.
Issue-event nodes stay on the offset branch, including closure. The only spine
node is a first recorded open/blocked → in-progress transition; later resumes and
reopened episodes remain offset. An Open pause gets a dashed connector, not a
solid work segment. Same-time comments/notes/labels are clustered
separately from that initial entry. Branch joins remain at their recorded times
without placing other event nodes on the main line.
Clusters count those changes, not database snapshots; one recorded version may
contain several meaningful changes. Labels name additions/removals, comments show
their text/author, and **Show note changes** expands a before/after comparison.
Grouped-node floating popups also show up to five named comment previews, each
limited to 240 characters with an ellipsis. The preview list scrolls and reports
additional comments; the inspector keeps full text and all cluster members.
Selected-issue status annotations also include changes inside grouped nodes.
Closing changes receive placement priority, and labels try nearby positions
before collision filtering hides them. Offscreen events and event-kind filters
still apply; dense scenes retain a bounded annotation count.

Committed snapshots still reconstruct work curves and historical state, but
unchanged snapshots and unrelated title/assignee edits do not add event nodes.
The first available snapshot establishes a baseline, not a fabricated transition;
missing fields or conflicting equal-time records do not establish a change.
Snapshot-derived changes use **recorded at** timestamps: the exact edit time and
editor may be unknown. Commit hashes and committer information are tucked inside
**Technical evidence**. Raw snapshots remain available in Activity, and Git
commits remain in the Git inspector rather than the issue timeline.

The Activity tab likewise lists meaningful status, label and note changes instead
of repeated snapshot titles; comments remain in its Comments section. Raw records
and detailed coverage are available under the collapsed **Technical evidence ·
raw snapshots** disclosure. **Load older history** expands the comparison baseline
without duplicating entries or replacing already-loaded timeline history.

Selecting a closed issue checks its current Git binding/ancestry and shows the
result in Overview. **Git confirms integration** means its tip is contained in
the validated target now; it does not assign a merge time to the status closure.
If evidence changes, confirmation is cleared and **Check Git integration** can
refresh it. Status curves remain unchanged when Git proof is absent.

If an issue is closed now but its recorded history has no closure timestamp, the
scene stops at its last recorded working state and labels the end time unknown.
It does not invent a closure date or keep extending the issue to now. Likewise,
a currently in-progress issue with no recorded start gets an explicitly undated
current marker until status history supplies a start.
