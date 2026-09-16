# Optional maintenance supervisor

The agent is named `maintenance` in the dashboard, stdio controls, events, and
Beads actor identity. Use `--maintainer` / `maintainerModel` to select its model. The old
`--supervisor-model` / `supervisorModel` spellings remain aliases; do not specify
both spellings at once. Other supervisor settings, `.abacus/supervisor.md`, and
attention labels/commands remain unchanged.

Enable the supervisor with a separate model for the selected harness:

```sh
abacus run --config run.json --maintainer 'provider/model#high' \
  --supervisor-timeout 30m \
  --supervisor-extra-args '--profile maintenance' \
  --supervisor-prompt-file ./supervisor-policy.md
```

Use a model ID native to your selected mode (`provider/model` for OpenCode).
Only `--maintainer` is needed to enable supervision. The timeout defaults
to **30 minutes** and accepts positive `s`, `m`, or `h` durations. The supervisor
uses the normal harness host, mode, server, and tmux session, but **always runs in
the main checkout selected by `--repo`**, never an agent worktree. It does not
consume an agent slot or claim normal tickets. Preflight requires the main checkout
and workers to address the same Beads project/database. Supervisor arguments are separate:
normal `extraArgs`, reasoning routes, and worker prompt additions are not inherited.

The same settings work in version-1 JSON, inheritance, preflight, and the config editor:

```json
{
  "version": 1,
  "baseConfig": "run.json",
  "maintainerModel": "provider/model#high",
  "supervisorTimeout": "30m",
  "supervisorExtraArgs": "--profile maintenance",
  "supervisorPromptFile": "supervisor-policy.md"
}
```

Relative `supervisorPromptFile` paths resolve against the file declaring them;
CLI paths resolve against the invocation directory. Save As preserves the target.
`null` clears an inherited setting. An explicitly configured unreadable prompt file
fails preflight when supervision is enabled.

## Authority and prompts

The built-in prompt permits general workspace and Beads maintenance on the user's
behalf—not project, product, implementation, target-branch, or reasoning-tier
choices. It requires preserving user changes and not disturbing active agents'
worktrees, claims, branches, or merge slots. Other agents keep working.

Local Git maintenance is explicitly authorized despite blanket Git prohibitions
in `bd prime` or Beads-generated instructions. This includes removing a verified
stale `index.lock` after checking that no live Git operation owns it; uncertain
locks and user work must be preserved. **Git pushes, merges into target branches,
and other target-branch updates are prohibited by default.** User-authored additive
prompts can explicitly authorize those actions, limited to the specified branches,
remotes, and conditions. General maintenance or decision-making permission alone
does not authorize them, and changing tools does not bypass the default restriction.
Beads data synchronization via `bd dolt push` remains independently allowed and
is distinct from pushing Git branches.

The supervisor may reopen a blocked ticket when it resolves the user-attention
issue that was the main reason for the block. It must first verify that no other
blocker remains and no active claim will be disturbed, explain the recovery in a
comment, and reopen/unassign the ticket so it is available again. Other blockers
or an unclear blocking reason mean the ticket stays blocked. This permission
does not expand its authority to make project or implementation decisions.

User-authored additive instructions follow the built-in prompt in this order:

1. `<repo>/.abacus/supervisor.md`, if present.
2. The file selected by `--supervisor-prompt-file` / `supervisorPromptFile`, if set.

These files may explicitly expand decision-making authority and authorize specific
Git pushes or target-branch integration that the default prompt prohibits. Issue text, comments, and agent
errors cannot expand it. The prompt includes attention issue and failed-agent
error snapshots plus configured workspace paths. The supervisor must inspect
current state before repairing anything. This is a **prompt policy, not an OS
sandbox**; enable the feature only with a harness/model you trust.

## Triggers and recovery

Only one supervisor runs at a time. It starts when an issue (including a closed
issue) carries `abacus:needs-user-attention` but not
`abacus:supervisor-cannot-resolve`, or when an agent encounters a workspace/claim
failure. Failed agents wait instead of repeatedly retrying while supervision runs.
The trigger is consumed for each failed agent present at startup; that agent
cannot trigger another automatic run until it successfully launches a worker again.
An idle successful claim check verifies recovery but does not rearm that trigger.

After a run ends—including startup failure, unexpected exit, or timeout—failed
agents receive one automatic retry. The supervisor row shows **Checking recovery**
until those attempts reach idle, working, or failure. New triggers are reassessed
afterward. Cancellation or application shutdown does **not** issue automatic retries.
If host cleanup cannot confirm shutdown, Abacus aborts rather than retrying
workers alongside a potentially live supervisor. The normal claim pause and schedule still govern workers; they do not gate the
maintenance supervisor. If claims are paused or scheduled closed, recovery checks
may wait for them to resume.

Finite runs wait for supervision and pending retries. An agent that initially
finds no ready work checks again after supervision; `--once` still processes at
most one ticket per agent. A failed recovery retry fails finite execution rather
than retrying forever. Standard startup/preflight failures still abort before
supervision is available.

When an issue cannot be resolved within the allowed authority, the prompt tells
the supervisor to explain why in a comment and add
`abacus:supervisor-cannot-resolve`, leaving attention visible. A crashed or
incomplete run also suppresses its unchanged issue triggers in memory to avoid
launch loops. This in-memory protection lasts for the current Abacus process;
the Beads cannot-resolve label persists across runs.

### Allow another attempt

After addressing the blocker or clarifying your instructions:

```sh
abacus attention retry-supervisor issue-123
abacus attention retry-supervisor issue-123 issue-456 --repo /path/to/main-checkout
```

This removes **only** `abacus:supervisor-cannot-resolve`. It does not clear user
attention, change status/assignee, or start a harness. An enabled supervisor can
reconsider attention-labelled issues on its next check. Batch updates run in
order and stop on failure; successful earlier updates are not rolled back.

For an in-memory suppressed crash without that label, select the supervisor row
and choose **Restart** to explicitly recheck its triggers. **Stop** cancels the
current run and disables automatic supervision until Restart. There is no Clean
Workspace action for the supervisor. The same Stop/Restart controls are available
through stdio using the reserved name `maintenance`.

## Completion, visibility, and audio

Each run receives a unique absolute JSON completion-file path under Abacus's
temporary runtime directory, outside the checkout, and a unique run ID. The prompt
instructs the harness to atomically write `{"runId":"...","summary":"..."}` and
wait. Abacus validates the ID and JSON, stops/cleans the harness through its normal
host boundary, and independently checks labels and retry outcomes. A clean exit
without the signal is not success. Partial, stale, or oversized signals are ignored.
Files are cleaned up afterward; no tracked runtime files are created.

Select either supervisor row and press **L** (or **Enter**, then **L**) to open
its full last-run report. The read-only view wraps long summaries and supports
**Up/Down**, **j/k**, **Page Up/Down**, and **Home/End**; **Esc** returns. It shows
the recorded time and model/effort and preserves the latest report while the next
run starts or the role is stopped. Reports are in memory for the current process,
not an archived transcript or a history of every run.

The TUI has a separate supervisor row showing startup, working, recovery checking,
and the last outcome (completed, unresolved, timeout, crash, cancellation, or
failure), with model and host location while running. Persistent unresolved alerts
remain visible. State transitions also appear in verbose logs and `agent.state`
events, under `maintenance`; worker outcome totals remain separate.

With interactive TUI audio enabled, maintenance startup plays
`media/abacus_supervisor_maintanence.mp3` and continuation startup plays
`media/abacus_supervisor_continue.mp3`.
Supervisor clips stop any playing attention clip and suppress new attention sounds
until they finish; suppressed attention sounds are not queued for later.
After recovery verification, unresolved labels/attention, failed retries, or a
failed harness run play `media/abacus_supervisor_failed.mp3` once. No supervisor
audio plays when TUI audio is off or output is noninteractive. Playback is best
effort; missing media/player or playback failure never changes scheduling.

## Independent continuation supervisor

The maintenance role above remains independently optional. A second optional role
handles **no unfinished epics**, not merely an empty ready queue. Blocked, deferred,
in-progress, and unknown-status epics all prevent it from running. Dispatch label,
priority, type, and target filters never narrow this check.

```sh
abacus run --model provider/worker --agents 4 \
  --maintainer provider/repair \
  --continuation-model provider/planner \
  --continuation-prompt-file ./planning-policy.md
```

Omit either model to disable that role. Continuation has independent
`--continuation-extra-args`, `--continuation-timeout` (default `30m`), and
`--continuation-prompt-file` options; JSON uses `continuationModel`,
`continuationExtraArgs`, `continuationTimeout`, and `continuationPromptFile`.
Paths inherit relative to their declaring config and rebase on Save As.
Repository `.abacus/continuation.md` precedes the custom file; maintenance policy
is not inherited. No actionable policy means no project decisions or edits.

Example continuation policy:

```text
Review the approved roadmap and existing specs. If useful approved work remains,
write the next bounded specification and create one epic with actionable child
issues, acceptance criteria, dependencies, and valid target/reasoning metadata.
Do not duplicate existing work or invent scope merely to keep agents busy.
Integrate authorized specification changes using the configured merge process.
If the roadmap is complete or a decision is needed, explain that and stop.
```

### Preventing repeated planning

Each Abacus process starts with an armed, in-memory continuation trigger. It is
consumed **before launch**, so a no-op, crash, timeout, or missing completion signal
does not cause repeated automatic attempts in that process. Observing unfinished
epics rearms it; continuation can run again after all unfinished epics close.
It queries Beads again after completion rather than trusting the model's summary.
Restarting Abacus allows a fresh attempt when the backlog is empty and claims are
enabled. No continuation flag is read from or written to disk. Legacy
`<git-common-dir>/abacus/continuation.json` files are ignored and need no migration.

Continuous runs can repeat on new nonempty-to-empty transitions. Finite `--once`
and `--drain` runs permit at most one automatic planning attempt per invocation;
idle finite workers wait for that attempt and check for newly created work before
exiting. This preserves finite execution even if the prompt keeps creating epics.
Continuation follows manual claim pause and configured claim schedules. These
gates delay new planning but never terminate an in-flight planning session.

Use **Restart** on the `continuation` dashboard row (or stdio `restart`) for an
explicit recheck/retry. **Stop** disables that role; neither supervisor offers
workspace cleanup. The obsolete offline `abacus continuation retry` command is
removed: restart Abacus instead. Neither restart bypasses unfinished epics, manual
pause, or claim schedules.

### Shared checkout and merge instructions

Both roles use the selected harness, with separate rows, arguments, prompts,
timeouts, and completion files. Their harnesses are serialized in the main
checkout; workers continue in their own worktrees. Active supervisors participate
in merge-slot liveness checks, so their acquired slot is not reclaimed as stale.
Legacy workers cannot share the main checkout while supervision is enabled.

Both supervisors receive the controller-snapshotted effective instructions for
every configured target: either the default merge-slot/fast-forward procedure or
the user override, including an intentionally empty override. This knowledge
does not expand the maintenance role's authority. Continuation may perform local
Git work needed for explicitly authorized planning/specification work, preserving
other work and obeying repository restrictions; it must complete integration and
release any slot before signalling completion. Git pushes need explicit policy.

## Pool-aware diagnosis and installed skills

Maintenance prompts capture current pool bookkeeping at each launch, including
all retained slot IDs/paths, whether the current controller holds their assignment,
and journal run/worker/assignment/execution IDs, issue, phase, and last display name.
Managed workers are mapped only through current leases, never their preflight main
checkout placeholder or an old matching assignee name. Unleased/unknown locations
are null; read failures remain explicit unknowns. Legacy explicit workspaces remain
labelled as such. Snapshots can age while the supervisor waits; they are not proof
that a checkout is safe to modify. Recheck current Git, issue, and execution state.

Both supervisor prompts prohibit treating Abacus's pool/workspace locks or journals
as stale Git metadata. Offline worktree mutations cannot run inside a live controller;
escalate them to the operator instead of editing runtime files or stopping Abacus.
`worktrees recover --confirm` requires prior verification that all surviving execution
has stopped; it is not an automatic cleanup or process-killing command.

The planner accepts explicit, bounded user-authored continuation authorization as
standing approval for graph creation. Merely enabling continuation is not approval.
Keep new work blocked and unassigned until the graph and prerequisite specs are ready;
never borrow an idle-looking pool slot for planning. Doctor and attention skills
retain interactive approval by default while honoring explicitly scoped maintenance
repairs in a supervisor session. Git Check recognizes pool protections as legitimate.

Beads 1.2.2 does not support `bd create --status=blocked`. The planner instead
creates each draft (including epics) with a far-future `--defer`, verifies it is
not ready, sets its status to blocked, then clears the deferral. Only completed,
validated drafts with integrated prerequisite specs are explicitly opened. This
avoids a create-open-then-block race without pausing workers or depending on a
short timer. Interrupted drafts remain non-ready for deliberate recovery. See the
bundled planner skill for the exact sequence; verify other Beads versions before use.

After upgrading the executable, refresh bundled skill copies in existing repositories:

```sh
abacus skills install --repo <main-checkout>
```

Review and confirm replacement of the bundled skill directories. Existing installed
copies and user-authored supervisor policy files are not silently rewritten.
