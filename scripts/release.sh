#!/usr/bin/env bash
# Commit the changelog rollover, then atomically push the current branch and tag.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/release-version.sh"
if [[ $# != 1 ]]; then
  echo 'Usage: scripts/release.sh <version> (commits changelog and pushes current branch + tag to origin)' >&2
  exit 1
fi
version=$(release_version "$1")
tag="v$version"
cd "$script_dir/.."
if [[ -n $(git status --porcelain) ]]; then
  echo 'error: commit or stash all changes before releasing' >&2
  exit 1
fi
branch=$(git symbolic-ref --quiet --short HEAD) || {
  echo 'error: release from an attached branch, not detached HEAD' >&2; exit 1;
}
if git show-ref --verify --quiet "refs/tags/$tag"; then
  echo "error: local tag $tag already exists; no changes made" >&2
  exit 1
fi
remote_tag=$(git ls-remote --tags origin "refs/tags/$tag")
if [[ -n $remote_tag ]]; then
  echo "error: origin already has $tag; choose a new version" >&2
  exit 1
fi
temp=$(mktemp)
trap 'rm -f "$temp"' EXIT
bash "$script_dir/release-changelog.sh" "$version" CHANGELOG.md > "$temp"
cat "$temp" > CHANGELOG.md
git add -- CHANGELOG.md
git commit -m "Release $version" --only -- CHANGELOG.md
if [[ -n $(git status --porcelain) ]]; then
  echo 'error: release commit left changes (possibly from a hook); inspect before tagging or pushing' >&2
  exit 1
fi
git tag -a "$tag" -m "Abacus $version"
# A rejected branch update must not publish a tag for an unlanded changelog.
if ! git push --atomic origin "refs/heads/$branch:refs/heads/$branch" "refs/tags/$tag:refs/tags/$tag"; then
  echo "error: atomic push failed; release commit and local tag $tag retained. Inspect origin and branch protection; retry both refs together:" >&2
  printf 'git push --atomic origin %q %q\n' "refs/heads/$branch:refs/heads/$branch" "refs/tags/$tag:refs/tags/$tag" >&2
  exit 1
fi
echo "Pushed $branch and $tag. Follow the Release workflow in GitHub Actions."
