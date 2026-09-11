#!/usr/bin/env bash
# Print a rolled-over changelog to stdout; never modify the input file.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/release-version.sh"
if [[ $# != 2 ]]; then
  echo 'Usage: scripts/release-changelog.sh <version> <CHANGELOG.md>' >&2
  exit 1
fi
version=$(release_version "$1")
file=$2
# Strict heading contract prevents accidentally releasing the wrong section.
awk -v version="$version" '
  /^## / {
    if (!first++) {
      if ($0 != "## [Unreleased]") bad = 1
    }
    if ($0 == "## [Unreleased]") unreleased++
    if (index($0, "## [" version "]") == 1) duplicate = 1
  }
  END { exit !(unreleased == 1 && !bad && !duplicate) }
' "$file" || {
  echo 'error: CHANGELOG.md must start its version sections with exactly one ## [Unreleased] and not already contain this release' >&2
  exit 1
}
awk -v version="$version" -v date="$(date -u +%Y-%m-%d)" '
  $0 == "## [Unreleased]" {
    print
    print ""
    print "## [" version "] - " date
    next
  }
  { print }
' "$file"
