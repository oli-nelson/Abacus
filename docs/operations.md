# Operating Abacus

This guide explains what you see after Abacus starts, how to steer a running
pool, and what happens when an agent or external tool fails.

## Before launch

Use preflight-only mode when you want the exact run configuration validated
without changing a workspace or claiming an issue. Run repository commands
inside the main checkout, or pass `--repo <main-checkout>` as below:

```sh
abacus preflight --repo /work/main-repo \
  --mode codex \
  --tmux-session work \
  --model gpt-5.6-terra \
  -a alice /work/repo-a
```

Preflight validates the selected main checkout, target and reasoning configuration, local
target refs, selected harness, Git workspaces, Beads projects,
`no-git-ops`, Dolt identity, server-address syntax when applicable, and the
required tmux executable when applicable. It does not clean workspaces, claim
issues, create panes, sessions, or windows, or print a run summary. Missing tmux
targets are created by `run` only after preflight succeeds.

When `.abacus/reasoning.json` enables `enforceLabels`, preflight also requires
runtime model mappings for all of `high`, `medium`, and `low`. This prevents a
valid ticket from being blocked because the operator started Abacus with an
incomplete mapping table.

`abacus health` answers the broader question “is this repository generally
ready?”; `preflight` answers “would this exact agent configuration pass
preflight?”

## Event logs and agent control

Add `--event-log <path>` to record structured JSONL activity alongside the TUI.
Use `--stdio` for event-only stdout and JSONL commands on stdin; optionally add
`--start-paused` to wait for a controller before fresh claims. See the
[event and stdio protocol](events-and-stdio.md) for commands and examples.

## The live dashboard

Interactive runs begin with a short animated ASCII entrance. Press any key to
skip it or pass `--no-intro`. No animation or TUI is shown for redirected
stdin/stdout/stderr, `TERM=dumb`, verbose, preflight, or stdio modes.


An interactive terminal shows one row per configured agent. Rows move through:

| State | Meaning |
| --- | --- |
| `STARTING` | The loop is entering service. |
| `PAUSED` | New claims are disabled at the claim boundary. |
| `WAITING` / `IDLE` | The agent is looking for eligible work or sleeping between polls. |
| `SYNCING` | Beads/Dolt state is being synchronized. |
| `PREPARING` | The issue branch and launch inputs are being prepared. |
| `WORKING` | The selected coding agent is running. |
| `FINALIZING` | Abacus is reading the terminal issue state and cleaning the host. |
| `RECOVERING` / `RETRYING` | A failed operation is being retried safely. |
| `STOPPED` | This loop has ended. |

Active rows include the issue ID and title, time in the current state, pane or
process location, retry count, and most recently observed exit code. Warnings
remain visible, while idle polling is visually distinct from failure retries.

Add `--start-paused` to start with claims disabled and the header showing
**CLAIMS PAUSED** immediately. Press **Shift-Tab** to pause or resume new claims
for every agent. Active work continues; an agent pauses only when it next reaches the claim boundary.

Use the **Up** and **Down** arrows to select a row. Press **Enter** on an agent
to open its action panel:

- **Stop Agent** interrupts its pane or process and parks that loop. An active
  ticket remains `in_progress` and reserved for the same agent, and workspace
  changes remain untouched.
- **Restart Agent** restarts an active agent or resumes a parked one. A ticket
  retained by Stop is revalidated for ownership, target, binding, and history
  before relaunch in the same workspace.
- **Clean Agent Workspace** asks for a second confirmation, stops the agent,
  safely reopens and releases any active ticket, runs `git reset --hard` and
  `git clean -fd`, and leaves the agent parked. Choose Restart when the clean
  workspace should return to service.

Escape closes the action panel. Clean removes tracked modifications and
untracked files and directories permanently; ignored files are unaffected
because Abacus uses `git clean -fd` exactly. A cleanup failure remains visible
as a persistent alert.

The same selection continues through the latest-comments rows. Press **Enter**
on a comment to open its complete text with the issue title, author, and
timestamp. The detail view wraps without truncating the message. Use **Up**,
**Down**, **Page Up**, or **Page Down** to scroll a long comment, then press
**Escape** to return to the dashboard.

### Latest comments

The dashboard ends with the latest eight Beads comments by default. Each entry
shows an issue ID, truncated title, author, and up to two wrapped message lines.
Header colors provide quick attribution:

- red — issue carries `abacus:needs-user-attention`;
- green — author matches a configured agent;
- cyan — unrecognized author.

Change the count with `--latest-comments <1-100>`. The comment snapshot is
read-only. If refresh fails, Abacus keeps the previous snapshot and emits a
deduplicated warning.

### Logs instead of a dashboard

Use `--verbose` (also `-v`) for timestamped state transitions,
warnings, alerts, and every external command:

```sh
abacus run --verbose [other run options]
```

When standard error is redirected without verbose mode, Abacus automatically
uses compact state-transition lines instead of terminal control sequences. Set
`NO_COLOR=1` to disable color while keeping the live layout.

## User-attention workflow

An agent can add `abacus:needs-user-attention` when it needs awareness, a
decision, or outside action. Abacus keeps matching IDs and titles in a persistent
dashboard alert, including for closed issues, until the label is removed.

List the IDs from a shell inside the main checkout, or add `--repo <main-checkout>`:

```sh
abacus attention list
```

Respond durably, clear the label, and optionally return the issue to the ready
queue:

```sh
abacus attention resolve ab-123 --message "Approved option A" --reopen
```

The response is written as a Beads comment before the label is removed. If the
comment fails, the label stays in place so the request is not silently lost.

## Desktop notifications

Notifications are disabled by default and generated by Abacus itself, giving
every harness the same behavior.

```sh
abacus run --notify attention --notify-sound [other run options]
```

| Mode | Reports |
| --- | --- |
| `off` | Nothing |
| `attention` | Newly observed attention issues, blocked tickets, and persistent recovery failures |
| `all` | Everything above, plus all ticket outcomes and the final summary |

With sound enabled, closed tickets and fully successful runs use a positive
sound. Attention, persistent failures, reopened/blocked/interrupted tickets, and
summaries containing them use a negative sound.

On macOS, Abacus invokes `/usr/bin/osascript`. On Linux, it uses `notify-send`
when available. Delivery is best effort, may depend on the active desktop and
sound theme, and never changes ticket or process outcomes. A terminal bell is a
fallback only when sound was requested.

## Claim and branch lifecycle

Before agents start, Abacus acquires exclusive per-workspace ownership and
establishes a Beads baseline:

- one agent with a Dolt remote: pull, then record the current full Dolt commit;
- multiple agents on shared server storage: record the live commit without a
  pull.

Each loop then:

1. Inspects its assigned workspace. A dirty `abacus/<issue-id>` branch is preserved and resumes that exact open issue; any unsafe dirty state stops that agent with a persistent alert.
2. Pulls before the claim in single-agent remote mode.
3. Selects eligible ready work and claims it atomically as `BEADS_ACTOR`.
4. Resolves the explicit or default target, revalidates ownership, and records/reads
   back the execution binding. Synchronizes Beads when configured before touching Git.
5. Creates `abacus/<issue-id>` at its recorded target commit or reuses a matching
   bound branch after checking history; unbound legacy branches require adoption.
6. Starts the harness after verifying a normal claim is clean, or directly in the preserved workspace for an interrupted issue recovery.

Interrupted-workspace recovery takes precedence over the ready queue and ignores
dispatch filters, but not target, binding, history, or ownership checks. Abacus never automatically resets or cleans a dirty workspace.
If the issue is no longer open, belongs to another agent, or cannot be claimed,
the files remain in place and that agent stops for operator attention.

On multi-agent startup, clean workspaces wait at a shared recovery barrier.
Abacus releases normal ready lookup only after every configured workspace has
been inspected and every resumable dirty workspace has claimed its exact issue.
An unsafe dirty workspace contributes to the barrier before its agent parks, so
it does not prevent the remaining agents from starting.

Abacus never dispatches a candidate with an unclosed direct child and always
excludes the `gt:slot` coordination bead. Optional filters and newest-comment
tie-breaking are described in the [CLI reference](cli-reference.md#dispatch-and-supervision).

The coding agent—not Abacus—implements, verifies, commits, performs the
repository's serialized merge process, and chooses the terminal Beads status:

- `closed` — work completed and merged;
- `open` — work should be retried;
- `blocked` — outside help is required.

Changing the issue away from `in_progress` tells Abacus the session may end.

## Target validation and repair

With `enforceTargetBranch: false`, missing target metadata uses `defaultTarget`
(`main` unless configured otherwise). Enforced omissions, malformed explicit
targets, and binding conflicts are atomically claimed only to block the ticket,
clear its assignee, and attach `abacus:needs-user-attention` with a precise reason.
No coding agent launches. A validation claim consumes the agent's one claim in
`--once`; continuous/drain may continue after successful quarantine.

Stop dispatch and active work before repairs. From the selected main checkout:

```sh
abacus targets check <issue-id>
abacus targets set <reviewed-branch> <issue-id>
abacus targets check <issue-id>
# Only after review and successful validation:
abacus attention resolve <issue-id> --message "Target corrected" --reopen
```

The setter preserves lifecycle and attention state. Legacy branch adoption and
changed bindings need explicit review; see [target recovery](targets.md).

## Failure and recovery

Abacus watches the Beads status and the hosted process together.

### Unexpected agent exit

If the process exits while the issue is still `in_progress`, Abacus:

1. logs the exit code;
2. reopens and unassigns the issue with a reason;
3. reads the issue back to verify recovery;
4. pushes Beads/Dolt when a remote is configured; and
5. returns to the loop only after recovery and synchronization succeed.

It never counts an unexpected exit as success.

### Ticket timeout

`--ticket-timeout 30s`, `15m`, or `2h` starts its clock after the agent host
starts. At the limit, Abacus stops the host and reuses the same verified recovery
path if the ticket remains `in_progress`. A terminal ticket update racing with
the timeout is preserved.

### Temporary read failures

Temporary `bd show` failures move the row to `RECOVERING`; the agent remains
supervised while Abacus retries with repeated-warning suppression. If the agent
exits while status remains unreadable, Abacus does not guess and does not claim
more work for that loop. It stops the loop and raises a persistent alert.

### Exhausted recovery

Failed reopen or Dolt-push retries stop the affected agent and remain visible as
an attention alert. Finite runs (`--once` and `--drain`) return nonzero rather
than retrying orchestration failures forever.

## Clean shutdown

Press `Ctrl-C` in the Abacus terminal. Abacus:

1. cancels all loops;
2. interrupts every active pane or direct child;
3. rechecks each issue;
4. attempts to reopen anything still `in_progress` with a shutdown note;
5. performs configured Dolt pushes with bounded retries; and
6. leaves managed tmux panes dead for reuse and removes temporary files.

Tmux cleanup is deliberately best effort. Abacus sends Ctrl-C to the recorded
pane, waits briefly, and force-respawns a harmless exiting command when necessary
so the pane becomes reusable. It never targets an unrecorded pane. An explicitly
named or pre-existing session is never killed; an implicit session created by the
current invocation is removed unless `--disown-tmux-session` was supplied.

The final summary includes elapsed time, the initial full Dolt commit, and
closed, reopened, blocked, and interrupted counts for each agent. The initial
commit is a diagnostic baseline, not a checkpoint created by Abacus; resetting a
shared database to it could discard other writers' later changes.

If a database-wide rollback is genuinely required, follow the quiesce, backup,
safety-tag, reset, and verification procedure in
[Managing Shared Dolt for Abacus](shared-dolt.md#roll-back-to-the-commit-reported-by-abacus).

## Routine operator commands

```sh
# Inspect repository readiness.
abacus health

# See ready or active Beads work.
bd ready --json
bd list --all
bd show <issue-id> --json

# Inspect hosted panes.
tmux list-panes -t <session>

# Remove stale local issue branches after tickets close.
abacus branches prune
```

For command syntax, see the [CLI reference](cli-reference.md). For responsibility
boundaries, see [Architecture and boundaries](architecture.md).
