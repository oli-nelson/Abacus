#!/usr/bin/env bash
# Seed only Beads issues; agents, not this script, create agent-tree.html.
set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: seed-tree-demo.sh [existing-repo]

Add one epic and 21 node tasks (1 + 4 + 16) to an existing Beads project.
Defaults to the current directory. Requires bd; does not initialize a project,
write HTML, configure Abacus, create worktrees, commit, or push.

Pause Abacus while seeding. Each invocation creates a NEW epic targeting the
same agent-tree.html; do not seed twice into the same demo. On failure, inspect
the printed issue IDs before retrying (creation is not transactional).
Tickets use the configured Abacus default target. With strict target enforcement,
use `abacus targets set <branch> <ids...>` for all printed IDs before dispatch.
The root task deliberately blocks for a human-supplied custom root name. Add a
comment or append a note to that task, then reopen it. For example:
  abacus attention resolve <root-id> --message "Root node name: My Tree" --reopen
Run multiple agents in continuous mode with --label demo:agent-tree; --once and
--drain can retire idle agents before the root unlocks parallel work.
EOF
}

if [[ "${1:-}" == --help || "${1:-}" == -h ]]; then usage; exit 0; fi
if (( $# > 1 )) || [[ "${1:-}" == -* ]]; then usage >&2; exit 1; fi
command -v bd >/dev/null 2>&1 || { echo 'error: bd is required' >&2; exit 1; }
cd -- "${1:-$PWD}"
bd where >/dev/null
bd status >/dev/null
[[ ! -e agent-tree.html && ! -L agent-tree.html ]] || { echo 'error: agent-tree.html already exists' >&2; exit 1; }
trap 'echo "error: seeding stopped; inspect the issue IDs already printed before retrying" >&2' ERR

IFS= read -r -d '' common <<'EOF' || true
## Shared contract
Work only on agent-tree.html in the repository root. Every node task owns exactly
one logical node; never add a sibling, ancestor, descendant, or placeholder node.
Use your actual Abacus agent name (BEADS_ACTOR) and this task's actual Beads ID,
not the epic ID or an invented identity. Encode all string values (including the
human root name) as valid JavaScript string literals (escape < as \u003c to prevent an embedded </script> sequence).
Do not change the renderer, layout, styling, or other agents' node records.
Keep records in numeric path order: root, 1, 1.1, ..., 1.4, 2, 2.1, ..., 4.4.
The renderer determines all coordinates and edges from the node's path.

Inspect existing work first. If this ticket's node already exists from an earlier
attempt, preserve its original attribution and validate it; never add it twice.
Follow the Abacus-provided Git/merge instructions. Preserve every other node when
merging concurrent changes; never resolve conflicts by replacing the whole file.
Close only after your one-node change is merged into the bound target and any
merge slot is released. Do not claim or complete another node ticket.

## Validation
Open the HTML directly in a browser: no server, downloads, or build is needed.
Check that all existing nodes render, each label shows its agent and task ID,
and rotation changes the perspective. Check unique paths and task IDs, that
all non-root nodes have their parent present, and that your diff adds only your
assigned node (the root task alone also installs the unchanged template).
EOF

create_issue() {
  local id
  id=$(bd create "$@" --priority 2 --labels demo:agent-tree,abacus:low_reasoning --silent) || return $?
  [[ -n "$id" && "$id" != *[[:space:]]* ]] || { echo 'error: bd returned an invalid issue ID' >&2; return 1; }
  printf 'Created %s\n' "$id" >&2
  printf '%s' "$id"
}

IFS= read -r -d '' epic_body <<'EOF' || true
## Goal
Demonstrate Abacus agents independently editing and merging ONE shared HTML file,
agent-tree.html. The finished graphical 3D tree has exactly three levels:
1 root, 4 children, and 4 leaves under each child (21 nodes, 20 edges).
Every node displays the actual contributing agent name and its node-task ID.

## Structure
All 21 node tasks are direct children of this epic. Separate blocking dependencies
mirror the visual tree: each child task depends on its visual parent's task.
Do NOT reparent node tasks under other node tasks: Abacus skips a ticket with
unfinished Beads children, which would deadlock parent-first construction.
Siblings have no dependencies on each other. Different tasks are required, but
agent assignment is left to Abacus; an agent may contribute more than one node.
The root task first requests a human-supplied custom root name, then installs
the canonical self-contained HTML template. The custom name supplements (never
replaces) the root agent name and task ID.

## Definition of done
After all 21 node tasks are closed, verify the merged HTML has paths root, 1..4,
and 1.1..4.4, exactly 21 unique task IDs, 20 parent edges, visible attribution,
and working perspective rotation offline. This epic is verification only: add
zero nodes and do not redesign the file. If a defect remains, report it for user
attention rather than adding nodes or silently fixing another task's work.
EOF
epic=$(create_issue 'Demo: build a shared-file 3D agent tree' --type epic --description "$epic_body")

IFS= read -r -d '' root_body <<EOF || true
## Task: add root (level 1) — human naming checkpoint
BEFORE creating or editing HTML, inspect this ticket's notes and comments,
including their authors/history. A human user must provide a custom root node
name in a comment or appended note on THIS ticket. Suggested format:
Root node name: <custom name>
A clearly stated custom root name in another format also qualifies. A title,
description, template placeholder, agent-authored note/comment, or name from
another ticket does NOT qualify. If authorship or the intended name is ambiguous,
ask the user to clarify; do not guess. Use the latest clear human naming decision.

If no qualifying name exists, add a comment asking the user to supply one via a
comment or appended note, add abacus:needs-user-attention, mark this ticket blocked,
and stop WITHOUT creating the file, merging, or closing. Do not repeat an existing
unanswered request. Tell the user to reopen the ticket after supplying the name.
The agent MUST NOT choose, invent, supply, append, or comment a custom node name
itself to satisfy this gate, impersonate the user, or forge human approval.
Only a human can supply the naming decision. This ticket CANNOT be completed
until that human-provided name exists in its notes or comments.

When resumed, re-read notes/comments and verify the human naming decision BEFORE
proceeding. Copy the supplied name into the root record, preserving its spelling;
this implementation copy is allowed, but supplying the decision yourself is not.
Remove abacus:needs-user-attention once the naming request is satisfied; do not
request another name on retries. Record the source comment/note in your completion
summary. If the human name has not arrived, remain blocked without implementing.

Create agent-tree.html from the EXACT template below. Replace only
__ROOT_AGENT__ and __ROOT_TASK__ with your actual agent name and this ticket ID,
and __ROOT_NAME__ with the verified human-supplied custom name,
properly escaped as described below. This installs exactly ONE node: root.
The template contains no child node records; do not add any.
If the file already exists, inspect it and preserve existing work rather than
blindly overwriting it. If unrelated content occupies the path, request attention.

$common

## Canonical HTML template
~~~html
EOF
root_body+=$'\n'
IFS= read -r -d '' template <<'HTML' || true
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Abacus · Agent Tree</title>
<style>
  :root { color-scheme: dark; font: 16px system-ui, sans-serif; background: #0b1020; color: #e6efff; }
  body { margin: 0 auto; max-width: 1440px; padding: 24px; }
  h1 { margin-bottom: 8px; }
  p { color: #a8b8d8; }
  label { display: inline-flex; align-items: center; gap: 12px; margin: 8px 24px 8px 0; }
  canvas { display: block; width: 100%; height: 680px; border: 1px solid #304266; border-radius: 16px; background: #10192d; }
  li { padding: 4px; overflow-wrap: anywhere; }
  @media (max-width: 600px) { body { padding: 12px; } canvas { height: 560px; } }
</style>
</head>
<body>
<h1>Abacus · Agent Tree</h1>
<p>One file. One node per task. Three levels of shared work.</p>
<label>Rotation <input id="yaw" type="range" min="-180" max="180" value="20"></label>
<label>Tilt <input id="pitch" type="range" min="-60" max="60" value="15"></label>
<p id="summary"></p>
<canvas id="tree" role="img" aria-label="Perspective 3D agent tree; node details follow below."></canvas>
<details open><summary>Node attribution (accessible text view)</summary><ul id="roster"></ul></details>
<script>
'use strict';
// NODE RECORDS: insert only your assigned record, sorted by numeric path.
const nodes = [
  { path: 'root', parent: null, agent: '__ROOT_AGENT__', task: '__ROOT_TASK__', name: '__ROOT_NAME__' },
];
// END NODE RECORDS. Everything below is fixed; node tasks must not edit it.
const canvas = document.getElementById('tree');
const ctx = canvas.getContext('2d');
const yaw = document.getElementById('yaw');
const pitch = document.getElementById('pitch');
const ordered = [...nodes].sort((a, b) => a.path === b.path ? 0 :
  a.path === 'root' ? -1 : b.path === 'root' ? 1 :
  a.path.localeCompare(b.path, 'en', { numeric: true }));
const byPath = new Map(ordered.map(n => [n.path, n]));
if (byPath.size !== nodes.length || new Set(nodes.map(n => n.task)).size !== nodes.length)
  throw new Error('Duplicate node path or task ID');
for (const n of ordered) {
  const parent = n.path === 'root' ? null : n.path.includes('.') ? n.path.split('.')[0] : 'root';
  if (!(n.path === 'root' || /^[1-4](\.[1-4])?$/.test(n.path)) || n.parent !== parent ||
      (parent !== null && !byPath.has(parent)) || !n.agent || !n.task)
    throw new Error('Invalid node: ' + n.path);
  const li = document.createElement('li');
  li.textContent = `${n.name ?? n.path} (${n.path}) ← ${n.parent ?? 'origin'} · ${n.agent} · ${n.task}`;
  document.getElementById('roster').append(li);
}
if (!byPath.has('root')) throw new Error('Missing root');
document.getElementById('summary').textContent = `${nodes.length} / 21 nodes · ${nodes.length - 1} / 20 edges`;
function position(path) {
  if (path === 'root') return [0, -210, 0];
  const [branch, leaf] = path.split('.').map(Number);
  const angle = (branch - 1) * Math.PI / 2;
  if (!leaf) return [180 * Math.cos(angle), 0, 180 * Math.sin(angle)];
  const spread = angle + (leaf - 2.5) * 0.30;
  return [360 * Math.cos(spread), 210, 360 * Math.sin(spread)];
}
function draw() {
  const w = canvas.clientWidth, h = canvas.clientHeight, dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  const ry = Number(yaw.value) * Math.PI / 180, rx = Number(pitch.value) * Math.PI / 180;
  const fit = Math.min(w / 1100, h / 850);
  const projected = new Map(ordered.map(n => {
    const [x, y, z] = position(n.path);
    const xx = x * Math.cos(ry) + z * Math.sin(ry);
    const zz = -x * Math.sin(ry) + z * Math.cos(ry);
    const yy = y * Math.cos(rx) - zz * Math.sin(rx);
    const depth = y * Math.sin(rx) + zz * Math.cos(rx);
    const scale = 1100 / (1100 + depth);
    return [n.path, { x: w / 2 + xx * scale * fit, y: h / 2 + yy * scale * fit, depth, scale }];
  }));
  ctx.strokeStyle = '#6686b4'; ctx.lineWidth = 2;
  for (const n of ordered) {
    if (!n.parent) continue;
    const a = projected.get(n.parent), b = projected.get(n.path);
    ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
  }
  // Painter's order plus perspective, shaded spheres, and rotation expose depth.
  for (const n of [...ordered].sort((a, b) => projected.get(b.path).depth - projected.get(a.path).depth)) {
    const p = projected.get(n.path), r = 11 * p.scale;
    const color = n.path === 'root' ? '#fbbf24' : n.parent === 'root' ? '#38bdf8' : '#34d399';
    const gradient = ctx.createRadialGradient(p.x - r / 3, p.y - r / 3, 1, p.x, p.y, r);
    gradient.addColorStop(0, '#ffffff'); gradient.addColorStop(0.35, color); gradient.addColorStop(1, '#17243a');
    ctx.fillStyle = gradient; ctx.beginPath(); ctx.arc(p.x, p.y, r, 0, Math.PI * 2); ctx.fill();
    ctx.font = '12px system-ui'; ctx.textAlign = 'center'; ctx.lineWidth = 4; ctx.strokeStyle = '#10192d';
    for (const [i, text] of [`${n.name ?? n.path} · ${n.agent}`, n.task].entries()) {
      ctx.strokeText(text, p.x, p.y + r + 16 + i * 15);
      ctx.fillStyle = '#e6efff'; ctx.fillText(text, p.x, p.y + r + 16 + i * 15);
    }
  }
}
yaw.addEventListener('input', draw); pitch.addEventListener('input', draw);
window.addEventListener('resize', draw);
draw();
</script>
</body>
</html>
HTML
root_body+="$template"
root_body+=$'\n~~~'
root=$(create_issue 'Tree root: install template and add root' --type task --parent "$epic" --description "$root_body")

add_node() {
  local path=$1 parent_path=$2 parent_task=$3
  local body
  IFS= read -r -d '' body <<EOF || true
## Task: add node $path under $parent_path
Prerequisite: Beads task $parent_task must be CLOSED and its node $parent_path
must already exist in the merged target's agent-tree.html. Read that ticket and
verify both conditions before editing. If the node is absent, do not recreate it:
report the mismatch and block this ticket for attention.

Add exactly this one record to the existing nodes array, replacing only the two
identity placeholders with your actual agent name and this ticket's ID:

{ path: '$path', parent: '$parent_path', agent: '__YOUR_AGENT__', task: '__YOUR_TASK__' },

Use the existing HTML; the canonical renderer/template lives in root task $root.
Never copy the root-only template over the evolving file. No other file changes.

$common
EOF
  # Bare --deps ID means this new node depends on that ID. Explicit
  # blocks:ID reverses the edge in bd 1.2.2 (the new node would block its parent).
  create_issue "Tree node $path: add child of $parent_path" --type task --parent "$epic" \
    --deps "$parent_task" --description "$body"
}

for branch in 1 2 3 4; do
  child=$(add_node "$branch" root "$root")
  for leaf in 1 2 3 4; do
    add_node "$branch.$leaf" "$branch" "$child" >/dev/null
  done
done

printf '\nCreated epic %s and 21 node tasks (1 + 4 + 16). Root: %s\n' "$epic" "$root"
printf 'All tasks share agent-tree.html; 20 blocking edges mirror its tree.\n'
printf 'The root will block until a human supplies a custom name in its notes/comments.\n'
printf 'Inspect: bd show %s --children\n' "$epic"
printf 'Run continuous Abacus agents with --label demo:agent-tree (see --help).\n'
