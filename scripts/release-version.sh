#!/usr/bin/env bash
# Shared release contract. Source this file, then call release_version <version>.
release_version() {
  local value=${1-}
  value=${value#v}
  local number='(0|[1-9][0-9]{0,3})'
  local pattern="^${number}\\.${number}\\.${number}(-(alpha|beta|rc)\\.(0|[1-9][0-9]*))?$"
  if [[ $# != 1 || ! $value =~ $pattern ]]; then
    printf 'error: use X.Y.Z or X.Y.Z-{alpha,beta,rc}.N (optional v prefix; X/Y/Z: 0-9999, no leading zeroes)\n' >&2
    return 1
  fi
  printf '%s\n' "$value"
}
