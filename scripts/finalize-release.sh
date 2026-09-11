#!/usr/bin/env bash
# CI-only finalization. The workflow must gate this on ALL native build/test jobs.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/release-version.sh"
fail() { echo "error: $*" >&2; exit 1; }
[[ $# == 4 ]] || fail 'Usage: finalize-release.sh <version> <branch> <tested-sha> <artifact-directory>'
[[ ${GITHUB_ACTIONS:-} == true && ${GITHUB_RUN_ID:-} =~ ^[0-9]+$ ]] || fail 'only run this from the Release workflow'
version=$(release_version "$1")
branch=$2
tested_sha=$3
assets=$(cd "$4" && pwd)
tag="v$version"
git check-ref-format "refs/heads/$branch" >/dev/null
[[ $tested_sha =~ ^[0-9a-f]{40}$ ]] || fail 'expected a full tested commit SHA'
cd "$script_dir/.."
[[ $(git rev-parse HEAD) == "$tested_sha" ]] || fail 'checkout does not match tested source'
[[ -z $(git status --porcelain) ]] || fail 'finalization requires a clean checkout'
command -v jq >/dev/null
for rid in linux-x64 linux-arm64 osx-x64 osx-arm64; do
  [[ -s "$assets/abacus-$version-$rid.tar.gz" ]] || fail "missing verified archive for $rid"
done
[[ -s "$assets/CHANGELOG.md" && -f "$assets/release-notes.md" ]] || fail 'missing prepared changelog or notes'
(
  cd "$assets"
  shasum -a 256 "abacus-$version-linux-x64.tar.gz" "abacus-$version-linux-arm64.tar.gz" \
    "abacus-$version-osx-x64.tar.gz" "abacus-$version-osx-arm64.tar.gz" > SHA256SUMS
)

# Bind retries to this workflow run, tested parent, and exact prepared changelog.
message=$(printf 'Release %s\n\nAbacus-Release-Run: %s\nAbacus-Release-Source: %s' "$version" "$GITHUB_RUN_ID" "$tested_sha")
remote_tag=$(git ls-remote --tags origin "refs/tags/$tag")
git fetch --no-tags origin "refs/heads/$branch"
remote_head=$(git rev-parse FETCH_HEAD)
if [[ -n $remote_tag ]]; then
  git fetch --no-tags origin "refs/tags/$tag"
  release_commit=$(git rev-parse 'FETCH_HEAD^{commit}')
  [[ $(git cat-file -t FETCH_HEAD) == tag ]] || fail 'existing tag is not an annotated release tag'
  [[ $(git show -s --format=%P "$release_commit") == "$tested_sha" ]] || fail 'existing tag has a different source'
  [[ $(git show -s --format=%B "$release_commit") == "$message" ]] || fail 'existing release belongs to another workflow run'
  [[ $(git diff --name-only "$tested_sha" "$release_commit") == CHANGELOG.md ]] || fail 'release commit changed more than the changelog'
  git show "$release_commit:CHANGELOG.md" | cmp - "$assets/CHANGELOG.md" || fail 'prepared changelog differs from the existing release'
  git merge-base --is-ancestor "$release_commit" "$remote_head" || fail 'release commit is not on the release branch'
  echo "Resuming publication of $tag from workflow run $GITHUB_RUN_ID."
else
  [[ $remote_head == "$tested_sha" ]] || fail 'release branch changed during testing; request a new run from its latest commit'
  cp "$assets/CHANGELOG.md" CHANGELOG.md
  git add -- CHANGELOG.md
  git -c user.name='github-actions[bot]' -c user.email='41898282+github-actions[bot]@users.noreply.github.com' \
    commit -m "$message" --only -- CHANGELOG.md
  [[ $(git show -s --format=%P HEAD) == "$tested_sha" ]] || fail 'unexpected release commit parent'
  [[ $(git diff --name-only "$tested_sha" HEAD) == CHANGELOG.md ]] || fail 'release commit changed more than the changelog'
  [[ -z $(git status --porcelain) ]] || fail 'commit hook left unexpected changes'
  git -c user.name='github-actions[bot]' -c user.email='41898282+github-actions[bot]@users.noreply.github.com' \
    tag -a "$tag" -m "$message"
  # Exact old-value lease prevents races, including branch rewinds. HEAD is
  # strictly a child of tested_sha: this never rewrites the expected history.
  git push --atomic --force-with-lease="refs/heads/$branch:$tested_sha" origin \
    "HEAD:refs/heads/$branch" "refs/tags/$tag:refs/tags/$tag" || \
    fail 'atomic push rejected; no release published. Check branch protection or concurrent changes, then rerun the failed job.'
fi

# Git updates and Release publication cannot be atomic together. Only a draft
# owned by this SAME workflow run may be resumed; published assets are immutable.
marker="<!-- abacus-release run=$GITHUB_RUN_ID source=$tested_sha -->"
notes="$assets/publish-notes.md"
cat "$assets/release-notes.md" > "$notes"
printf '\n\n%s\n' "$marker" >> "$notes"
flags=(--prerelease=false)
[[ $version != *-* ]] || flags=(--prerelease --latest=false)
if state=$(gh release view "$tag" --json isDraft,body); then
  [[ $(jq -r .body <<< "$state") == "$(cat "$notes")" ]] || fail 'existing release notes do not belong to this run'
  if [[ $(jq -r .isDraft <<< "$state") == false ]]; then
    echo "$tag is already published; nothing was overwritten."
    exit 0
  fi
else
  # If lookup failed for any reason other than absence, create fails safely
  # rather than overwriting an existing release.
  gh release create "$tag" --verify-tag --draft --notes-file "$notes" \
    --title "Abacus $version" "${flags[@]}"
fi
gh release upload "$tag" "$assets/abacus-$version-linux-x64.tar.gz" \
  "$assets/abacus-$version-linux-arm64.tar.gz" "$assets/abacus-$version-osx-x64.tar.gz" \
  "$assets/abacus-$version-osx-arm64.tar.gz" "$assets/SHA256SUMS" --clobber
gh release edit "$tag" --draft=false "${flags[@]}"
