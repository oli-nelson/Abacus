#!/usr/bin/env bash

# Expected successful result: main in repo/ contains the combined dashboard
# produced by all four worktrees, all four Beads tickets are closed, every
# worktree is clean, and neither Git nor Dolt has a configured remote. The
# setup script contains the detailed expected file-level outcome.
#
set -Eeuo pipefail

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

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

model="${1:-${ABACUS_MODEL:-openai/gpt-5.6-sol}}"
[[ "$model" == */* ]] || die "model must use OpenCode's provider/model format"

# This script deliberately does not create, inspect, select, or attach to tmux.
# Abacus performs its own preflight against the user-created oli:agents target.
exec "$abacus_bin" \
  --tmux-session oli \
  --tmux-window agents \
  --tmux-layout tiled \
  --model "$model" \
  -a demo-0 "$root/wt/0" \
  -a demo-1 "$root/wt/1" \
  -a demo-2 "$root/wt/2" \
  -a demo-3 "$root/wt/3"
