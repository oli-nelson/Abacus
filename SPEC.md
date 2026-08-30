# Abacus

Abacus is a simple agent orchestrator built on top of [Beads](https://github.com/gastownhall/beads).

It uses:

- Beads for task management
- OpenCode for running agents
- tmux for managing agent processes

## Setup

Before running Abacus:

1. Set up a Beads project in your Git repository.
2. Start a tmux session.
3. Optionally, start an OpenCode server.

### Agent workspaces

Each agent is assigned a Git workspace. This can be:

- The main repository directory
- A Git worktree
- A separate clone of the repository

Multiple agents must not use the same workspace directory.

### Beads database

A single agent can use a normal local Beads database.

Multiple agents must use the same shared Dolt database so task claims are atomic and immediately visible to every agent. Abacus should refuse to start multiple agents if the Beads project is not configured this way.

## Usage

Start agents in an existing tmux session:

```sh
abacus --tmux-session <session_name> \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

Each agent runs in its own tmux pane as an OpenCode instance, using its assigned Git workspace.

To connect the agents to an existing OpenCode server:

```sh
abacus --tmux-session <session_name> \
  --opencode-server 127.0.0.1:1234 \
  -a <agent_name> <git_workspace_path> \
  -a <agent_name> <git_workspace_path>
```

The OpenCode instances still run in separate tmux panes, but each starts a new client session connected to the specified server.

## Agent workflow

Each Abacus agent follows this loop:

1. In single-agent mode, pull the latest Beads data if a remote is configured. Agents using a shared database already see the latest data.
2. Abacus atomically claims a ready task using the agent name as the actor:

   ```sh
   BEADS_ACTOR=<agent_name> bd ready --claim --json
   ```

3. Create or check out an `abacus/<issue_id>` branch in the assigned workspace.
4. Make sure the workspace has no local changes before starting OpenCode.
5. Start OpenCode with `BEADS_ACTOR=<agent_name>` and a prompt describing the issue and its ticket-state responsibilities.
6. While OpenCode is running, Abacus monitors the ticket status through Beads.
7. The OpenCode agent does the work and changes the ticket status when it is finished:

   - Close it after the work has been completed and merged.
   - Return it to `open` if the work should be retried by another agent.
   - Mark it `blocked` if it cannot continue without outside help.

8. Changing the ticket from `in_progress` signals that the OpenCode session is finished. Abacus stops the OpenCode process.
9. Whenever an OpenCode process ends, Abacus runs `bd dolt push` if a remote is configured. This ensures the final ticket update is pushed even if the agent did not push it.
10. Abacus returns to the start of the loop.

An unexpected OpenCode exit that leaves the ticket `in_progress` must not be treated as completed work.
If abacus sees that the ticket is still `in_progress` after OpenCode exits, it should log a warning, reopen the ticket (along with a `bd dolt push` if applicable) and return to the start of the loop.

## OpenCode prompt template

```text
You are <agent_name>, working on Beads ticket <issue_id> in <workspace_path>.

Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
name. Do not claim another ticket.
Read the ticket with:

  bd show <issue_id> --json

Work on the branch abacus/<issue_id> and satisfy the ticket's definition of done.
Commit your changes, then use the repository's serialized merge process to merge
the branch into the latest main branch.

When you are completely finished, update the ticket:

- Success:
    bd close <issue_id> --reason "<summary of completed work>" --json
- Work should be retried:
    bd update <issue_id> --status open --append-notes "<reason>" --json
- Work is blocked:
    bd update <issue_id> --status blocked --append-notes "<blocker>" --json

Changing the ticket from in_progress tells Abacus to end this session. Make the
status change one of your final actions, after all code, commits, merges, and
ticket notes are complete.
```
