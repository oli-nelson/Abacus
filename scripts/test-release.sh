#!/usr/bin/env bash
# Hermetic tests: real Git against disposable local remotes, fake GitHub CLI.
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
export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
export GIT_AUTHOR_NAME=ReleaseTest GIT_AUTHOR_EMAIL=release@example.invalid
export GIT_COMMITTER_NAME=$GIT_AUTHOR_NAME GIT_COMMITTER_EMAIL=$GIT_AUTHOR_EMAIL
export GITHUB_ACTIONS=true GITHUB_RUN_ID=123 GH_REPO=example/abacus
export FAKE_GH_STATE="$temp/gh-state"
mkdir -p "$FAKE_GH_STATE" "$temp/bin"
export PATH="$temp/bin:$PATH"
cat > "$temp/bin/gh" <<'GH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$FAKE_GH_STATE/calls"
case "$1 $2" in
  'workflow run')
    printf '%s\n' "$@" > "$FAKE_GH_STATE/dispatch"
    [[ ! -e "$FAKE_GH_STATE/fail-dispatch" ]]
    ;;
  'release view')
    [[ -f "$FAKE_GH_STATE/release.json" ]] || exit 1
    cat "$FAKE_GH_STATE/release.json"
    ;;
  'release create')
    [[ ! -e "$FAKE_GH_STATE/release.json" ]] || exit 1
    while [[ $# -gt 0 ]]; do
      if [[ $1 == --notes-file ]]; then
        jq -n --rawfile body "$2" '{isDraft: true, body: $body | rtrimstr("\n")}' > "$FAKE_GH_STATE/release.json"
        break
      fi
      shift
    done
    ;;
  'release upload')
    [[ ! -e "$FAKE_GH_STATE/fail-upload" ]] || exit 1
    for arg in "$@"; do
      case "$arg" in *.tar.gz|*/SHA256SUMS) [[ -s $arg ]] ;; esac
    done
    ;;
  'release edit')
    jq '.isDraft = false' "$FAKE_GH_STATE/release.json" > "$FAKE_GH_STATE/edit.json"
    mv "$FAKE_GH_STATE/edit.json" "$FAKE_GH_STATE/release.json"
    ;;
  *) echo "unexpected gh invocation: $*" >&2; exit 1 ;;
esac
GH
chmod +x "$temp/bin/gh"
expect_failure() {
  if "$@" >"$temp/error" 2>&1; then
    echo "error: unexpectedly succeeded: $*" >&2; exit 1
  fi
}
git init -q --bare "$temp/origin.git"
git init -q -b main "$temp/repo"
mkdir -p "$temp/repo/scripts"
cp "$script_dir/release.sh" "$script_dir/release-version.sh" \
  "$script_dir/release-changelog.sh" "$script_dir/finalize-release.sh" "$temp/repo/scripts/"
cd "$temp/repo"
echo 'artifacts/' > .gitignore
cat > CHANGELOG.md <<'CHANGELOG'
# Changelog

## [Unreleased]

### Added
- A noteworthy feature.

## [1.0.0] - 2026-01-01

- An older release.
CHANGELOG
git add scripts CHANGELOG.md .gitignore
git commit -qm initial
git remote add origin "$temp/origin.git"
git push -q origin main
git --git-dir="$temp/origin.git" symbolic-ref HEAD refs/heads/main
source_sha=$(git rev-parse HEAD)
cp CHANGELOG.md "$temp/original.md"

# The local helper dispatches without committing, tagging, pushing, or editing.
# Map an SSH alias to a local bare remote, without any real network requests.
git remote set-url origin git@github-test-alias:example/abacus.git
git config "url.$temp/origin.git.insteadOf" git@github-test-alias:example/abacus.git
# remote get-url expands insteadOf; stub ONLY that read to retain the alias.
real_git=$(command -v git)
export REAL_GIT="$real_git"
cat > "$temp/bin/git" <<'GIT'
#!/usr/bin/env bash
if [[ "$*" == 'remote get-url origin' ]]; then
  echo git@github-test-alias:example/abacus.git
else
  exec "$REAL_GIT" "$@"
fi
GIT
chmod +x "$temp/bin/git"
expect_failure bash scripts/release.sh
expect_failure bash scripts/release.sh invalid
touch dirty
expect_failure bash scripts/release.sh 1.2.3
rm dirty
git checkout -q --detach
expect_failure bash scripts/release.sh 1.2.3
git checkout -q main
bash scripts/release.sh v1.2.3
grep -Fxq 'version=1.2.3' "$FAKE_GH_STATE/dispatch"
grep -Fxq "expected_sha=$source_sha" "$FAKE_GH_STATE/dispatch"
grep -Fxq 'github.com/example/abacus' "$FAKE_GH_STATE/dispatch"
[[ $(git rev-parse HEAD) == "$source_sha" && -z $(git tag) ]]
cmp CHANGELOG.md "$temp/original.md"
touch "$FAKE_GH_STATE/fail-dispatch"
expect_failure bash scripts/release.sh 1.2.3
rm "$FAKE_GH_STATE/fail-dispatch"
git commit -qm 'unpushed work' --allow-empty
expect_failure bash scripts/release.sh 1.2.3
git checkout -q --detach "$source_sha"
rm "$temp/bin/git"

# Preparation is read-only: this is all that happens before the build/test gate.
mkdir -p artifacts/release
bash scripts/release-changelog.sh 1.2.3 CHANGELOG.md > artifacts/release/CHANGELOG.md
printf '\n### Added\n- A noteworthy feature.\n' > artifacts/release/release-notes.md
cmp CHANGELOG.md "$temp/original.md"
[[ $(git --git-dir="$temp/origin.git" rev-parse main) == "$source_sha" ]]
[[ -z $(git --git-dir="$temp/origin.git" tag) ]]
expect_failure bash scripts/release-changelog.sh 1.0.0 CHANGELOG.md

# Each publish retry is a fresh checkout of the original tested source.
new_publish_checkout() {
  local name=$1
  git clone -q "$temp/origin.git" "$temp/$name"
  cd "$temp/$name"
  git checkout -q --detach "$source_sha"
  mkdir -p artifacts/release
  cp "$temp/repo/artifacts/release/"* artifacts/release/
}
new_publish_checkout missing
expect_failure bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(git rev-parse HEAD) == "$source_sha" && -z $(git tag) ]]
for rid in linux-x64 linux-arm64 osx-x64 osx-arm64; do
  echo "verified $rid" > "$temp/repo/artifacts/release/abacus-1.2.3-$rid.tar.gz"
done

# A branch advance during tests must not be incorporated into the release.
git -C "$temp/repo" push -q origin main
new_publish_checkout advanced
expect_failure bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ -z $(git --git-dir="$temp/origin.git" tag) ]]
git --git-dir="$temp/origin.git" update-ref refs/heads/main "$source_sha"

# Close the race between the branch check and push with an exact old-value lease.
new_publish_checkout race
advanced_sha=$(git -C "$temp/repo" rev-parse main)
cat > .git/hooks/pre-push <<HOOK
#!/bin/sh
git --git-dir="$temp/origin.git" update-ref refs/heads/main "$advanced_sha"
HOOK
chmod +x .git/hooks/pre-push
expect_failure bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(git --git-dir="$temp/origin.git" rev-parse main) == "$advanced_sha" ]]
[[ -z $(git --git-dir="$temp/origin.git" tag) ]]
git --git-dir="$temp/origin.git" update-ref refs/heads/main "$source_sha"

# A protected/rejected branch must not leave a remote tag or changelog change.
printf '#!/bin/sh\nexit 1\n' > "$temp/origin.git/hooks/pre-receive"
chmod +x "$temp/origin.git/hooks/pre-receive"
new_publish_checkout rejected
expect_failure bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(git --git-dir="$temp/origin.git" rev-parse main) == "$source_sha" ]]
[[ -z $(git --git-dir="$temp/origin.git" tag) ]]
[[ ! -e "$FAKE_GH_STATE/release.json" ]]
rm "$temp/origin.git/hooks/pre-receive"

# Failed upload leaves a landed release commit + draft, resumable in this run.
new_publish_checkout upload_failure
touch "$FAKE_GH_STATE/fail-upload"
expect_failure bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
release_sha=$(git --git-dir="$temp/origin.git" rev-parse 'v1.2.3^{}')
[[ $(git --git-dir="$temp/origin.git" rev-parse main) == "$release_sha" ]]
[[ $(git rev-parse "$release_sha^") == "$source_sha" ]]
[[ $(git diff --name-only "$source_sha" "$release_sha") == CHANGELOG.md ]]
[[ $(jq -r .isDraft "$FAKE_GH_STATE/release.json") == true ]]
grep -Fq "## [1.2.3] - $(date -u +%Y-%m-%d)" CHANGELOG.md
grep -Fq '## [1.0.0] - 2026-01-01' CHANGELOG.md
[[ $(grep -Fc '## [Unreleased]' CHANGELOG.md) == 1 ]]
[[ $(grep -Fc -- '- A noteworthy feature.' CHANGELOG.md) == 1 ]]

new_publish_checkout wrong_run
expect_failure env GITHUB_RUN_ID=456 bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(jq -r .isDraft "$FAKE_GH_STATE/release.json") == true ]]
new_publish_checkout wrong_artifact
echo tampered >> artifacts/release/CHANGELOG.md
expect_failure bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(jq -r .isDraft "$FAKE_GH_STATE/release.json") == true ]]
new_publish_checkout retry
rm "$FAKE_GH_STATE/fail-upload"
bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(jq -r .isDraft "$FAKE_GH_STATE/release.json") == false ]]
[[ $(git --git-dir="$temp/origin.git" rev-parse main) == "$release_sha" ]]
new_publish_checkout already_published
before=$(grep -c '^release upload' "$FAKE_GH_STATE/calls")
bash scripts/finalize-release.sh 1.2.3 main "$source_sha" artifacts/release
[[ $(grep -c '^release upload' "$FAKE_GH_STATE/calls") == "$before" ]]

for content in '# Missing section' '## [1.0.0]' "$(printf '## [Unreleased]\n## [Unreleased]')"; do
  printf '%s\n' "$content" > "$temp/invalid.md"
  expect_failure bash "$script_dir/release-changelog.sh" 2.0.0 "$temp/invalid.md"
done
echo 'Release script tests passed.'
