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

Interactive runs begin with a short animated ASCII entrance and a bundled mix
of the welcome track with the jingle at 15% gain so the voice remains prominent.
Audio is off by default; pass `--tui-audio` to enable it. Press any key to skip
the intro, or pass `--no-intro` to skip both animation and audio.
Audio playback is best effort through macOS `afplay`, or the first available
Linux player among `ffplay`, `mpv`, and SoX `play`. No animation, intro audio, or
TUI is shown for redirected stdin/stdout/stderr, `TERM=dumb`, verbose, preflight,
or stdio modes. When the animation finishes naturally, the mix finishes in the
background instead of being cut off; skipping stops it immediately.

`--tui-audio` also plays a bundled attention clip once whenever an issue newly
needs your attention, including issues that already carried the label when the
run started. The clip does not replay for an issue that still carries the label,
never restarts while it is still playing, and follows the flag in every run mode
rather than the intro's `--no-intro` gate. Playback is best effort, so a missing
player only means silence.


Wide terminals show agent, working, and attention counts in the header. The active
tab and selected row are highlighted; status and keyboard hints stay at the bottom
as you switch screens. Taller terminals add spacing between agent rows.

The dashboard has four screens: **1 Agents**, **2 Attention Center**,
**3 Latest Comments**, and **4 Settings**. Use **1–4** to jump directly,
**Tab** or **Right** to cycle forward, and **Left** to cycle backward.
The active tab is bracketed. An inactive tab's **!n** badge counts new or changed
current entries since you last viewed that screen, even with `NO_COLOR=1`.
Repeated polls and elapsed-time ticks do not count as new information. Opening a
screen acknowledges its snapshot; it does **not** resolve attention issues.
Updates received behind an open action panel or comment detail remain unread
until you close the panel. Badges are run-local and disappear when entries are
removed or notices expire. Settings starts without a badge.

Only the selected screen uses the content area. **Page Up/Down** scroll it;
**Up/Down** or **j/k** select rows on Agents and Latest Comments, and scroll
Attention Center and Settings. Each screen retains its own position. Navigation
and claim status stay visible on short terminals; overflow no longer pushes
other sections off-screen. Close a panel with **Escape** before switching screens.

Agents is the initial screen and shows one row per configured agent. Rows move through:

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

Each agent's header contains its name and active issue ID/title. Status and
progress appear below, without repeating the ticket label; long titles wrap. An
agent with no ticket has no title to show, so its name, state, and progress share
one line instead of leaving a blank row.
Active rows also include time in the current state, pane or
process location, retry count, and most recently observed exit code.

Attention Center lists the work that needs you and the current notices. Red attention rows carry issues labelled
`abacus:needs-user-attention` and persistent recovery alerts; yellow notice rows
carry one live message per agent or Abacus source. Notice text wraps to the
terminal width instead of being clipped. A notice that repeats, or that restates
that source's persistent alert, refreshes the existing row instead of adding a
duplicate, resolving an alert clears its source's notice, and an unrepeated
notice expires after about a minute so resolved conditions stop consuming rows.
Scroll to see additional alerts instead of losing them to a hidden-count row.
Idle polling stays visually distinct from failure retries.

The compact header keeps claims and screen tabs visible. Settings shows the
current run's default model/effort, agent count, tmux session/window, and claim
schedule. These are read-only; use `abacus config edit` before a later run to
change saved configuration. **Shift-Tab** still toggles claims from every screen.
Each agent reports three ordered facts on one metadata line: its actual
checked-out branch (or `detached@<commit>`), the model it is using or would use,
and that model's reasoning effort — `branch: … • model: … • effort …`. Every
row reports them, including idle, waiting, paused, and stopped rows, so a
ticket-resolved model stays visible while the agent merges and falls back to the
run's default once the ticket ends. Settings reports the run's default
model/effort. Read-only Git snapshots refresh roughly every five seconds, including paused and stopped agents. `DIRTY` appears beside
the branch outside preparation, active work, and finalization; it includes tracked
and untracked changes. An unavailable snapshot shows `branch: unknown`, not stale
clean information. OpenCode TUI effort is marked **requested** because that harness
does not accept the effort flag; other harnesses receive the displayed effort.

Reopening an attention ticket clears its Beads assignee but does not release its
Git branch. If that branch remains checked out in an agent's worktree, only that
worktree can claim it. Other agents skip it and continue looking for work. If the
owning worktree is not in the running pool, run its agent or deliberately release
the branch after inspecting the workspace; Abacus will not force or detach it.

Add `--start-paused` to start with claims disabled and the header showing
**CLAIMS PAUSED** immediately. Press **Shift-Tab** to pause or resume new claims
for every agent. Active work continues; an agent pauses only when it next reaches the claim boundary.
When a configured [claim schedule](run-config.md#scheduled-claim-windows) is
holding claims, the header shows **CLAIMS BLOCKED** and each agent row names the
window and the next claimable time. The manual toggle cannot override a schedule;
only editing or clearing the config can.

Use the **Up** and **Down** arrows, or **k** and **j**, to select a row. The same
keys scroll an open comment. Press **Enter** on an agent
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

Switch to **3 Latest Comments** to select comment rows. Press **Enter**
on a comment to open its complete text with the issue title, author, and
timestamp. The detail view wraps without truncating the message. Use **Up**,
**Down**, **Page Up**, or **Page Down** to scroll a long comment, then press
**Escape** to return to the dashboard.

### Latest comments

Latest Comments shows the latest eight Beads comments by default. Each entry
shows an issue ID, truncated title, author, and up to two wrapped message lines.
Header colors provide quick attribution:

- red — issue carries `abacus:needs-user-attention`;
- green — author matches a configured agent;
- cyan — unrecognized author.

Change the count with `--latest-comments <1-100>`. The comment snapshot is
read-only. If refresh fails, Abacus keeps the previous snapshot and emits a
deduplicated warning.

### Merge slot

When the repository configures a Beads merge slot, the dashboard shows who owns
it. The holding agent's row is marked `⇄ holding merge slot`, every agent in the
waiter queue shows `⇄ merge queue #<position>`, and nothing else is drawn, so
ownership is read straight from the agent rows. Waiters that belong to another
machine or run hold a queue position but have no row to mark, so they are not
displayed. The slot is refreshed every five seconds with read-only
`bd merge-slot check`.

A harness that is gone can neither hold nor wait for a merge, so Abacus releases
a slot and prunes queue entries when the named agent is not configured in this
run or has no running harness. Configured, running agents are preserved, and
remaining waiters keep their order. Duplicate waiters and the current holder’s
redundant waiter entry are also removed. Each reclamation is reported as a warning.
**This version assumes a single controller:** claims and waiters from other
runs or external agents are also removed. Do not share the merge slot with
another controller.

### Logs instead of a dashboard

Use `--verbose` (also `-v`) for timestamped state transitions,
warnings, alerts, and every external command:

```sh
abacus run --verbose [other run options]
```

When standard error is redirected without verbose mode, Abacus automatically
uses compact state-transition lines instead of terminal control sequences. On a
terminal, verbose state levels and final outcome categories are color-coded for
quick scanning. Set `NO_COLOR=1` to disable color across logs, summaries, prompts,
standalone reports, and the dashboard while keeping their visual structure.

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
