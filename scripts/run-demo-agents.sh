#!/usr/bin/env bash

# Expected successful result: main in repo/ contains the combined dashboard
# produced by all four worktrees, all four Beads tickets are closed after a user
# adds the required "acknowledged" note to the user-attention ticket, every
# worktree is clean, and neither Git nor Dolt has a configured remote. The setup
# script contains the detailed expected file-level outcome and acknowledgement
# instructions.
#
set -Eeuo pipefail

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'usage: %s <opencode|codex|claude|opencode-server> [model] [effort] [--remote]\n' "${0##*/}" >&2
  exit 2
}

(( $# >= 1 )) || usage

agent_mode=$1
shift

remote=false
positionals=()
for argument; do
  if [[ "$argument" == --remote ]]; then
    [[ "$remote" == false ]] || usage
    remote=true
  else
    positionals+=("$argument")
  fi
done
(( ${#positionals[@]} <= 2 )) || usage

case "$agent_mode" in
  opencode)
    fallback_model="openai/gpt-5.6-sol"
    ;;
  codex)
    fallback_model="gpt-5.6-sol"
    ;;
  claude)
    fallback_model="opus"
    ;;
  opencode-server)
    fallback_model="openai/gpt-5.6-sol"
    ;;
  *)
    usage
    ;;
esac

model="${positionals[0]:-${ABACUS_MODEL:-$fallback_model}}"
effort="${positionals[1]:-${ABACUS_EFFORT:-high}}"
[[ -n "$model" && "$model" != *[[:space:]]* ]] || die "model must be nonempty and contain no whitespace"
[[ -n "$effort" && "$effort" != *[[:space:]#]* ]] || die "effort must be nonempty and contain no whitespace or '#'"
if [[ "$agent_mode" == opencode || "$agent_mode" == opencode-server ]]; then
  [[ "$model" == */* ]] || die "model must use OpenCode's provider/model format"
fi
if [[ "$remote" == true && "$agent_mode" != claude ]]; then
  die "--remote is only supported for claude mode"
fi

root="$PWD"
[[ -d "$root/repo/.git" ]] || die "run this script from the abacus-demo root containing repo/ and wt/"

for index in 0 1 2 3; do
  [[ -d "$root/wt/$index" ]] || die "missing worktree: $root/wt/$index"
done

if [[ -n "${ABACUS_BIN:-}" && -x "$ABACUS_BIN" ]]; then
  abacus_bin="$ABACUS_BIN"
else
  abacus_bin="/Users/onelson/Development/ab/abacus/src/Abacus/bin/Debug/net10.0/abacus"
fi

[[ -x "$abacus_bin" ]] || die "Abacus executable is not runnable: $abacus_bin"

abacus_args=(
  --mode "$agent_mode"
  --tmux-session oli
  --tmux-window agents
  --tmux-layout tiled
  --model "$model"
  --effort "$effort"
  --append-agent-prompt "NEVER use the git-commit-staged skill"
  --notify all
  --notify-sound
)

if [[ "$remote" == true ]]; then
  abacus_args+=(--remote)
fi

if [[ "$agent_mode" == opencode-server ]]; then
  opencode_server="${ABACUS_OPENCODE_SERVER:-127.0.0.1:${ABACUS_OPENCODE_PORT:-4096}}"
  abacus_args+=(--opencode-server "$opencode_server")
fi

# This script deliberately does not create, inspect, select, or attach to tmux.
# Abacus performs its own preflight against the user-created oli:agents target.
exec "$abacus_bin" \
  "${abacus_args[@]}" \
  -a demo-0 "$root/wt/0" \
  -a demo-1 "$root/wt/1" \
  -a demo-2 "$root/wt/2" \
  -a demo-3 "$root/wt/3"
