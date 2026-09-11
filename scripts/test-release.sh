#!/usr/bin/env bash
# Hermetic shell regression checks; pushes only to a disposable local bare repo.
set -Eeuo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/release-version.sh"
for value in 0.0.0 1.2.3 v1.2.3 1.2.3-alpha.0 1.2.3-beta.2 v1.2.3-rc.1; do
  [[ $(release_version "$value") == "${value#v}" ]]
done
for value in '' v 1.2 01.2.3 1.02.3 1.2.03 10000.0.0 1.2.3-rc.01 1.2.3-dev '1.2.3+meta' '1.2.3;echo bad'; do
  if release_version "$value" >/dev/null 2>&1; then
    echo "error: accepted invalid version: $value" >&2; exit 1
  fi
done
temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT
# Ignore user signing/hooks/identity configuration.
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
export GIT_AUTHOR_NAME=ReleaseTest GIT_AUTHOR_EMAIL=release@example.invalid
export GIT_COMMITTER_NAME=$GIT_AUTHOR_NAME GIT_COMMITTER_EMAIL=$GIT_AUTHOR_EMAIL
git init -q --bare "$temp/origin.git"
git init -q "$temp/repo"
mkdir "$temp/repo/scripts"
cp "$script_dir/release.sh" "$script_dir/release-version.sh" "$script_dir/release-changelog.sh" "$temp/repo/scripts/"
cd "$temp/repo"
cat > CHANGELOG.md <<'CHANGELOG'
# Changelog

## [Unreleased]

### Added
- A noteworthy feature.

## [1.0.0] - 2026-01-01

- An older release.
CHANGELOG
git add scripts CHANGELOG.md
git commit -qm initial
git remote add origin "$temp/origin.git"
expect_failure() {
  if bash scripts/release.sh "$@" >"$temp/error" 2>&1; then
    echo "error: release unexpectedly succeeded: $*" >&2; exit 1
  fi
}
expect_failure
expect_failure invalid
commit=$(git rev-parse HEAD)
expect_failure 1.0.0 # changelog already contains this version
[[ $(git rev-parse HEAD) == "$commit" ]]
[[ -z $(git status --porcelain) ]]
git checkout --detach -q
expect_failure 1.2.3
git checkout -q -

touch dirty
expect_failure 1.2.3
! git show-ref --verify --quiet refs/tags/v1.2.3
rm dirty
bash scripts/release.sh 1.2.3
branch=$(git symbolic-ref --short HEAD)
[[ $(git --git-dir="$temp/origin.git" rev-parse "$branch") == "$(git rev-parse HEAD)" ]]
[[ $(git log -1 --format=%s) == "Release 1.2.3" ]]
[[ $(git diff-tree --no-commit-id --name-only -r HEAD) == CHANGELOG.md ]]
[[ -z $(git status --porcelain) ]]
grep -Fq "## [1.2.3] - $(date -u +%Y-%m-%d)" CHANGELOG.md
grep -Fq '## [1.0.0] - 2026-01-01' CHANGELOG.md
cat > "$temp/expected.md" <<EXPECTED
# Changelog

## [Unreleased]

## [1.2.3] - $(date -u +%Y-%m-%d)

### Added
- A noteworthy feature.

## [1.0.0] - 2026-01-01

- An older release.
EXPECTED
cmp CHANGELOG.md "$temp/expected.md"
[[ $(awk '/^## / { print; if (++n == 2) exit }' CHANGELOG.md) == "$(printf '## [Unreleased]\n## [1.2.3] - %s' "$(date -u +%Y-%m-%d)")" ]]
[[ $(git --git-dir="$temp/origin.git" rev-parse 'v1.2.3^{}') == "$(git rev-parse HEAD)" ]]
expect_failure v1.2.3
git tag -d v1.2.3 >/dev/null
expect_failure v1.2.3 # remote duplicate, no local tag recreated
! git show-ref --verify --quiet refs/tags/v1.2.3
bash scripts/release.sh v1.2.4-rc.1
git remote set-url origin "$temp/missing.git"
expect_failure 1.2.4
! git show-ref --verify --quiet refs/tags/v1.2.4
git remote set-url origin "$temp/origin.git"
printf '#!/bin/sh\nexit 1\n' > "$temp/origin.git/hooks/pre-receive"
chmod +x "$temp/origin.git/hooks/pre-receive"
remote_before=$(git --git-dir="$temp/origin.git" rev-parse "$branch")
expect_failure 1.2.4
[[ $(git --git-dir="$temp/origin.git" rev-parse "$branch") == "$remote_before" ]]
git show-ref --verify --quiet refs/tags/v1.2.4 # retained for an explicit retry
! git --git-dir="$temp/origin.git" show-ref --verify --quiet refs/tags/v1.2.4
# A second release preserves the previous notes beneath the prior version.
[[ $(grep -Fc -- '- A noteworthy feature.' CHANGELOG.md) == 1 ]]
[[ $(grep -Fc '## [Unreleased]' CHANGELOG.md) == 1 ]]
# Invalid changelogs must fail before emitting any replacement.
for content in '# Missing section' '## [1.0.0]' "$(printf '## [Unreleased]\n## [Unreleased]')"; do
  printf '%s\n' "$content" > "$temp/invalid.md"
  if bash scripts/release-changelog.sh 2.0.0 "$temp/invalid.md" > "$temp/result" 2>/dev/null; then
    echo 'error: accepted malformed changelog' >&2; exit 1
  fi
  [[ ! -s "$temp/result" ]]
done
echo 'Release script tests passed.'
