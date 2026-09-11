# Getting Started with Abacus

> **Ticket targets:** Configure the target allowlist and default destination.
> Missing ticket metadata is allowed unless `enforceTargetBranch` is enabled. See [target setup, audit, and recovery](targets.md).

> **Reasoning routing:** `.abacus/reasoning.json` optionally requires one of
> `abacus:high_reasoning`, `abacus:medium_reasoning`, or
> `abacus:low_reasoning` on executable tickets. Runtime `--reasoning-model`
> options map those tiers to models; otherwise `--model` is the fallback.

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

> [!NOTE]
> Abacus preserves dirty workspaces. When the current branch is
> `abacus/<issue-id>`, it resumes that exact open issue before normal dispatch.
> Other dirty workspaces stop the affected agent with a persistent alert and
> remain untouched for operator recovery. Clean agents wait at startup until
> interrupted workspaces have reserved their tickets.

## Path A: create a new multi-agent project

This is the fastest route when you are starting from scratch.

From the directory that should contain the new project, run:

```sh
abacus new my-project --agents 4
```

Abacus refuses an existing `my-project` destination, then creates:

```text
my-project/
├── repo/                       # main checkout and Beads project
├── worktrees/0/                # detached agent worktree
├── worktrees/1/
├── worktrees/2/
├── worktrees/3/
├── abacus_base.json                  # shared repo, agents, effort
└── abacus_{opencode,codex,claude}.json # baseConfig + mode/model
```

The initializer:

1. Creates `repo/` with an initial `main` branch.
2. Initializes a uniquely named shared-server Beads database non-interactively
   with the maintainer role.
3. Sets `no-git-ops=false`, marks Dolt local-only, and creates a merge slot.
4. Installs the four bundled Abacus skills, writes `.abacus/targets.json`
   with `enforceTargetBranch: false`, `defaultTarget: "main"`, and `main` allowed,
   and writes `.abacus/reasoning.json` with `enforceLabels: false`.
5. Commits the initial repository state.
6. Adds the requested detached worktrees.
7. Writes `abacus_base.json` with the repository, created worktrees, and shared
   effort, plus three harness/model configs that reference it with `baseConfig`.
   It sets `startPaused: true`, `notify: "all"`, and `notifySound: true`.
   No shell launcher scripts are generated.

The initializer itself does not create a tmux session. Create ready work and run
`abacus run` from the project root and select a harness config; Abacus creates
its derived detached session and `Abacus Agents` window when needed:

```sh
cd my-project/repo
bd create "Add the first feature" \
  --description "Describe the change and important context." \
  --acceptance "State the observable definition of done." \
  --json

cd ..
abacus run # select abacus_codex.json (or another harness config)
```

Edit shared settings with `abacus config edit abacus_base.json`, and the Codex
harness/model with `abacus config edit abacus_codex.json`. The base is incomplete
on its own: select a harness config in the picker. Generated runs start paused;
press **Shift-Tab** to resume claims. All notifications and sound are enabled.

For automation, redirected I/O, or running from elsewhere, explicitly select a
config; the non-interactive routes never open the picker:

```sh
abacus run --config /path/to/my-project/abacus_codex.json --start-paused=false
```

Generated config paths are relative to their own file. CLI `--model`, `--effort`,
`--tmux-session`, and `--reasoning-model high <model>` override saved values.
For custom inherited settings, create `local.json` with
`"baseConfig": "abacus_codex.json"` and use `abacus run --config local.json`.

**Migration:** `abacus new` no longer creates launch scripts. Use `abacus run` and
its picker, or supply `--config` with CLI overrides instead of script arguments
or environment variables. Add new worktrees to the base config explicitly.
Previously generated scripts are not changed or removed.

See [run configurations](run-config.md) for the editor, Save As, and JSON schema.

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

Use `abacus models` or the selected harness's model picker/catalog to choose a
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
the ticket's bound target branch; Abacus supplies a basic merge-slot-aware fallback, but does not merge
branches itself.

### B3. Initialize Abacus configuration and skills

After Beads setup, run from inside the main checkout (not a linked worktree):

```sh
abacus init
```

From outside the main checkout, use `abacus init --repo /path/to/main-checkout`.
The same `--repo` option is supported by health and ticket-maintenance commands.

This installs the bundled skills and creates `.abacus/targets.json` allowing
`main` only if the config is absent. Existing configuration is preserved and
existing bundled skill replacements require confirmation. Beads must belong to
this Git repository and be readable; configured local branches must exist.
If your repository has no local `main`, create a config naming its intended
existing `defaultTarget` and include that branch in `targets` first. No branches, tickets, Beads settings, or commits are changed.
Review and commit the configuration and skills.

The generated config sets `enforceTargetBranch: false` and `defaultTarget: "main"`.
Tickets without target metadata use `main`; explicit targets override it. Enable
enforcement to require target metadata on every ticket. See [target operations](targets.md) for release
branches, main-checkout selection with `--repo`, and migration of existing issue branches.
`abacus skills install` remains available for skill-only installation without
requiring Beads or a targets config.

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
# Use the issue ID returned above:
abacus targets set main <returned-id>
abacus targets check <returned-id>

bd ready --json
git status --porcelain
```

If the final command shows changes you need, commit or move them now.

### B5. Validate the repository

```sh
abacus health
```

`health` is read-only. It reports tool versions, Beads storage and
configuration, worktrees, merge-slot availability, bundled skills, and runnable
agent modes. A missing merge slot is advisory; repositories may serialize
merges another way.

### B6. Start the agent

```sh
abacus run \
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

Detach with `Ctrl-b d`. Because this example explicitly names the session,
Abacus treats it as user-owned and leaves it running after shutdown.

## Path C: scale an existing repository to multiple agents

Multiple agents need both isolated Git workspaces and one shared, server-backed
Dolt database. This example keeps the primary checkout for administration and
creates four persistent agent worktrees.

For existing-data migration, backup and recovery procedures, see
[Managing Shared Dolt for Abacus](shared-dolt.md). Do not switch an embedded
database to server mode by editing configuration alone.

### C1. Configure shared Beads storage

```sh
export REPO=/path/to/your/repository
export WORKTREES=/path/to/your/repository-worktrees
export BASE=main
export BEADS_PREFIX=myproject
export BEADS_DATABASE=abacus_myproject_20260904

cd "$REPO"
bd init \
  --shared-server \
  --prefix "$BEADS_PREFIX" \
  --database "$BEADS_DATABASE" \
  --non-interactive
bd config set no-git-ops false
bd dolt start
bd dolt show --json
```

Choose a database name that is unique among projects using the shared server.
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
[Beads FAQ](https://github.com/gastownhall/beads/blob/main/docs/reference/faq.md),
[Beads Dolt documentation](https://github.com/gastownhall/beads/blob/main/docs/architecture/dolt.md),
and the [Abacus shared Dolt operations guide](shared-dolt.md).

### C4. Start the pool

Create enough independent ready issues for the pool, then run:

```sh
abacus run \
  --mode codex \
  --tmux-session abacus-work \
  --tmux-window agents \
  --model gpt-5.6-terra \
  --effort high \
  -a alice "$WORKTREES/alice" \
  -a bob "$WORKTREES/bob" \
  -a carol "$WORKTREES/carol" \
  -a dave "$WORKTREES/dave"
```

Pane-hosted runs use the `tiled` tmux layout by default. Pass
`--tmux-layout <layout>` only when you want a different supported arrangement.

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

abacus run \
  --mode opencode-server \
  --model provider/model \
  --effort high \
  --opencode-server 127.0.0.1:4096 \
  -a alice "$REPO"
```

Pass `host:port`, without an `http://` prefix. Abacus normalizes the address and
starts a directly supervised `opencode run --attach` child. It does not start,
stop, or query the server API. Add any tmux-related option if you prefer attached
clients hosted in panes; omitting the session name uses the derived default.

## Next steps

- Learn to read, pause, and operate a pool in the [Operations guide](operations.md).
- Look up filters, timeouts, finite modes, and prompt additions in the [CLI reference](cli-reference.md).
- Review the cleanup and ownership model in [Architecture and boundaries](architecture.md).
