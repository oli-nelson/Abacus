# Events and stdio control

Abacus can report structured activity while showing its TUI, or act as a
non-interactive child process controlled by another agent. These options belong
to `run`; they do not change how the selected coding-agent harness is hosted.

## Record activity

Add `--event-log /path/to/events.jsonl` to any normal run:

```sh
abacus run --mode codex --model gpt-5.4 --tmux-session agents \
  --event-log /tmp/abacus-events.jsonl -a alice /path/to/worktree
```

The file is UTF-8 JSON Lines, appended rather than overwritten and flushed after
every event. Its parent directory must exist. Use a separate file per concurrent
Abacus process. Keep logs **outside agent workspaces**: appending a tracked or
untracked file there would trigger dirty-workspace protection.

Logs contain workspace paths, ticket titles, comments, and subprocess command
arguments, potentially including prompts or sensitive data. Protect and rotate
them yourself; there is no built-in rotation or redaction. These are Abacus
activity events, not transcripts of the hosted agents' stdout/stderr.

## Run as an agent-controlled process

Launch Abacus with pipes for stdin/stdout/stderr:

```sh
abacus run --stdio --start-paused \
  --mode opencode-server --opencode-server 127.0.0.1:4096 \
  --model provider/model --repo /path/to/repo \
  -a alice /path/to/worktree --event-log /tmp/abacus-events.jsonl
```

`--stdio` guarantees event-only stdout: no dashboard, ASCII intro, human summary,
terminal bell, or child-process output. Stderr is reserved for fatal diagnostics.
It rejects `--verbose` and enabled desktop notifications. Normal harness
prerequisites still apply: local OpenCode, Codex, and Claude require tmux;
direct OpenCode Server attachment does not.

Wait for `control.ready` before sending commands. Startup preflight and the Beads
baseline happen first. By default agents may already claim work when control
becomes ready; `--start-paused` prevents **fresh** claims until `resume`.
This also delays interrupted dirty-workspace recovery; once resumed, that
recovery still takes precedence over fresh dispatch, as in the TUI. The flag also
works in the interactive TUI, where **Shift-Tab** resumes claims.
It cannot be used with verbose output or redirected/dumb terminals unless
`--stdio` supplies the resume control.

Send one JSON object per line and flush each line. Commands are case-sensitive;
`id` is a required nonempty string chosen by the caller. Use distinct IDs to
correlate results. IDs are echoed, not persisted or deduplicated.

```json
{"id":"1","command":"status"}
{"id":"2","command":"pause"}
{"id":"3","command":"resume"}
{"id":"4","command":"stop","agent":"alice"}
{"id":"5","command":"restart","agent":"alice"}
{"id":"6","command":"clean-workspace","agent":"alice","confirm":true}
{"id":"7","command":"shutdown"}
```

| Command | Behavior |
| --- | --- |
| `status` | Returns claim permission, full agent rows, the resolved tmux target when pane-hosted, current attention issues, persistent alerts, recent comments, and recent warnings. |
| `pause` / `resume` | Disable/enable new claims; active tickets continue. |
| `stop` | Interrupt the named agent, retain its ticket reservation, and park its loop. |
| `restart` | Interrupt/relaunch the reserved ticket or resume the parked loop. |
| `clean-workspace` | **Destructive:** requires literal JSON `confirm:true`; safely reopen active work, then discard tracked/untracked workspace changes using the same confirmed TUI cleanup path. Leave the agent parked. |
| `shutdown` | Acknowledge, stop accepting input, and gracefully shut down all agents using normal ticket recovery and host cleanup. |

Responses are `control.result` events with `data.id`, `data.command`, and
`data.ok`. Failures include `data.error`; status succeeds with `data.status`.
Agent rows include `branch`, nullable `isDirty`, `model`, `effort`, and `runActive`.
Workspace fields refresh roughly every five seconds and become null when a read
fails. `runActive` is true only while that agent hosts a running agent process;
the dashboard shows the row's `model`/`effort` line only then, while the event
stream always carries both fields. Model/effort describe the current launch
selection, reverting to defaults when the ticket is cleared. OpenCode TUI effort
is requested, not confirmed by that harness. These fields are also included in
`agent.state` events.
Malformed input may have a null ID/command. Unknown commands/agents, duplicate or
unexpected properties, missing fields, unconfirmed cleanup, and oversized lines
(over 65,536 characters) fail without terminating the session. A pending agent
request cannot be overwritten; agents whose finite loops have finished cannot be
restarted within that run.

Successful stop/restart/clean results mean **accepted**, not completed: follow
`agent.state`, `ticket.outcome`, and alert events to observe the result. Do not
blindly pipeline conflicting actions; wait for the desired state first.

EOF is equivalent to graceful shutdown, not permission to leave agents running.
A final command without a newline is processed before EOF. Keep stdin open while
controlling a continuous run. With `--once` or `--drain`, Abacus also exits when
its finite work finishes, even if stdin remains open. Ctrl-C retains exit 130;
successful shutdown/EOF exits 0, and orchestration or event-output failure exits 1.

## Event contract (version 1)

Each line is a complete object:

```json
{"version":1,"runId":"...","sequence":1,"timestamp":"2026-09-06T12:00:00+00:00","type":"run.starting","data":{"model":"provider/model","effort":"high","reasoningModels":{},"reasoningEfforts":{},"extraArguments":[],"reasoningArguments":{},"agentMode":"openCodeServer","executionMode":"continuous","agents":[{"name":"alice","workspacePath":"/path/to/worktree"}]}}
```

`runId` separates runs in appended logs. `sequence` is monotonically increasing
within one run, and timestamps are UTC. All sinks receive the same serialized
line in the same order, including concurrent agents. Newlines/control characters
inside strings are JSON-escaped. Consumers should ignore unknown types/fields.

| Type | `data` |
| --- | --- |
| `run.starting` | Parsed default model/effort, reasoning model/effort mappings, default and per-tier extra harness arguments, harness mode, execution mode, configured agents |
| `system`, `warning` | Message; warnings also include source |
| `command` | Source and subprocess command diagnostic (including exit diagnostics); independent of `--verbose` |
| `agent.state` | Full row: name, activity, detail, changedAt, issueId, ticketTitle, runLocation, workspacePath, lastExitCode, hasExitObservation, retryCount, branch, isDirty, model, effort, runActive |
| `tmux.target` | Resolved session and window names for a pane-hosted run |
| `claims.changed` | `enabled` |
| `attention.changed`, `comments.changed` | Replacement `issues` / `comments` arrays (empty clears them) |
| `alert.raised`, `alert.cleared` | Source and, when raised, message |
| `control.ready` | Supported commands and initial claim permission |
| `control.result` | Correlated command result described above |
| `control.requested` | TUI-originated agent action |
| `control.eof`, `control.error` | Input disconnected or failed |
| `ticket.outcome` | Agent, outcome, issue ID, title |
| `run.summary` | Elapsed duration, initial Dolt commit, per-agent outcome totals |
| `run.error` | Fatal error message |
| `run.exited` | Final process exit code |

Unchanged state/comment/attention snapshots are not continually re-emitted.
Use `status` for the complete current view, and treat the final process exit code
as authoritative. Startup failures after event setup emit `run.error` and
`run.exited`, but may have no summary or `control.ready`. Argument parsing and
log-file opening failures occur before event setup and report only on stderr.
If an event sink breaks during a run, Abacus reports the failure on stderr,
requests shutdown, and still performs cleanup; delivery to a broken sink cannot
be guaranteed.

## Interactive entrance

Normal TUI runs open with an animated ASCII logo and abacus while a bundled mix
plays the welcome track over the jingle at 15% gain so the voice stays prominent.
Any key skips the animation and stops its audio; a natural animation completion
lets the mix finish in the background. Intro audio is off by default;
`--tui-audio` enables it, while `--no-intro` disables both animation and audio. It
never plays for stdio, verbose, preflight, standalone commands, redirected
stdin/stdout/stderr, or `TERM=dumb`. `NO_COLOR` disables its colors. Narrow
terminals receive a compact version.
