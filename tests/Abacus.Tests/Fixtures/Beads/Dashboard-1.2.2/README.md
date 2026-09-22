# Real Beads 1.2.2 dashboard contracts

Captured 2026-09-21 using installed `bd version 1.2.2 (Homebrew)` in a disposable
embedded Dolt repository (`/tmp/abacus-web-contract`), not the Abacus checkout.
Only generated fixture content is present; no production records or credentials.

Setup: `git init -b main`, local fixture Git name/email, then
`bd init --prefix web --non-interactive --role maintainer`. Created `web-cuh`,
updated its notes to `Camera controls added`, and added the camera-controls comment.
Those initial default writes produced commits. Subsequent writes explicitly used
`--dolt-auto-commit off`; no commit or configuration change was used to detect edits.

1. `bd --dolt-auto-commit off update web-cuh --notes 'Working-set one'`
2. `bd --readonly vc status --json` → `head-one.json`
3. `bd --readonly export` → `export-one.jsonl`
4. Update notes to `Working-set two` with the same off flag.
5. Read status/export → `head-two.json`, `export-two.jsonl`.
6. `bd --readonly history web-cuh --limit 2 --json` → `history.json`.
7. Create `web-0kh` with auto-commit off, then add a comment to `web-cuh` with
   auto-commit off. Read status/export → `head-comment.json`, `export-comment.jsonl`.
8. `bd --dolt-auto-commit off dep add web-cuh web-0kh`. Read status/export →
   `head-dependency.json`, `export-dependency.jsonl`.

All four HEAD files are identical. Export changes on each operation; comment and
edge changes do not even move `web-cuh.updated_at`. History has capitalized
`CommitHash`, `Committer`, `CommitDate`, `Issue`, is newest-first, obeys `--limit`,
and does not contain the uncommitted note values. It also repeats an identical
issue snapshot for the comment commit, without comment bodies inside `Issue`.
Do not turn every history entry into a fictional status/notes transition.

The captured dependency timestamp is two hours ahead of the actual capture time
(the local zone was UTC+02), unlike comments. Preserve source timestamps and expose
skew rather than silently correcting them or treating timestamps as topology.

These fixtures prove embedded CLI behavior only. Shared-server remote-write
reconciliation, historical compaction and mutation conflict safety still need their
own integration tests. Default exports are documented by installed help to exclude
memories and infrastructure; never add `--all` or `--include-memories` for the UI.
