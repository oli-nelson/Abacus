# Release Smoke-Test Record

This page records manual release evidence; it is not a setup guide. For a
repeatable user walkthrough, see [Getting started](getting-started.md). For the
external behaviors under test, see the [CLI contracts](contracts/cli-contracts.md).

| Field | Value |
| --- | --- |
| Date | 2026-08-31 |
| Platform | macOS arm64 |
| OpenCode | 1.18.20 |
| Model | `opencode/big-pickle` |

The command contracts had already been captured with the minimum supported
versions. All repositories, Dolt databases and remotes, servers, and tmux
sessions used in this smoke test were disposable under `/tmp`.

## Local single-agent path

- Initialized a Git repository and embedded Beads project with one ticket.
- Started an existing named tmux session and ran the published `abacus` executable without `--opencode-server`.
- Abacus claimed `smoke-20e` as `alice`, created `abacus/smoke-20e`, and launched the real OpenCode CLI with `--model opencode/big-pickle`.
- The agent created and committed `SMOKE.txt`, fast-forwarded it to `main` using the repository's documented merge process, and closed the ticket.
- Abacus detected `closed`, sent Ctrl-C to its recorded pane, removed the run files, and returned to idle polling. No Dolt pull or push was attempted because no remote was configured.

## Attached two-agent shared-Dolt path

- Started one external Dolt SQL server and configured two detached Git worktrees against the same `127.0.0.1:45851/shared` identity (`embedded: false`).
- Started one existing OpenCode server. Its disposable test configuration allowed both worktree directories.
- Started `alice` and `bob` together with `--opencode-server 127.0.0.1:45852` and `--model opencode/big-pickle`.
- Atomic claims assigned the two tickets once each. Abacus created two distinct issue branches and tmux panes.
- Both attached OpenCode client sessions were observed in the server log with `providerID=opencode modelID=big-pickle`.
- The repository's lock-and-`git update-ref` script serialized both merges. `RED.txt` and `BLUE.txt` reached `main`; tickets `smoke-de6` and `smoke-7wj` both became `closed`.
- Abacus detected both terminal states and cleaned its panes. No Dolt push was attempted because the shared database had no remote.

## Unexpected-exit and remote-push path

- Configured a single-agent embedded Beads project with a working local file remote, then pushed its initial state.
- Put a controlled, sleeping `opencode` test executable first on `PATH`, allowing the harness to kill exactly the OpenCode child while leaving Abacus and tmux alive.
- Abacus claimed `smoke-9pm`, created its issue branch, and invoked the expected prompt, `--model provider/fake`, and `--dir` arguments.
- The harness sent SIGKILL to OpenCode. The wrapper atomically recorded exit code 137.
- Abacus logged the unexpected exit, re-read the ticket as `in_progress`, changed it to `open` with the note `Abacus agent alice exited with process code 137 before updating the ticket`, interrupted the recorded pane, and successfully ran `bd dolt push`.

## Release artifact

The following produced a single Mach-O arm64 executable named `abacus`; invoking `--help` returned 0 and printed the documented prerequisites and both usage forms:

```sh
dotnet publish src/Abacus/Abacus.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true \
  -o artifacts/publish
```

The automated fake-CLI suite separately exercises process-level SIGINT so Ctrl-C shutdown can be asserted deterministically without shell background-job signal semantics.
