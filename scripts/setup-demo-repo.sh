#!/usr/bin/env bash

# Expected repository state after all four demo agents finish successfully:
#
# - repo/ remains the clean main worktree and main contains all four changes.
# - index.html contains status cards, an activity feed, a worktree roster,
#   accessible theme controls, and the demo footer.
# - styles.css contains the responsive component styles and light/dark themes.
# - app.js renders activity timestamps and persists the selected theme.
# - all four Beads demo tickets are closed in the shared Dolt database after the
#   special ticket's work is complete and a user explicitly acknowledges it.
# - the acknowledged ticket no longer carries the abacus:needs-user-attention
#   label, and it records the observing agent's branch-state assessment.
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

Abacus's built-in agent prompt supplies the default merge process. This demo
deliberately has no project-level agent instruction file or generated merge
helper.
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

git -C "$repo" add README.md index.html styles.css app.js
git -C "$repo" commit -m "Create the Abacus demo dashboard"

(
  cd "$repo"

  # Keep the demo free of project-level agent instruction files. Abacus provides
  # the workflow and default merge guidance directly in each launched prompt.
  # Set no-git-ops explicitly because a global Beads setting may otherwise be
  # inherited by the demo and prevent agents from committing or merging.
  bd init --shared-server --setup-exclude --prefix abacus-demo --database "$beads_database" --skip-agents --quiet
  bd config set no-git-ops false
  bd config set dolt.local-only true

  # Abacus excludes gt:slot issues when claiming work. Agents use this Beads
  # gate to serialize their direct Git merges without a generated helper script.
  bd merge-slot create

  bd create "Add status summary cards" \
    --type task --priority 2 --labels demo,frontend --estimate 25 \
    --description "Add a responsive summary-card section near the top of the dashboard. Show four clearly labelled values: Ready tasks, Active agents, Merged changes, and Conflicts resolved. Use semantic HTML in index.html and add the card layout and visual treatment to styles.css. Keep the existing intro content." \
    --acceptance "The dashboard has four responsive, accessible summary cards; each card has a label and value; index.html and styles.css remain readable; the completed branch is committed and merged into main using Abacus's default merge process."

  bd create "Add the recent agent activity feed" \
    --type task --priority 2 --labels demo,frontend --estimate 30 \
    --description "Add a Recent agent activity panel to index.html with at least four entries covering a claim, edit, merge, and completion. Add suitable styles to styles.css. Enhance app.js so each activity row receives a human-friendly relative-time label from a data-minutes-ago attribute." \
    --acceptance "The page contains a semantic recent-activity list with four entries; app.js renders relative labels without dependencies; the panel is responsive and visually consistent; the completed branch is committed and merged into main using Abacus's default merge process."

  bd create "Add a worktree roster" \
    --type task --priority 2 --labels demo,frontend --estimate 25 \
    --description "Add a Worktree roster panel to index.html showing worktrees 0 through 3. Include columns or equivalent labelled fields for workspace, agent, and state. Give the four agents distinct names and states. Add accessible responsive table or list styling in styles.css." \
    --acceptance "All four worktrees are represented with agent and state information; the roster works at narrow widths and has accessible labels; styling matches the rest of the dashboard; the completed branch is committed and merged into main using Abacus's default merge process."

  bd create "Add theme controls and demo footer" \
    --type task --priority 2 --labels demo,frontend --estimate 30 \
    --description "This is the demo's user-attention checkpoint. First inspect the issue branch and comments. Determine whether commits already present from a previous run fully or partially satisfy the ticket, complete any remaining implementation, and validate the result. Add an accessible light/dark theme toggle to the page header and a compact footer explaining that the page was assembled by Abacus agents. Implement the toggle in app.js, remember the choice in localStorage, and add both theme palettes plus control/footer styles to styles.css. Do not request acknowledgement or add the abacus:needs-user-attention label until the work is complete, validated, and ready for user review. Once it is ready, add an agent comment beginning 'Branch ready for acknowledgement:' that summarizes the completed work, add the attention label, mark the ticket blocked, and stop without merging or closing it. A qualifying acknowledgement is a later comment from a human user—not the coding agent—whose entire content is exactly 'acknowledged'. The coding agent must never add, edit, forge, or simulate that comment. When an agent observes a qualifying acknowledgement, it must check whether an agent comment beginning 'Acknowledgement observed; branch state:' already exists after that acknowledgement. If none exists, this is the first observation: inspect the branch history and diff against main, then add exactly one such comment explaining whether commits inherited from a previous run satisfy all or part of the ticket and what, if anything, the current run changed. Do not duplicate that assessment if it already exists. After the assessment is recorded, ensure the attention label is removed and never re-add it, then merge the branch and close the ticket. Never merge or close before both the qualifying acknowledgement and branch-state assessment exist." \
    --acceptance "The theme toggle is keyboard accessible and updates its accessible label or pressed state; the choice survives reloads; both themes remain legible; the footer credits the parallel-agent demo; all implementation is complete and validated before the agent adds its 'Branch ready for acknowledgement:' comment or requests user attention; a human user—not the coding agent—later adds a comment whose entire content is exactly 'acknowledged'; the agent has not added, edited, forged, or simulated that comment; the first agent to observe the acknowledgement adds exactly one 'Acknowledgement observed; branch state:' comment that explains whether inherited commits satisfy all or part of the ticket and describes any current-run changes; the attention label is absent at completion; the branch is not merged and the issue is not closed until the acknowledgement and branch-state comment exist; the completed branch is merged into main using Abacus's default merge process and the issue is closed."
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
printf '\nOne demo ticket instructs its agent to finish and validate its work before requesting user attention.\n'
printf 'After the agent reports that the branch is ready, a human user must add the exact comment "acknowledged".\n'
printf 'The agent is explicitly forbidden from adding that acknowledgement itself.\n'
printf 'After the agent flags it, find the ticket with: bd -C "%s" list --label abacus:needs-user-attention\n' "$repo"
printf 'From the main repository, acknowledge and reopen it with: abacus --resolve-attention <issue-id> "acknowledged" --reopen\n'
printf 'The first agent to observe that acknowledgement records the branch state before it merges and closes the ticket.\n'
printf 'From the demo root, run the Abacus launcher script supplied with Abacus.\n'
