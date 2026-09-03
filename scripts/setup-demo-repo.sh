#!/usr/bin/env bash

# Expected repository state after all four demo agents finish successfully:
#
# - repo/ remains the clean main worktree and main contains all four changes.
# - index.html contains status cards, an activity feed, a worktree roster,
#   accessible theme controls, and the demo footer.
# - styles.css contains the responsive component styles and light/dark themes.
# - app.js renders activity timestamps and persists the selected theme.
# - all four Beads demo tickets are closed in the shared Dolt database after a
#   user explicitly acknowledges the user-attention ticket.
# - the acknowledged ticket retains the abacus:needs-user-attention label so the
#   dashboard continues to show it until a user removes the label.
# - the Beads merge-slot gate is open and ready for another serialized merge.
# - wt/0 through wt/3 are clean and retain their abacus/<issue-id> branches.
# - the serialized merges may include conflict-resolution commits because the
#   tasks intentionally overlap in index.html, styles.css, and app.js.
# - the demo still has no Git remote and no Dolt remote.
#
set -Eeuo pipefail

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

require_command bd
require_command git

demo_root="${1:-$PWD/abacus-demo}"
case "$demo_root" in
  /*) ;;
  *) demo_root="$PWD/$demo_root" ;;
esac

[[ ! -e "$demo_root" ]] || die "destination already exists: $demo_root"

repo="$demo_root/repo"
worktrees="$demo_root/wt"
beads_database="abacus_demo_$(date -u +%Y%m%d%H%M%S)_$$"

mkdir -p "$repo" "$worktrees"

git -C "$repo" init --initial-branch=main
git -C "$repo" config user.name "Abacus Demo"
git -C "$repo" config user.email "abacus-demo@example.invalid"

cat >"$repo/README.md" <<'EOF'
# Abacus Parallel-Agent Demo

This repository is a deliberately small static dashboard used to demonstrate
four Abacus agents claiming Beads tasks, working in separate Git worktrees, and
serializing their merges back to `main`.

Open `index.html` directly in a browser to view the result. There is no build
step and no package manager.

Agents must follow the serialized merge process in `AGENTS.md`. It uses the
shared Beads merge slot and ordinary Git commands; the repository does not
contain a generated merge helper.
EOF

cat >"$repo/AGENTS.md" <<'EOF'
# Abacus demo agent instructions

This is a static HTML/CSS/JavaScript demo. There is no build step.

## Working rules

1. Run `bd prime` and read the assigned ticket with `bd show <id> --json`.
2. Before starting any implementation, check whether the ticket requires user
   acknowledgement. If it does, first add the requested user-attention label,
   then wait until the required acknowledgement comment has been added to the Beads
   issue by a human user. Adding the label is only a coordination step: do not
   edit files, write code, or otherwise begin implementation before the comment
   appears. The agent must never add, edit, forge, or simulate the
   acknowledgement comment itself.
3. Work only on the `abacus/<issue-id>` branch prepared by Abacus.
4. Keep changes focused on the assigned ticket.
5. Inspect the result and perform any lightweight checks that are useful.
6. Commit the completed change on the issue branch.
7. Acquire the repository's Beads merge slot. Wait and retry if another agent
   currently holds it:

   ```sh
   until bd merge-slot acquire --holder "$BEADS_ACTOR"; do sleep 2; done
   ```

8. While holding the slot, merge the latest `main` into the issue branch. If
   there are conflicts, resolve and commit them in this worktree:

   ```sh
   git merge main
   ```

9. Fast-forward `main` from the dedicated main worktree, then release the slot:

   ```sh
   branch="$(git branch --show-current)"
   main_worktree="$(git rev-parse --show-toplevel)/../../repo"
   git -C "$main_worktree" merge --ff-only "$branch"
   bd merge-slot release --holder "$BEADS_ACTOR"
   ```

10. Only close the Beads ticket after the fast-forward and slot release succeed.
   If the work cannot be merged, release the slot before reopening or blocking
   the ticket.

Never check out `main` in an agent worktree. The Beads merge slot plus the
documented Git commands are the repository's serialized merge process.
EOF

cat >"$repo/index.html" <<'EOF'
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Abacus Agent Dashboard</title>
    <link rel="stylesheet" href="styles.css">
  </head>
  <body>
    <header class="site-header">
      <p class="eyebrow">Parallel delivery, clearly counted</p>
      <h1>Abacus Agent Dashboard</h1>
      <p class="lede">A tiny page assembled by four agents working at once.</p>
    </header>

    <main id="dashboard" class="dashboard">
      <section class="panel intro-panel" aria-labelledby="intro-heading">
        <h2 id="intro-heading">Demo in progress</h2>
        <p>The sections below will be added by independently claimed tasks.</p>
      </section>
    </main>

    <script src="app.js"></script>
  </body>
</html>
EOF

cat >"$repo/styles.css" <<'EOF'
:root {
  color-scheme: light;
  font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
  background: #f4f1ea;
  color: #1f2933;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
  min-height: 100vh;
}

.site-header,
.dashboard {
  width: min(70rem, calc(100% - 2rem));
  margin-inline: auto;
}

.site-header {
  padding: 4rem 0 2rem;
}

.eyebrow {
  color: #9a3412;
  font-size: 0.8rem;
  font-weight: 800;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

h1,
h2 {
  line-height: 1.1;
}

h1 {
  margin: 0.5rem 0;
  font-size: clamp(2.4rem, 8vw, 5rem);
}

.lede {
  max-width: 42rem;
  color: #52606d;
  font-size: 1.15rem;
}

.dashboard {
  display: grid;
  gap: 1rem;
  padding-bottom: 4rem;
}

.panel {
  padding: 1.5rem;
  border: 1px solid #d9d2c3;
  border-radius: 1rem;
  background: #fffdf8;
  box-shadow: 0 0.75rem 2rem rgb(70 55 35 / 8%);
}
EOF

cat >"$repo/app.js" <<'EOF'
const dashboard = document.querySelector("#dashboard");

if (dashboard) {
  dashboard.dataset.ready = "true";
}
EOF

git -C "$repo" add README.md AGENTS.md index.html styles.css app.js
git -C "$repo" commit -m "Create the Abacus demo dashboard"

(
  cd "$repo"

  # --skip-agents lets the explicit setup recipes below install integrations
  # without bd init also modifying the instruction files.
  bd init --shared-server --stealth --prefix abacus-demo --database "$beads_database" --skip-agents --quiet
  bd config set dolt.local-only true

  bd setup codex
  bd setup claude
  bd setup opencode

  bd setup codex --check
  bd setup claude --check
  bd setup opencode --check

  # Abacus excludes gt:slot issues when claiming work. Agents use this Beads
  # gate to serialize their direct Git merges without a generated helper script.
  bd merge-slot create

  bd create "Add status summary cards" \
    --type task --priority 2 --labels demo,frontend --estimate 25 \
    --description "Add a responsive summary-card section near the top of the dashboard. Show four clearly labelled values: Ready tasks, Active agents, Merged changes, and Conflicts resolved. Use semantic HTML in index.html and add the card layout and visual treatment to styles.css. Keep the existing intro content." \
    --acceptance "The dashboard has four responsive, accessible summary cards; each card has a label and value; index.html and styles.css remain readable; the completed branch is committed and merged into main using the AGENTS.md serialized merge process."

  bd create "Add the recent agent activity feed" \
    --type task --priority 2 --labels demo,frontend --estimate 30 \
    --description "Add a Recent agent activity panel to index.html with at least four entries covering a claim, edit, merge, and completion. Add suitable styles to styles.css. Enhance app.js so each activity row receives a human-friendly relative-time label from a data-minutes-ago attribute." \
    --acceptance "The page contains a semantic recent-activity list with four entries; app.js renders relative labels without dependencies; the panel is responsive and visually consistent; the completed branch is committed and merged into main using the AGENTS.md serialized merge process."

  bd create "Add a worktree roster" \
    --type task --priority 2 --labels demo,frontend --estimate 25 \
    --description "Add a Worktree roster panel to index.html showing worktrees 0 through 3. Include columns or equivalent labelled fields for workspace, agent, and state. Give the four agents distinct names and states. Add accessible responsive table or list styling in styles.css." \
    --acceptance "All four worktrees are represented with agent and state information; the roster works at narrow widths and has accessible labels; styling matches the rest of the dashboard; the completed branch is committed and merged into main using the AGENTS.md serialized merge process."

  bd create "Add theme controls and demo footer" \
    --type task --priority 2 --labels demo,frontend --estimate 30 \
    --description "This is the demo's user-attention checkpoint. As the first action after reading the issue, add the abacus:needs-user-attention label yourself. Then wait for a human user to add a new issue comment whose entire content is exactly 'acknowledged'. Adding the label is a coordination step only: do not edit files, write code, or otherwise begin implementation until that comment appears. The coding agent is not allowed to add, edit, forge, or simulate the acknowledgement comment under any circumstances; it must come from the user. After the user acknowledges the issue, add an accessible light/dark theme toggle to the page header and a compact footer explaining that the page was assembled by Abacus agents. Implement the toggle in app.js, remember the choice in localStorage, and add both theme palettes plus control/footer styles to styles.css. Keep the attention label on the issue through completion." \
    --acceptance "Before any implementation starts, the coding agent has added the abacus:needs-user-attention label and a human user—not the coding agent—has added an issue comment whose entire content is exactly 'acknowledged'; the agent has not added, edited, forged, or simulated that comment; after acknowledgement, the theme toggle is keyboard accessible and updates its accessible label or pressed state; the choice survives reloads; both themes remain legible; the footer credits the parallel-agent demo; the attention label remains present; the completed branch is committed and merged into main using the AGENTS.md serialized merge process and the issue is closed."

  git add AGENTS.md CLAUDE.md .agents .claude .codex 2>/dev/null || true
  if [[ -n "$(git status --porcelain)" ]]; then
    git commit -m "Configure Beads agent integrations"
  fi
)

original_umask="$(umask)"
umask 077
for index in 0 1 2 3; do
  git -C "$repo" worktree add "$worktrees/$index" -b "demo/worker-$index" main
  if [[ -d "$worktrees/$index/.beads" ]]; then
    chmod 700 "$worktrees/$index/.beads"
  fi
done
umask "$original_umask"

[[ -z "$(git -C "$repo" status --porcelain)" ]] || die "main worktree is dirty after setup"

for index in 0 1 2 3; do
  git -C "$worktrees/$index" reset --hard main >/dev/null
  [[ -z "$(git -C "$worktrees/$index" status --porcelain)" ]] || \
    die "worktree $index is dirty after setup"
done

printf '\nDemo repository created at %s\n' "$demo_root"
printf 'Main repository: %s\n' "$repo"
printf 'Worktrees:       %s/{0,1,2,3}\n' "$worktrees"
printf 'Dolt database:   %s\n' "$beads_database"
printf '\nBeads status:\n'
bd -C "$repo" status
printf '\nNo Git or Dolt remote was configured.\n'
printf '\nOne demo ticket instructs its agent to add abacus:needs-user-attention before starting work.\n'
printf 'Before its agent may start any implementation, a human user must add the exact comment "acknowledged".\n'
printf 'The agent is explicitly forbidden from adding that acknowledgement itself.\n'
printf 'After the agent flags it, find the ticket with: bd -C "%s" list --label abacus:needs-user-attention\n' "$repo"
printf 'A human can acknowledge it with: bd -C "%s" comment <issue-id> "acknowledged"\n' "$repo"
printf 'From the demo root, run the Abacus launcher script supplied with Abacus.\n'
