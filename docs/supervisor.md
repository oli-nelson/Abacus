# Optional maintenance supervisor

Enable the supervisor with a separate model for the selected harness:

```sh
abacus run --config run.json --supervisor-model 'provider/model#high' \
  --supervisor-timeout 30m \
  --supervisor-extra-args '--profile maintenance' \
  --supervisor-prompt-file ./supervisor-policy.md
```

Use a model ID native to your selected mode (`provider/model` for OpenCode).
Only `--supervisor-model` is needed to enable supervision. The timeout defaults
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
  "supervisorModel": "provider/model#high",
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
through stdio using the reserved name `supervisor`.

## Completion, visibility, and audio

Each run receives a unique absolute JSON completion-file path under Abacus's
temporary runtime directory, outside the checkout, and a unique run ID. The prompt
instructs the harness to atomically write `{"runId":"...","summary":"..."}` and
wait. Abacus validates the ID and JSON, stops/cleans the harness through its normal
host boundary, and independently checks labels and retry outcomes. A clean exit
without the signal is not success. Partial, stale, or oversized signals are ignored.
Files are cleaned up afterward; no tracked runtime files are created.

The TUI has a separate supervisor row showing startup, working, recovery checking,
and the last outcome (completed, unresolved, timeout, crash, cancellation, or
failure), with model and host location while running. Persistent unresolved alerts
remain visible. State transitions also appear in verbose logs and `agent.state`
events, under `supervisor`; worker outcome totals remain separate.

With interactive TUI audio enabled, startup plays `media/abacus_supervisor.mp3`.
Supervisor clips stop any playing attention clip and suppress new attention sounds
until they finish; suppressed attention sounds are not queued for later.
After recovery verification, unresolved labels/attention, failed retries, or a
failed harness run play `media/abacus_supervisor_failed.mp3` once. No supervisor
audio plays when TUI audio is off or output is noninteractive. Playback is best
effort; missing media/player or playback failure never changes scheduling.
