# Shared-file tree demo

From the Abacus source checkout, seed an **existing** demo project while its
agents are stopped or paused:

```sh
./scripts/seed-tree-demo.sh /path/to/my-project/repo
```

This adds one epic and 21 node tasks (1 root, 4 children, 16 leaves), without
creating files or configuring the project. Every task edits `agent-tree.html`;
blocking dependencies enforce parent-first construction, while all node tasks
belong directly to the epic. The root ticket embeds the fixed, offline 3D HTML
template and deliberately blocks for a human-supplied custom root name. Supply
it in a comment or appended note, then reopen the root, for example:

```sh
abacus attention resolve <root-id> --message "Root node name: Our Tree" --reopen
```

Agents cannot supply that naming decision themselves. Every node also displays
its contributing agent and task ID. Run multiple agents in continuous mode with
`--label demo:agent-tree`; `--once`/`--drain` may retire idle agents before the root
unlocks its children. Seed only once per demo; see the script's `--help` for
partial-failure and strict-target guidance.

The seeder uses bare `--deps <parent-task-id>` to make each new node depend on
its parent. Do not replace this with `--deps blocks:<parent-task-id>`: Beads
1.2.2 interprets that form in the opposite direction. Verify the real ready-queue
contract in a disposable embedded Beads project with:

```sh
ABACUS_TEST_REAL_BEADS=1 python3 -m unittest discover -s tests/scripts -v
```
