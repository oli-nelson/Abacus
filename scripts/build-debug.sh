#!/usr/bin/env bash

set -Eeuo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd -- "$script_dir/.." && pwd)

if ! command -v dotnet >/dev/null 2>&1; then
  printf 'error: required command not found: dotnet\n' >&2
  exit 1
fi

exec dotnet build "$repo_root/Abacus.sln" --configuration Debug "$@"
