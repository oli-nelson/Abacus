# Managed worktrees

```sh
abacus run --model provider/model --agents 3
# Optional display names; unspecified names default to agent-N:
abacus run --model provider/model --agents 3 --agent-name Alice --agent-name Bob
abacus worktrees list
abacus worktrees reclaim slot-2
abacus worktrees remove slot-2 --confirm
abacus worktrees prune
```

Omit `--agents` for one worker, or for the number of supplied names. Saved configs
use `"agentCount": 3` and optionally `"agentNames": ["Alice", "Bob"]`. Names must
be unique within a run, at most 100 characters, and contain no control characters; `maintenance` and `continuation` are reserved. A shorter
name list uses defaults for the remaining workers; a longer list is an error.
Custom names have no relationship to pool paths or durable ownership. Change
names between runs without moving checkouts, relabelling issues, or losing caches.

## Workers lease slots, not their own checkouts

Abacus creates detached pool checkouts as needed to establish the requested
minimum capacity. Before an assignment, a worker exclusively leases a safe slot.
Interrupted issue work anywhere in the pool is preferred over general capacity,
including slots above the current worker count. The lease lasts through claim,
preparation, execution, merge/finalization, and verified harness cleanup. An
operator-stopped worker retains its reservation until restart or shutdown.

After an assignment the lease is released; compilation caches remain. A smaller
worker count never deletes slots. A slot is not reusable just because it is
unlocked: unfinished issue branches are reserved for that issue; dirty detached
work, blocked/unknown tickets, inconsistent records, and unmerged closed work
remain untouched. If no safe slot exists, continuous workers wait with an alert;
finite runs report an error instead of claiming that work was drained.

Checkouts live in the OS local application-data directory under
`abacus/worktrees/<repository-key-and-id>/slot-N`. Existing `agent-N` pool slot IDs
remain valid and are never renamed automatically. Slot IDs are not worker names.
`worktrees list` prints exact paths, disk usage including ignored caches, and
assignment information. A `reserved` row is a conservative indication, not a
promise the ticket can resume without review.

## Ownership and recovery

The shared Git directory holds the pool manifest, controller lease, and durable
per-slot assignment records. Each assignment has a unique ID, run ID, worker ID,
issue ID, phase, display name, and launch location when known. Each harness launch
also gets a fresh execution ID, fencing late cleanup from earlier restarts. The controller lease
permits only one Abacus controller or pool-mutating command per repository; OS-held
workspace locks exclude simultaneous users of a slot. There is no lease timeout
or age-based stealing. Late updates from released assignments are rejected.

Fresh claims still use Beads' atomic `bd update --claim`. The issue intent is
recorded before claiming, and a pool-wide check prevents two slots from reserving
the same issue. Recovery reconciles this record with the branch and Beads rather
than matching configured agent names. Beads assignees remain human-readable:
when a new worker safely resumes the assignment, its name replaces the previous
one. Before then, the old name means the last assignee, not a currently live agent.
An unexpected manual assignee change requires review instead of automatic takeover.
Legacy branches without assignment evidence never justify stealing an assigned
or in-progress ticket.

### Controller crash or uncertain cleanup

Before launching a harness, Abacus durably records `execution-uncertain`. Only
successful cleanup with a verified stopped host clears it. A controller crash,
ambiguous launch failure, or cleanup failure leaves this marker intact even if
the OS releases the controller/workspace locks. The slot cannot be dispatched,
reclaimed, or deleted on the assumption that its agent died too. A new controller
also refuses to start while any slot has uncertain execution: that old process
might still own the merge slot or be integrating in the main checkout. This avoids
reclaiming its merge ownership or racing a newly launched supervisor.

1. Stop Abacus and inspect `abacus worktrees list` for the recorded pane/process.
2. **Stop all surviving harness processes and subprocesses using that checkout.**
   A missing controller or reused PID alone is not proof. Check any attached
   server activity too. If uncertain, leave the slot quarantined.
3. Only after confirming they have stopped:
   ```sh
   abacus worktrees recover slot-2 --confirm
   ```
4. Restart Abacus. It rechecks the issue, branch, binding, and local state before
   recovery, updates the assignee, and preserves the work.

`recover --confirm` records the operator's stop confirmation; it does **not** kill
processes, claim proof of their absence, reset Git, or reopen/close issues. This
conservative manual step is deliberately preferred to automatically stealing a
potentially live checkout. No wrapper/PID timeout is treated as a safety guarantee.

## Maintenance and shrinking the pool

Stop Abacus before mutating the pool. Commands accept `--repo <main-checkout>`.
To reduce six workers to three, pause claims, let in-flight work finish, stop,
change the count, then inspect and remove any three safe slots. There is no need
to choose slots matching particular worker names or numbers.

- **list:** read-only paths, assignment/branch details, review status, and bytes.
- **reclaim:** detach an idle clean slot. Require closed recorded/branch issues
  and HEAD merged into a configured target; preserve caches and branch refs.
- **remove --confirm:** same checks, then remove the checkout and ignored caches
  with non-forced `git worktree remove`.
- **recover --confirm:** acknowledge verified execution shutdown after a crash;
  retain the issue reservation and all files for normal recovery.
- **prune:** forget missing slots only after their Git registrations have been
  removed, and only if no assignment remains. Repair moved worktrees instead.

Cleanup uses guarded `git reset --hard`, never `git clean`: preserve untracked
files and ignored caches, refusing resets that would overwrite an obstruction.
Ordinary untracked leftovers can still require review before fresh dispatch.

## Migrating older configurations

Before upgrading from dedicated agent slots, stop the old controller and all its
harnesses (including surviving tmux panes or attached-server work). Old executions
pre-date the assignment journal and cannot be proven stopped by its absence.

Existing `agents` arrays and `--agent <name> <workspace>` declarations retain their
explicit workspace behavior. They are never adopted, moved, or deleted. Finish
or explicitly recover their tickets before switching to a pool: checked-out issue
branches in those workspaces remain reserved there.

Replace `agents` with `agentCount` and optional `agentNames`. CLI `--agents` or
`--agent-name` selects managed mode over a saved legacy list; CLI `--agent`
replaces the saved count and names. Never combine both modes in one CLI/config
layer. Legacy pool `agent-N` paths remain unchanged; only newly allocated slots
use `slot-N` IDs. Missing, foreign, malformed, or duplicate issue ownership is
reported for review rather than silently recreated or reassigned.
