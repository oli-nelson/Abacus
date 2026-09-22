# Web dashboard acceptance audit — open gates

This is a current-state gap audit against the **13 original criteria** in
[ABACUS_WEB_SERVER_SPEC.md](../ABACUS_WEB_SERVER_SPEC.md#8-acceptance-criteria-and-verification).
It is not a reduced definition of done. Passing a targeted test establishes only
its tested behavior, not the entire criterion. No criterion is marked closed
without an end-to-end evidence review of every clause.

## User-directed completion scope — 2026-09-22

Ollie explicitly paused further issue-management expansion: leave the existing
features stable, then finish timeline/inspector work, followed by a full visual
and animation pass against the reference. Completion for this goal follows that
revised scope; the original matrix below remains a record of specification gaps,
not a claim that deferred actions have shipped.

- Freeze issue-management capabilities after verifying the changes in flight.
  Publication and unsupported ownership-sensitive mutations remain unavailable.
- Finish timeline/inspector behavior, source-backed facts/provenance, navigation,
  and reference-scene presentation without inventing integrations or ownership.
- Before starting visual/animation changes, reopen the supplied reference image
  and compare the current UI directly against it. Match its luminous lines, smooth
  curves, speech-bubble callouts, depth, typography and composition as closely as
  possible without inventing source facts.
- Then review and polish the complete visual and animation experience, including
  responsive layouts, dense scenes, accessibility, reduced motion and interrupts.
- Verify the resulting experience and regressions before declaring completion.
  Deferred issue actions must remain clearly documented, not silently marked done.

| # | Current evidence | Remaining proof or implementation |
|---|---|---|
| 1 | CLI/config, host, session and run end-to-end tests cover opt-in hosting, binding and startup ordering. | Finish the clause-by-clause option/precedence/disabled-mode and standalone no-ownership audit, including current native packages. |
| 2 | Reference CDP and inspected screenshot show three recorded-start curves, one verified-current-containment return, visible lane cards, selected captions and author-first speech bubble. Inspector is resizable with keyboard controls and compact full-event evidence. | Historical integration dates/merge summaries remain unimplemented; verify every label/count/minimap against real source history rather than only the synthetic reference fixture. |
| 3 | Timeline, table, search and inspector CDP scripts cover navigation, filters, playback, drafts, keyboard tabs and narrow layouts. Event-link tests cover reload and actual Back/Forward; camera preference and inspector resize tests cover persistence and no-source-request interactions. | Complete no-source-call/no-mutation review for all interactions and deep links; full keyboard/list content parity. |
| 4 | `IssueActions` supports comments, content/label edits and plain attention request/resolution with revision checks and retry/partial outcomes. Real Beads label and draft-only HTTP creation fixtures exist, plus a browser draft composer with policy review and same-request recovery. | Reviewed publication, status, assignment/claim, target, dependency editing and resolve/reopen; reservation/ownership fencing and actual CLI acceptance for every action. |
| 5 | Export-based collection and fixtures detect working-set/comment/dependency changes, including unchanged source HEAD. | Shared-server writes without filesystem events and configured-interval detection evidence on the current implementation; fuller observation/history reconciliation. |
| 6 | [Recorded standalone HTTP/Beads/Git idle run](measurements/dashboard-http-idle.json): 60.02s, ten clients/20 streams, visible dirty content, zero data events, issue rebuilds/serializations, snapshot builds or repeat history/diff queries. Export/hash/fingerprint/CLI costs recorded separately. | Repeat the combined scenario with unchanged integrated runtime, then prove changed runtime updates independently. Isolated CPU/peak memory and scale costs remain unmeasured; this standalone record does not close every clause. |
| 7 | Real Git tests cover tip invalidation, packing/deletion, shallow deepening and repeated tracked/untracked text/binary content revisions. Shared worktree subscriptions deliver content changes. | Complete the matrix for new registrations, force-push, external edits and issue-dependent projection isolation together; verified detached-worker associations are still missing. |
| 8 | Bounded caches, concurrent read sharing, latest-state worktree queues, failure recovery, input/output limits and stream tests exist. | Full cancellation/eviction/restart/gap/slow-client audit across every detail endpoint; source watcher hints/overflow behavior and current cold-query measurements. |
| 9 | Historical unknowns, equal-time conflicts, clock skew, binding validation and current containment are explicit; missing/deleted branches do not imply integration. | Complete fast-forward/squash/reopen/closed-unmerged provenance matrix and run-local/history reconciliation. Current-containment connectors use validated Git evidence and are excluded from playback; historical integration topology remains unfinished. |
| 10 | In-process runtime controls, claim gates, supervisor/worker receipts, run stop, mutation drain and finite-exit tests exist. | Real-harness browser acceptance alongside TUI/stdio/schedules, pool ownership and failure isolation; full final-event/shutdown/coexistence matrix. |
| 11 | HTTP Host/Origin/body checks, literal CLI arguments and browser text rendering have tests; worktree paths are registered IDs and untracked symlink target contents are not exposed. | Reaudit the complete expanded read/write surface, remote trusted-client edits and all secret-bearing diagnostics after remaining actions land. |
| 12 | [Waiting-marker continuous-motion scene](measurements/dashboard-scene-waiting-markers.json): 1,000 issues/10,000 comments, one rendered browser plus nine stream-only clients; 1,100ms warm activation, 49.24 FPS software WebGL with 394 changed poses/394 frames, zero motion fetches/buffer uploads and zero settled frames over one second. Machine and source hashes recorded. | Full real-source/ten-client benchmark with cold CLI cost, latency, peak memory, subprocess counts and detection mode; native-GPU review. Screen-space label culling now prevents overlaps in the fitted benchmark screenshot; broader visual/motion review remains open. This synthetic rendering slice does not close the criterion. |
| 13 | Timeline and editor browser checks cover some camera/playback, draft and interruption behavior. | Complete motion/reduced-motion/focus/burst/hidden-resume review on the benchmark fixture, including future integration transitions and no replay after identical reconnects. |

## Immediate next implementation priority

Issue-management expansion remains frozen at Ollie's direction; do not resume
publication/ownership-sensitive writes merely because they remain in the original
matrix. The next work is the remaining timeline/inspector source-provenance and
complete visual/motion acceptance review, followed by broad regressions. The
reference, event-link, camera, resize and dense-scene checks are bounded evidence,
not substitutes for source-backed history or the full interaction matrix.

The [real draft CLI contract](measurements/dashboard-draft-cli.json) remains
recorded evidence for Beads 1.2.2, not a claim of dashboard publication support.

## Other delivery requirements

The numbered criteria do not erase the rest of the specification: complete the
required action table, inspector/provenance fields, source-backed topology,
reference styling and interaction details. Native packaging/release checks and a
fresh full regression remain final gates. See the chronological evidence and
known limitations in [dashboard-implementation.md](dashboard-implementation.md).

## Motion review checkpoint — 2026-09-22

Current-source combined run passed `playback-filter`, `motion-interrupt`,
`reference`, and `timeline` CDP checks, each with a fresh fixture/profile, plus
27 pure model tests. This verifies the current filtered-count correction together
with the reference scene, not only before that final change.

| Motion requirement | Current evidence / remaining work |
|---|---|
| Camera fit/focus/projection transition | 450ms finite easing; timeline and interruption browser tests cover exact endpoints and reduced-motion interruption. |
| Hidden-view playback and return | Browser checks prove saved stationary playhead, accurate transport and no camera replay. A two-tab Chrome test also verifies actual `document.hidden` changes, stopped frames, saved playback position and settled return; OS process suspension is not tested. |
| Pinned callout entrance | 240ms finite effect; tests cover no repeat on identical selection, source refresh, cancellation and reduced motion. |
| Buttons and focus | Shared 140ms CSS tokens, visible focus, reduced-motion suppression. |
| Inspector tabs and panels | 240ms panel entrance and 180ms gliding indicator now use shared tokens; inspector-motion CDP verifies rapid-switch cancellation, same-tab no replay, reduced motion, drafts and zero source requests. Extended browser evidence also covers system reduced-motion changes, explicit full-motion override and Branches-view cancellation without replay. A two-tab Chrome visibility test verifies cancellation while the document is genuinely hidden and no replay on return. |
| New scene events and changed status | 350ms live-event bead fades now have browser evidence for no per-frame uploads/queries, no duplicate/snapshot replay and reduced-motion suppression. Current-status marker color blends now have focused browser evidence; 350ms live new-lane path/halo/bead/card entrances now have focused browser evidence for finite animation, idle, no per-frame uploads/queries, repeated/snapshot suppression and reduced motion. Active reduced-motion cancellation, glow-off cleanup and actual WebGL-loss fallback now also pass. A 1,000-lane burst verifies bounded effects/idle/no paging replay; actual SSE reconnect verifies missed-lane baseline suppression. Two-tab Chrome verification also covers actual hidden-tab cancellation, arrivals received while hidden, retained data and zero replay on return. |
| Verified-current connector arrival | 350ms newly verified false→true containment fades only the return segment/endpoint. Focused browser evidence covers unchanged/first-load baseline suppression and no frame uploads/queries. Synthetic SSE Git invalidation and active reduced-motion cancellation also pass. Changed-branch and actual SSE reconnect baseline suppression also pass. Two-tab Chrome verification also covers actual hidden-tab interruption, released solid/halo ranges and retained authoritative containment without replay on return. |
| Filtering/clustering transitions | 180ms event-kind/shared-filter fades preserve fixed lane slots and timestamps. Browser checks cover rapid replacement, identical no-op, explicit/system reduced motion, full override and view-switch cancellation without replay. Live cluster-member arrivals now fade from 55% opacity with focused no-replay/reduced-motion/geometry-reuse evidence. Range-driven split/merge now uses a 180ms fade with fixed authoritative time anchors; Chrome evidence verifies actual grouped→split→grouped markers, retained individual-event selection and accessible members, identical-range suppression and reduced motion. Broader cross-feature review remains open. |
| Idle and continuous camera motion | Strongened synthetic benchmark verifies changed poses, no motion queries/uploads and settled idle; not full real-source/multi-rendered-client acceptance. |

These explicit gaps are why the full visual/animation pass is not yet complete;
a green browser regression alone does not close the motion requirements.
