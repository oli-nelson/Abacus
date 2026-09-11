#!/usr/bin/env bash
# Request a release; never modify the checkout, create tags, or push Git refs.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/release-version.sh"
if [[ $# != 1 ]]; then
  echo 'Usage: scripts/release.sh <version> (requests the Release GitHub workflow)' >&2
  exit 1
fi
version=$(release_version "$1")
cd "$script_dir/.."
command -v gh >/dev/null || { echo 'error: install GitHub CLI and run gh auth login first' >&2; exit 1; }
[[ -z $(git status --porcelain) ]] || { echo 'error: commit and push your changes before requesting a release' >&2; exit 1; }
branch=$(git symbolic-ref --quiet --short HEAD) || {
  echo 'error: select a release branch, not detached HEAD' >&2; exit 1;
}
source_sha=$(git rev-parse HEAD)
remote_sha=$(git ls-remote --heads origin "refs/heads/$branch" | cut -f1)
[[ $remote_sha == "$source_sha" ]] || {
  echo 'error: HEAD must match the branch on origin; push/pull explicitly before requesting a release' >&2; exit 1;
}
# Resolve the destination from origin, not gh's potentially different default repo.
remote=$(git remote get-url origin)
case "$remote" in
  https://github.com/*) repository=${remote#https://github.com/} ;;
  git@*:*) repository=${remote#*:} ;; # Includes local GitHub SSH host aliases.
  *) echo 'error: origin must be a GitHub.com HTTPS or SSH remote' >&2; exit 1 ;;
esac
repository=${repository%.git}
[[ $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || {
  echo 'error: cannot determine the GitHub owner/repository from origin' >&2; exit 1;
}
repository="github.com/$repository"
gh workflow run release.yml --repo "$repository" --ref "$branch" \
  -f "version=$version" -f "expected_sha=$source_sha"
echo "Requested Abacus $version from $branch at $source_sha."
echo 'Follow Actions → Release. No local files or Git refs were changed.'
