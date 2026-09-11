# Releasing Abacus

## Create a release

Commit and push the release workflow and source changes first. The workflow must
exist on GitHub's default branch to support manual dispatch. Keep noteworthy
changes under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md); do not roll
over that section or create a version tag yourself.

Choose either entry point:

- **GitHub:** Actions → Release → Run workflow. Select the release branch
  (normally `main`) and enter a version such as `0.1.1`. Leave `expected_sha`
  blank unless you want to require an exact source commit.
- **CLI:** install/authenticate the GitHub CLI with `gh auth login`, then run:

  ```sh
  bash scripts/release.sh 0.1.1
  ```

The local helper requires a clean, attached branch whose HEAD exactly matches
that branch on origin. It sends the version, branch, and expected source SHA to
GitHub Actions. It **never edits files, commits, tags, or pushes**. Explicitly
push your source changes before requesting the release. GitHub.com HTTPS and
SSH origins (including local SSH host aliases) are supported.

### What happens in Actions

1. **Validate and prepare:** pin the dispatch commit, validate the version and
   optional SHA guard, reject existing version tags, and prepare a changelog
   rollover plus release notes as temporary workflow artifacts. No tracked file,
   branch, tag, or GitHub Release is changed.
2. **Build and test:** run the shell regression tests and full .NET suite on four
   native runners, all checked out at that same source SHA. Publish self-contained
   executables and verify each extracted archive's `abacus version` output.
3. **Finalize only after all four jobs succeed:** check that the release branch
   still points at the tested SHA. Move Unreleased into a dated version, insert
   a fresh empty Unreleased section, commit only CHANGELOG.md, and create the
   annotated version tag. Push the changelog commit and tag atomically.
4. **Publish:** create a draft GitHub Release using the prepared notes, upload all
   four archives plus `SHA256SUMS`, then publish it.

**A validation, build, or test failure leaves the repository changelog and tags
unchanged.** A branch change during testing prevents first-time finalization.
The push uses an exact old-value lease to reject races, including branch rewinds;
the new commit must be a direct child of the tested source, never a history rewrite.

The tagged commit differs from the tested commit **only in CHANGELOG.md**; the
binaries are built from the tested source with the requested version embedded.
Release runs are serialized within the repository. Tag pushes no longer trigger
this workflow: finalization and publication happen within the same run.

For version `0.1.1`, the downloads are:

| Platform | Architecture | Asset |
| --- | --- | --- |
| Linux | Intel/AMD x64 | `abacus-0.1.1-linux-x64.tar.gz` |
| Linux | ARM64 | `abacus-0.1.1-linux-arm64.tar.gz` |
| macOS | Intel x64 | `abacus-0.1.1-osx-x64.tar.gz` |
| macOS | Apple Silicon | `abacus-0.1.1-osx-arm64.tar.gz` |

Versions are `X.Y.Z`, optionally prefixed with `v`. Previews use
`X.Y.Z-alpha.N`, `X.Y.Z-beta.N`, or `X.Y.Z-rc.N`. Preview releases are marked
prerelease and never latest. Core components must be 0–9999; numbers cannot have
leading zeroes. Other suffixes and build metadata are deliberately unsupported.

The version input supplies the MSBuild `Version` and final `v<version>` tag;
there is no version file to bump. `abacus version` reads embedded assembly
informational metadata, not Git or a sidecar file. Source builds default to
`0.0.0-dev`, and commit hashes are not appended.

### Permissions and protected branches

The caller needs permission to dispatch Actions. Only the final publish job has
`contents: write`; it uses the scoped `GITHUB_TOKEN` to push the changelog/tag
and manage GitHub Releases. No extra workflow secret is needed when repository
policy allows those operations. Earlier jobs have read-only repository access.

Branch and tag rules still apply: this flow does **not** bypass required PRs,
reviews, signing, or other protections. If the automation cannot push to the
selected branch, finalization fails without changing either remote ref. Use a
release branch your policy permits automation to update, and merge its changelog
back through your normal reviewed process if needed. Automatic release PRs are
not implemented. Git identity uses `github-actions[bot]`.

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
# Shell tests additionally need Git and jq; GitHub calls are faked.
bash scripts/test-release.sh
dotnet test Abacus.sln -c Release
bash scripts/package-release.sh 1.2.3 osx-arm64
```

The archive lands in `artifacts/release/`. Packaging does not create tags or
upload anything. It rejects cross-compilation so every archive is smoke-tested
natively. CI selects the SDK from `global.json`.

### Failure and retry behavior

- **Tests failed:** fix and push the source, then request a new run. You can reuse
  the version if no tag/changelog release section was created. For transient
  failures, rerun failed jobs from the existing Actions run.
- **Branch advanced during testing:** request a new run against the updated
  branch. The workflow will not merge untested changes into a release.
- **Push rejected:** fix the policy/authentication problem and rerun the failed
  publish job if the branch still matches the tested commit. Remote branch/tag
  updates are atomic; local runner changes are disposable.
- **Upload/publication failed after the Git push:** the changelog and tag remain,
  but the GitHub Release stays draft if it was created. Use **Re-run failed jobs**
  on the **same workflow run**, not another dispatch. The publish job verifies
  the release commit's run ID, tested parent, exact changelog, and membership in
  the release branch, then resumes its own draft uploads.
- **Publication succeeded but the response was lost:** the same run recognizes
  its already-published release and does nothing. Published assets are never
  deliberately overwritten; retries may replace partial assets only in that
  run's own draft.

Git updates and GitHub Release publication cannot be one atomic transaction.
Do not manually edit the draft, move tags, or change release assets while a run
is finalizing. Do not delete the draft to retry this flow. A different run cannot
take over a version already finalized by another run; unexpected ownership or
content differences fail for operator review.

Use failed-job reruns so the validated metadata and binaries remain the same.
Re-running all jobs after finalization is deliberately rejected by validation
because the version tag now exists. Keep workflow artifacts until publication
succeeds; expired artifacts require manual recovery, not blind replacement.
After success, pull the release commit into your local branch before requesting
another release.

Existing version sections and tags from the old tag-triggered flow, including
`v0.1.0`, are not rewritten automatically. Choose a new version for this flow;
do not move or reuse an existing tag.

Implementation references:
[Manual workflow dispatch](https://cli.github.com/manual/gh_workflow_run),
[GitHub hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners),
[GitHub CLI release creation](https://cli.github.com/manual/gh_release_create),
[.NET single-file publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).
