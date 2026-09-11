# Releasing Abacus

## Create a release

Commit and push the release-flow files to GitHub first. GitHub Actions must be
enabled, and repository/organization policy must permit the release job's
`contents: write` permission. No personal access token or extra secret is needed:
the workflow uses its scoped `GITHUB_TOKEN`.

Keep noteworthy changes under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md).
Just before each commit, agents must add concise user-facing notes there.
Released sections are immutable; no historical releases are invented when
starting the changelog.

From a clean, attached branch containing the changes you want to release
(normally `main`):

```sh
bash scripts/release.sh 1.2.3
```

The helper:

1. Validates the version, clean checkout, attached branch, changelog structure,
   and absence of an existing local/remote tag.
2. Renames the Unreleased contents to `## [1.2.3] - YYYY-MM-DD` (UTC date),
   inserting a new empty `## [Unreleased]` above it and preserving older releases.
3. Commits **only CHANGELOG.md**, with the message `Release 1.2.3`, and tags that
   commit with annotated tag `v1.2.3`.
4. Pushes the **current branch and new tag atomically** to `origin`. This includes
   any earlier unpushed commits on that branch, but no unrelated branches/tags.

Check the branch, HEAD, and origin before running it. Git authentication and
permission to push that branch and tags are required; the local helper does not
require .NET or `gh`. If branch protection requires a PR, use the manual path
below rather than bypassing protection.

### Protected-branch / manual path

Prepare the rollover on your normal PR branch:

```sh
bash scripts/release-changelog.sh 1.2.3 CHANGELOG.md > /tmp/abacus-changelog.md
# Inspect the output before replacing the source.
cp /tmp/abacus-changelog.md CHANGELOG.md
```

Commit the changelog through your normal reviewed PR process. Once merged, tag
the merged commit on the release branch and push the tag:

```sh
git tag -a v1.2.3 -m "Abacus 1.2.3"
git push origin refs/tags/v1.2.3:refs/tags/v1.2.3
```

Do not rerun the automatic helper after manually rolling over the changelog:
it rejects a version already recorded there. CI requires a matching dated
changelog section at the tagged commit; a bare tag without rollover fails validation.

Follow **Actions → Release** in GitHub. The tag push starts four native jobs:

| Platform | Architecture | Asset |
| --- | --- | --- |
| Linux | Intel/AMD x64 | `abacus-1.2.3-linux-x64.tar.gz` |
| Linux | ARM64 | `abacus-1.2.3-linux-arm64.tar.gz` |
| macOS | Intel x64 | `abacus-1.2.3-osx-x64.tar.gz` |
| macOS | Apple Silicon | `abacus-1.2.3-osx-arm64.tar.gz` |

Each job runs the test suite and shell release tests, publishes a self-contained
single executable, extracts its archive, and checks that `abacus version` prints
exactly `1.2.3`. The final job creates a draft GitHub Release using that version’s changelog entries as its release notes,
uploads all four archives plus `SHA256SUMS`, then publishes it. Build/test failures
prevent release creation; upload failures leave an unpublished draft.

Versions are `X.Y.Z`, optionally prefixed with `v`. For previews, use
`X.Y.Z-alpha.N`, `X.Y.Z-beta.N`, or `X.Y.Z-rc.N` (for example,
`bash scripts/release.sh 1.3.0-rc.1`). Preview releases are marked prerelease and
never latest. Core components must be 0–9999; numbers cannot have leading zeroes.
Other suffixes and build metadata are deliberately unsupported.

The Git tag is the release version source of truth; there is no version file to
bump. Publishing passes the tag without `v` as the .NET `Version` property.
`abacus version` reads embedded assembly informational metadata, never Git or a
sidecar file. Ordinary source builds report `0.0.0-dev`; explicit
`-p:Version=1.2.3` builds report `1.2.3`. Commit hashes are not appended.

## Download and install

Download the archive for your OS/CPU and `SHA256SUMS` from
[GitHub Releases](https://github.com/oli-nelson/Abacus/releases). For example:

```sh
# Linux (checks the downloaded archive; skips other architectures not downloaded):
sha256sum --ignore-missing -c SHA256SUMS

# macOS (select the checksum for your downloaded archive):
grep 'abacus-1.2.3-osx-arm64.tar.gz$' SHA256SUMS | shasum -a 256 -c -

tar -xzf abacus-1.2.3-osx-arm64.tar.gz
mkdir -p "$HOME/.local/bin"
install -m 755 abacus "$HOME/.local/bin/abacus"
"$HOME/.local/bin/abacus" version
# 1.2.3
```

Add `$HOME/.local/bin` to PATH if needed. No .NET installation is required.
The usual OS native runtime libraries and the external tools needed for actual
orchestration are still required. Linux artifacts target glibc distributions,
not Alpine/musl. macOS binaries are not Developer ID signed or notarized;
Gatekeeper may require explicit approval in System Settings → Privacy & Security.
There is no installer, automatic updater, or signing credential setup in this flow.

## Local verification and recovery

On a matching native OS/CPU, with the .NET SDK installed:

```sh
bash scripts/test-release.sh
dotnet test Abacus.sln -c Release
bash scripts/package-release.sh 1.2.3 osx-arm64
```

The archive lands in `artifacts/release/`. Packaging does not create tags or
upload anything. It rejects cross-compilation so every archive is smoke-tested
natively. CI selects the SDK from `global.json`.

For transient CI failures, rerun failed jobs in GitHub Actions. If the publish
job left a draft, delete **only that unpublished draft** (keep the tag), then
rerun the publish job. Existing releases are intentionally not overwritten.
Never move/reuse a published version tag; release a new patch instead. If source
changes are needed to fix a failed build, commit the fix and use a new version.
If the atomic push fails, the helper preserves the release commit and tag and
prints the exact two-ref retry command. Neither remote ref is updated by a
rejected atomic push. Inspect the remote and branch protection before retrying;
do not rerun the helper or push just the tag. Commit/signing/hook failures leave
local changes for inspection rather than automatically resetting your work.

Implementation references:
[GitHub hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners),
[GitHub CLI release creation](https://cli.github.com/manual/gh_release_create),
[.NET single-file publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).
