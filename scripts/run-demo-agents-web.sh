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

server_pid=""
abacus_pid=""
server_log=""

cleanup() {
  local exit_code=$?

  trap - EXIT INT TERM

  if [[ -n "$abacus_pid" ]] && kill -0 "$abacus_pid" 2>/dev/null; then
    kill -TERM "$abacus_pid" 2>/dev/null || true
    wait "$abacus_pid" 2>/dev/null || true
  fi

  if [[ -n "$server_pid" ]] && kill -0 "$server_pid" 2>/dev/null; then
    kill -TERM "$server_pid" 2>/dev/null || true
    wait "$server_pid" 2>/dev/null || true
  fi

  [[ -z "$server_log" ]] || rm -f "$server_log"
  exit "$exit_code"
}

handle_signal() {
  local signal=$1
  local exit_code=$2

  trap - "$signal"
  if [[ -n "$abacus_pid" ]] && kill -0 "$abacus_pid" 2>/dev/null; then
    kill -"$signal" "$abacus_pid" 2>/dev/null || true
  fi

  exit "$exit_code"
}

trap cleanup EXIT
trap 'handle_signal INT 130' INT
trap 'handle_signal TERM 143' TERM

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
command -v opencode >/dev/null 2>&1 || die "opencode is not available on PATH"

model="${1:-${ABACUS_MODEL:-openai/gpt-5.6-sol}}"
[[ "$model" == */* ]] || die "model must use OpenCode's provider/model format"

opencode_host="127.0.0.1"
opencode_port="${ABACUS_OPENCODE_PORT:-4096}"
[[ "$opencode_port" =~ ^[0-9]+$ ]] || die "ABACUS_OPENCODE_PORT must be a number"
(( 10#$opencode_port >= 1 && 10#$opencode_port <= 65535 )) || die "ABACUS_OPENCODE_PORT must be between 1 and 65535"

server_is_ready() {
  (exec 3<>"/dev/tcp/$opencode_host/$opencode_port") 2>/dev/null
}

server_address="$opencode_host:$opencode_port"
server_is_ready && die "cannot start OpenCode web: $server_address is already in use"

server_log=$(mktemp "${TMPDIR:-/tmp}/abacus-opencode-web.XXXXXX")
printf 'Starting OpenCode web at http://%s (log: %s)\n' "$server_address" "$server_log"

# exec keeps server_pid tied to the actual OpenCode process rather than an
# intermediate subshell. The EXIT trap stops it whenever Abacus or this script
# exits.
(
  cd "$root/repo"
  exec opencode web --hostname "$opencode_host" --port "$opencode_port"
) >"$server_log" 2>&1 &
server_pid=$!

server_ready=false
for ((attempt = 0; attempt < 100; attempt++)); do
  if ! kill -0 "$server_pid" 2>/dev/null; then
    server_status=0
    wait "$server_pid" || server_status=$?
    cat "$server_log" >&2
    die "OpenCode web exited before becoming ready (exit $server_status)"
  fi

  if server_is_ready; then
    server_ready=true
    break
  fi

  sleep 0.1
done

if [[ "$server_ready" != true ]]; then
  cat "$server_log" >&2
  die "timed out waiting for OpenCode web at $server_address"
fi

printf 'OpenCode web is ready; starting Abacus\n'
printf 'In OpenCode Web, use the project menu to Enable workspaces, then expand wt/0 through wt/3 to see agent sessions.\n'
"$abacus_bin" \
  --model "$model" \
  --opencode-server "$server_address" \
  --append-agent-prompt "NEVER use the git-commit-staged skill" \
  -a demo-0 "$root/wt/0" \
  -a demo-1 "$root/wt/1" \
  -a demo-2 "$root/wt/2" \
  -a demo-3 "$root/wt/3" &
abacus_pid=$!

while kill -0 "$abacus_pid" 2>/dev/null && kill -0 "$server_pid" 2>/dev/null; do
  sleep 0.2
done

if ! kill -0 "$server_pid" 2>/dev/null; then
  server_status=0
  wait "$server_pid" || server_status=$?
  server_pid=""

  if kill -0 "$abacus_pid" 2>/dev/null; then
    kill -TERM "$abacus_pid" 2>/dev/null || true
    wait "$abacus_pid" 2>/dev/null || true
  fi
  abacus_pid=""

  cat "$server_log" >&2
  die "OpenCode web exited while Abacus was running (exit $server_status)"
fi

abacus_status=0
wait "$abacus_pid" || abacus_status=$?
abacus_pid=""
exit "$abacus_status"
