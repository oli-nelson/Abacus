# Getting Started with Abacus

This guide takes you from an installed binary to a running agent. Choose one
setup path; you do not need to perform every walkthrough.

- [Path A](#path-a-create-a-new-multi-agent-project): let Abacus generate a new project and worktrees.
- [Path B](#path-b-use-an-existing-repository): add one agent to an existing repository.
- [Path C](#path-c-scale-an-existing-repository-to-multiple-agents): prepare multiple worktrees and shared Dolt yourself.
- [Path D](#path-d-use-opencode-server-without-tmux): attach directly to an existing OpenCode server.

For a more visual tour, open the [interactive quick-start guide](quick-start.html).

## 1. Install the tools

Abacus supports macOS and Linux.

| Tool | Minimum version | Needed for |
| --- | --- | --- |
| .NET SDK | 10.0.101 | Building Abacus |
| Beads (`bd`) | 1.2.2 | Every run |
| Git | 2.55.0 | Every run |
| OpenCode | 1.18.20 | OpenCode modes |
| Codex CLI | 0.151.0 | Codex mode |
| Claude Code | 2.1.212 | Claude mode |
| tmux | 3.6a | Interactive modes and optional pane-hosted server mode |

Only the selected agent harness is required for a particular run.

Build, test, and publish Abacus:

```sh
dotnet test Abacus.sln
dotnet publish src/Abacus -c Release -o artifacts/publish
./artifacts/publish/abacus --help
```

For a single self-contained executable, supply a runtime identifier:

```sh
dotnet publish src/Abacus -c Release -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o artifacts/publish
```

Supported targets are `osx-arm64`, `osx-x64`, `linux-x64`, and `linux-arm64`.
The examples below assume the resulting `abacus` executable is on `PATH`.

## 2. Understand the workspace rule

Every agent needs a different Git workspace: the main checkout, a linked
worktree, or a separate clone. Two agents may never share the same directory.

> [!CAUTION]
> Abacus treats assigned workspaces as disposable. Before each claim it runs
> `git reset --hard HEAD` and `git clean -fd`. This discards tracked changes and
> untracked, non-ignored files and directories. Commit or move anything you need
> before starting Abacus.

Ignored files remain because Abacus does not use `git clean -x`.

## Path A: create a new multi-agent project

This is the fastest route when you are starting from scratch.

From the directory that should contain the new project, run:

```sh
abacus --init-new-multi-agent-repo my-project 4
```

Abacus refuses an existing `my-project` destination, then creates:

```text
my-project/
├── repo/                       # main checkout and Beads project
├── worktrees/0/                # detached agent worktree
├── worktrees/1/
├── worktrees/2/
├── worktrees/3/
├── run_abacus_opencode.sh
├── run_abacus_codex.sh
└── run_abacus_claude.sh
```

The initializer:

1. Creates `repo/` with an initial `main` branch.
2. Initializes a uniquely named shared-server Beads database non-interactively
   with the maintainer role.
3. Sets `no-git-ops=false`, marks Dolt local-only, and creates a merge slot.
4. Installs the four bundled Abacus skills.
5. Commits the initial repository state.
6. Adds the requested detached worktrees.
7. Writes executable launchers that discover `worktrees/*` at runtime.

It does **not** create a tmux session. Create ready work and start one yourself:

```sh
cd my-project/repo
bd create "Add the first feature" \
  --description "Describe the change and important context." \
  --acceptance "State the observable definition of done." \
  --json

cd ..
tmux new-session -d -s my-project
./run_abacus_codex.sh gpt-5.6-sol high
```

Launchers accept model and effort as their first two arguments. You can also use:

| Variable | Purpose |
| --- | --- |
| `ABACUS_BIN` | Override the Abacus executable |
| `ABACUS_MODEL` | Set the default model |
| `ABACUS_EFFORT` | Set the default effort or variant |
| `ABACUS_TMUX_SESSION` | Override the normalized project-name session |

## Path B: use an existing repository

The following example starts one interactive OpenCode agent in an existing
repository.

### B1. Choose values

```sh
export REPO=/path/to/your/repository
export AGENT=alice
export SESSION=abacus-work
export WINDOW=agents
export MODEL=provider/model
export EFFORT=high
```

Use `abacus --models` or the selected harness's model picker/catalog to choose a
valid model.

### B2. Initialize and inspect Beads

```sh
cd "$REPO"
bd init --init-if-missing --non-interactive
bd config set no-git-ops false
bd dolt show --json
bd dolt remote list --json
```

A single agent may use the default embedded Dolt database. Make sure repository
instructions define how an agent should serialize and merge its branch into
`main`; Abacus supplies a basic merge-slot-aware fallback, but does not merge
branches itself.

### B3. Install the optional skills

```sh
abacus --install-skills
```

This installs:

| Skill | Purpose |
| --- | --- |
| `abacus-beads-planner` | Turn a concept into a reviewed Beads issue graph. |
| `abacus-beads-doctor` | Audit issue content, metadata, and dependencies. |
| `abacus-beads-attention` | Summarize issues that need human action. |
| `abacus-git-check` | Audit agent-facing Git restrictions. |

Existing bundled skill directories are replaced only after confirmation;
unrelated skills are preserved. Installation is optional and does not start an
agent run.

### B4. Create ready work

```sh
bd create "Add a hello-world file" \
  --description "Create HELLO.md with a short hello-world message and verify it." \
  --acceptance "HELLO.md is committed and merged into main." \
  --json

bd ready --json
git status --porcelain
```

If the final command shows changes you need, commit or move them now.

### B5. Start tmux and validate the repository

```sh
tmux new-session -d -s "$SESSION" -n "$WINDOW"
abacus --health
```

`--health` is read-only. It reports tool versions, Beads storage and
configuration, worktrees, merge-slot availability, bundled skills, and runnable
agent modes. A missing merge slot is advisory; repositories may serialize
merges another way.

### B6. Start the agent

```sh
abacus \
  --mode opencode \
  --tmux-session "$SESSION" \
  --tmux-window "$WINDOW" \
  --model "$MODEL" \
  --effort "$EFFORT" \
  -a "$AGENT" "$REPO"
```

Attach in another terminal to watch the interactive agent:

```sh
tmux attach-session -t "$SESSION"
```

Detach with `Ctrl-b d`. Stop Abacus with `Ctrl-C`; remove the tmux session
yourself when you are finished.

## Path C: scale an existing repository to multiple agents

Multiple agents need both isolated Git workspaces and one shared, server-backed
Dolt database. This example keeps the primary checkout for administration and
creates four disposable agent worktrees.

### C1. Configure shared Beads storage

```sh
export REPO=/path/to/your/repository
export WORKTREES=/path/to/your/repository-worktrees
export BASE=main

cd "$REPO"
bd init --shared-server --non-interactive
bd config set no-git-ops false
bd dolt start
bd dolt show --json
```

Review and commit project files changed by `bd init` before assigning worktrees
to Abacus. Do not separately run `bd init` inside each linked worktree; Beads
discovers the shared workspace from the repository.

### C2. Create detached worktrees

```sh
mkdir -p "$WORKTREES"
git -C "$REPO" worktree add --detach "$WORKTREES/alice" "$BASE"
git -C "$REPO" worktree add --detach "$WORKTREES/bob" "$BASE"
git -C "$REPO" worktree add --detach "$WORKTREES/carol" "$BASE"
git -C "$REPO" worktree add --detach "$WORKTREES/dave" "$BASE"
git -C "$REPO" worktree list
```

Detached worktrees are intentional: Abacus creates or checks out the matching
`abacus/<issue-id>` branch after claiming work.

### C3. Verify shared identity

```sh
for agent in alice bob carol dave; do
  bd -C "$WORKTREES/$agent" dolt show --json
done
```

Every result must describe a non-embedded database with the same normalized
host, port, and database. Abacus checks those fields during preflight and
rejects a pool that does not share one identity.

This requirement follows Beads' concurrency model: embedded mode is
single-writer, while server mode supports multiple concurrent clients. See the
[Beads FAQ](https://github.com/gastownhall/beads/blob/main/docs/reference/faq.md)
and [Dolt documentation](https://github.com/gastownhall/beads/blob/main/docs/DOLT.md).

### C4. Start the pool

Create enough independent ready issues for the pool, then run:

```sh
tmux new-session -d -s abacus-work -n agents

abacus \
  --mode codex \
  --tmux-session abacus-work \
  --tmux-window agents \
  --tmux-layout tiled \
  --model gpt-5.6-terra \
  --effort high \
  -a alice "$WORKTREES/alice" \
  -a bob "$WORKTREES/bob" \
  -a carol "$WORKTREES/carol" \
  -a dave "$WORKTREES/dave"
```

Each loop claims atomically, so agents do not intentionally receive the same
issue.

## Path D: use OpenCode Server without tmux

Start an OpenCode server in its own terminal:

```sh
opencode serve --hostname 127.0.0.1 --port 4096
```

Then attach an Abacus agent from another terminal:

```sh
export REPO=/path/to/your/repository

abacus \
  --mode opencode-server \
  --model provider/model \
  --effort high \
  --opencode-server 127.0.0.1:4096 \
  -a alice "$REPO"
```

Pass `host:port`, without an `http://` prefix. Abacus normalizes the address and
starts a directly supervised `opencode run --attach` child. It does not start,
stop, or query the server API. Add `--tmux-session` if you prefer attached
clients hosted in panes.

## Next steps

- Learn to read, pause, and operate a pool in the [Operations guide](operations.md).
- Look up filters, timeouts, finite modes, and prompt additions in the [CLI reference](cli-reference.md).
- Review the cleanup and ownership model in [Architecture and boundaries](architecture.md).
