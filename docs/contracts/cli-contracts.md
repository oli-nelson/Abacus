# External CLI contracts

Captured through 2026-09-02 with the minimum versions listed in `README.md`. Tests use the sanitized JSON in `tests/Abacus.Tests/Fixtures/Beads`; timestamps, generated IDs, repository paths, and clone identities are intentionally stable fixture values rather than the disposable repository's values.

## Beads 1.2.2

All commands run with the agent workspace as the working directory. Agent-owned commands receive `BEADS_ACTOR=<agent-name>` in their child environment.

| Operation | Invocation | Successful stdout | Exit behavior |
| --- | --- | --- | --- |
| Atomic claim | `bd ready --claim --exclude-label gt:slot --json` | A JSON array containing zero or one issue. A claim changes its status to `in_progress` and sets `assignee` to `BEADS_ACTOR`. | Both no ready work (`[]`) and a claim exit 0. Invalid/missing projects exit nonzero, so empty stdout is not treated as idle. Merge-slot coordination beads are excluded from the work queue. |
| Assigned retry lookup | `bd ready --assignee <agent> --exclude-label gt:slot --json` | Ready issues already assigned to this agent. | Used only when the atomic claim returns no issue, to recover retries left assigned by older Abacus versions. The selected issue is reclaimed with `bd update <id> --claim --json`. |
| Read issue | `bd show <id> --json` | A one-element JSON array. `id` and `status` are the only required fields. | Missing issues and command failures exit nonzero. |
| Reopen | `bd update <id> --status open --assignee "" --append-notes <reason> --json` | Updated issue JSON. | Nonzero means the issue was not reliably reopened. Clearing the assignee returns the issue to the atomic claim queue. |
| Dolt identity | `bd dolt show --json` | Embedded mode includes `embedded: true`, `database`, and `data_dir`. Server mode includes `embedded: false`, `host`, `port`, and `database`; `user` is not part of database identity. | Unavailable/malformed configuration exits nonzero. Multi-agent mode accepts only non-embedded identities with equal normalized host, port, and database. |
| List remotes | `bd dolt remote list --json` | `[]` or an array with `name`, `url`, `sql_url`, and `status`. | Exit 0 for either presence or absence; malformed output/failure is an error. |
| Pull | `bd dolt pull --json` | Human-readable progress (despite `--json`). | With no remote this version exits 1 (`no remote`). Unreachable/invalid remotes also exit 1. Abacus invokes pull only after a successful remote-list result says a remote exists. |
| Push | `bd dolt push --json` | Human-readable progress (despite `--json`). | With no remote this version prints a skip message and exits 0. Unreachable remotes exit 1. |

Captured issue statuses are `in_progress`, `open`, `blocked`, and `closed`. Schema additions are expected; production parsing must use `JsonDocument` and extract only the fields above.

Representative failure transcripts and exit codes are in `command-outcomes.json`. They establish that “no ready issue” is a successful empty array, while Beads failures are nonzero results.

## OpenCode 1.18.20

Interactive local mode uses the full TUI directly in a tmux pane:

```sh
opencode --prompt '<ticket prompt>' --model provider/model
```

The process must remain connected directly to the pane terminal; piping it would remove the TTY expected by the interactive interface.

Local mode was exercised as:

```sh
opencode run 'Reply with exactly OK.' \
  --model opencode/big-pickle \
  --dir /tmp/abacus-contract \
  --format json
```

It created one new session, reported provider `opencode` and model `big-pickle`, emitted `OK`, and exited 0 when the run ended.

Attached mode was exercised against a disposable `opencode serve` process as:

```sh
opencode run 'Reply with exactly ATTACHED.' \
  --model opencode/big-pickle \
  --attach http://127.0.0.1:45723 \
  --dir /tmp/abacus-contract \
  --format json
```

The server log recorded a newly-created session with the requested directory and `providerID=opencode modelID=big-pickle`. The client exited 0. This proves Abacus can create attached client sessions entirely through the CLI; no HTTP integration is needed. The `--format json` flag was used only to make this contract check observable and is not required by Abacus.

The [OpenCode model documentation](https://opencode.ai/v2/docs/models) defines variants as provider-specific overlays used for settings such as reasoning effort. OpenCode 1.18.20's TUI entry point has no `--variant` flag, and its model parser treats a `#variant` suffix as part of the model ID rather than as variant metadata. This [upstream OpenCode issue](https://github.com/anomalyco/opencode/issues/7354) tracks the missing top-level variant option. Abacus therefore passes the interactive model ID unchanged; OpenCode uses its configured or session-selected variant. The `run` command accepts `--variant <effort>` directly, including attached-server runs, so OpenCode Server mode applies the requested effort normally. Variant availability remains model-specific.

## Codex CLI 0.151.0

The installed `codex --help` and the [official Codex developer command reference](https://developers.openai.com/codex/cli/reference) establish that the base `codex` command launches the interactive TUI, accepts an initial positional prompt, and supports `--cd`, `--model`, `--sandbox`, and `--ask-for-approval`. Abacus uses:

```sh
codex --cd <workspace> \
  --model <model> \
  --config model_reasoning_effort=<effort> \
  --approve-for-me \
  '<ticket prompt>'
```

There is deliberately no `exec` subcommand. The [Codex configuration reference](https://developers.openai.com/codex/config-reference) defines `model_reasoning_effort`; Abacus supplies it through the CLI's per-invocation `--config` override. The installed version's `--approve-for-me` flag routes approval requests through automatic review using the workspace-write sandbox, allowing aligned Git, Beads/Dolt, and network operations without waiting for a human. This flag is part of Abacus's Codex 0.151.0 minimum contract; it was confirmed from the installed CLI help, while the upstream reference documents the corresponding automatic-review workflow and `/approve` retry command.

## Claude Code 2.1.212

The installed `claude --help`, [official Claude Code CLI reference](https://code.claude.com/docs/en/cli-usage), and [model configuration reference](https://code.claude.com/docs/en/model-config) establish that `claude '<prompt>'` starts an interactive session, `-p`/`--print` runs non-interactively and exits, and `--effort` selects the session effort. Claude uses the process working directory as its primary workspace. Abacus uses:

```sh
cd <workspace>
claude --model <model> \
  --effort <effort> \
  --permission-mode auto \
  --name '<agent> • <issue-id>' \
  [--remote-control '<issue-id> • <issue-title>'] \
  '<ticket prompt>'
```

There is deliberately no `--print`. `--effort` applies the requested model-specific effort to the interactive session. Automatic permission mode performs background safety checks without requiring a human to answer ordinary approval prompts. With Abacus `--remote`, the command additionally receives `--remote-control '<issue-id> • <issue-title>'`; the [official Claude Code Remote Control documentation](https://code.claude.com/docs/en/remote-control) confirms that this preserves the full local interactive session while giving the remote session an explicit name. Abacus does not use Claude background agents, worktree creation, or the Agent SDK.

## tmux 3.6a

A disposable session proved this pane contract:

```sh
tmux split-window -t <session-or-session:window> -d -P -F '#{pane_id}' <wrapper-path>
tmux send-keys -t <returned-pane-id> C-c
tmux kill-pane -t <returned-pane-id>
```

The wrapper ran a child command, atomically renamed a temporary marker containing exit code `0`, and stayed alive. `split-window` returned a distinct pane ID, the marker contained `0`, and `send-keys ... C-c` interrupted the wrapper. Cleanup targeted only that recorded pane ID. This establishes the shared wrapper/marker protocol used by all pane-hosted modes without tmux control mode.

## Git 2.55.0

Abacus uses only argument-list invocations: `git -C <workspace> rev-parse`, `status --porcelain`, `show-ref --verify --quiet refs/heads/<branch>`, `switch <branch>`, `switch -c <branch>`, and `branch --show-current`. Exit codes are authoritative; branch names and workspace paths are never interpolated into a shell command.
