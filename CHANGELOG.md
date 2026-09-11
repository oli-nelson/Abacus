# Changelog

Noteworthy changes to Abacus are recorded here. Add entries under Unreleased;
the release helper moves them into a dated version section and creates a fresh
Unreleased section. Released versions are listed newest first.

## [Unreleased]

### Added

- Versioned GitHub releases for Linux and macOS on x64 and ARM64, including
  self-contained binaries, SHA-256 checksums, and native version smoke tests.
- `abacus version` reports the embedded release version without requiring a
  repository or external tools; source builds report `0.0.0-dev`.
- A one-command release helper that commits the changelog rollover and atomically
  pushes the release branch and version tag.
