# Managing Shared Dolt for Abacus

Abacus requires every agent in a multi-agent run to use the same
server-backed Beads/Dolt database. This guide covers creating that database,
migrating an existing Beads project, routine operations, troubleshooting, and
rolling back to the Dolt commit printed by Abacus.

> [!IMPORTANT]
> Abacus validates and uses shared Dolt, but normal orchestration does not
> create, migrate, repair, start, or stop it. The only exception is
> `abacus --init-new-multi-agent-repo`, which initializes a new local shared
> server project.

The commands here target Beads 1.2.2, Abacus's minimum supported version. Check
the upstream [Beads Dolt guide](https://github.com/gastownhall/beads/blob/main/docs/architecture/dolt.md)
and your installed command help before performing destructive maintenance.

## Understand the topology

There are three separate concepts:

- **Embedded Dolt** runs inside `bd` and is safe for one writer. Abacus accepts
  it only for a single-agent run.
- **Server-backed Dolt** permits concurrent clients. Every workspace in one
  Abacus pool must report the same normalized host, port, and database.
- **Shared-server mode** lets several Beads projects use one local Dolt server,
  normally under `~/.beads/shared-server/` on port 3308. Each project still
  needs its own unique database name.

A shared server is not a Dolt remote. The server provides live concurrency on
one machine; a remote provides push/pull replication and off-machine recovery.
Abacus-created projects set `dolt.local-only=true`, so they do not configure a
remote unless you deliberately add one later.

## Create a new shared-server project

### Let Abacus create the complete layout

For a brand-new project, prefer:

```sh
abacus --init-new-multi-agent-repo my-project 4
```

The initializer creates a unique database name, configures shared-server Beads
with `no-git-ops=false`, marks the database local-only, creates a merge slot,
and creates four detached Git worktrees. See
[Getting started](getting-started.md#path-a-create-a-new-multi-agent-project)
for the resulting directory layout and launchers.

### Initialize Beads in an existing repository

Stop other Beads writers, choose a unique prefix and database name, then run:

```sh
cd /path/to/repository

bd init \
  --shared-server \
  --setup-exclude \
  --prefix myproject \
  --database abacus_myproject_20260904 \
  --skip-agents \
  --non-interactive \
  --role maintainer

bd config set no-git-ops false
bd config set dolt.local-only true   # omit this when configuring a remote
bd merge-slot create
bd dolt start
```

Use a different `--database` value for every distinct project on the shared
server. Reusing a database name makes two repositories share issue state and is
not a way to create an empty project.

Verify the result:

```sh
bd dolt show --json
bd dolt status
bd dolt test
bd doctor --server
abacus --health
```

`bd dolt show --json` should report `embedded: false`, `connection_ok: true`,
and the intended host, port, and database. `abacus --health` should report the
server-backed database as available for multi-agent use.

## Attach Git worktrees

Linked worktrees normally discover the main repository's Beads workspace; do
not run `bd init` independently in every worktree. Verify discovery and identity
from each one:

```sh
git -C /path/to/repository worktree list

for workspace in /path/to/worktrees/*; do
  printf '\n%s\n' "$workspace"
  bd -C "$workspace" where
  bd -C "$workspace" dolt show --json
done
```

All agent workspaces must report the same database, host, and port. An embedded
result or a different database means the workspace is not ready for the same
Abacus pool. Run the exact pool configuration with `abacus --check` before
allowing claims.

## Migrate existing Beads storage to a shared server

Use this procedure to move either embedded storage or a per-project server to a
shared server. Use a Dolt-native backup and restore; do not merely change
`dolt.mode` or copy individual Dolt files. `bd backup` preserves branches,
commit history, working-set state, and non-issue tables, while `bd export` does
not. This follows the upstream
[backend migration procedure](https://github.com/gastownhall/beads/blob/main/docs/architecture/dolt.md#migrating-between-backends).

The following is an in-place cutover with the old `.beads` directory retained
as an additional safety copy. Choose backup and archive paths outside the
repository's `.beads` directory.

1. Stop Abacus and every other process that can write this Beads database.
2. Check and capture the source:

   ```sh
   cd /path/to/repository
   bd dolt show --json
   bd vc status --json
   bd doctor --deep

   BACKUP=/absolute/path/to/backups/myproject-pre-shared
   AUDIT=/absolute/path/to/backups/myproject-pre-shared.jsonl
   bd backup init "$BACKUP"
   bd backup sync
   bd export --all -o "$AUDIT"
   ```

   If a backup destination is already configured, inspect it with
   `bd backup status` and run `bd backup sync` instead of registering a new
   destination. The JSONL file is only an audit export; the native backup is the
   restore source.

3. Archive the old local configuration and initialize the destination mode:

   ```sh
   bd dolt stop
   mv .beads ../myproject-beads-before-shared

   bd init \
     --shared-server \
     --setup-exclude \
     --prefix myproject \
     --database abacus_myproject_20260904 \
     --skip-agents \
     --non-interactive \
     --role maintainer
   bd config set no-git-ops false
   bd config set dolt.local-only true
   ```

4. Restore the native backup and verify it before removing the archive:

   ```sh
   bd backup restore --force "$BACKUP"
   bd dolt show --json
   bd vc status --json
   bd doctor --deep
   bd list --all
   bd merge-slot check
   abacus --health
   ```

   Create a merge slot with `bd merge-slot create` if the project did not
   already have one. Compare issue counts and representative issues with the
   audit export, then run `abacus --check` against every intended workspace.

Keep the native backup and archived `.beads` directory until the new shared
database has been exercised and independently backed up. Review and commit any
intended tracked `.beads` configuration changes.

## Upgrade Beads and apply schema migrations

A shared database must be migrated once, not once per worktree or client.
Schedule a maintenance window:

1. Pause claims, let active agents finish, then stop Abacus and all other
   writers for that database.
2. Before replacing the current `bd` binary, push/pull any configured remote and
   take a native backup plus `bd export --all` audit export.
3. Install the same supported Beads version for every client.
4. Inspect and apply the schema migration from one designated workspace:

   ```sh
   bd migrate --inspect --json
   bd migrate --dry-run
   bd migrate
   bd doctor --deep
   ```

5. Restart the shared server if the Beads upgrade requires it, validate every
   database hosted by that server, then run `abacus --health` and
   `abacus --check` before resuming claims.

For a database replicated to multiple clones, only one designated clone should
migrate and publish the new schema. Other clones adopt the migrated database;
do not independently migrate them. Follow the upstream
[remote-backed migration procedure](https://github.com/gastownhall/beads/blob/main/docs/getting-started/upgrading.md#remote-backed-databases-and-multiple-clones).

## Routine operations

Use these read-only checks first:

```sh
bd dolt show --json       # resolved mode, identity, and connection result
bd dolt status            # managed-server state and endpoint
bd dolt test              # direct connection test
bd vc status --json       # branch, full HEAD commit, and working-set state
bd doctor --server        # server-specific checks
bd doctor --deep          # graph and data-integrity checks
abacus --health           # Abacus readiness classification
```

Before planned maintenance:

```sh
bd backup status
bd backup sync
bd export --all -o /absolute/path/to/issues-audit.jsonl
```

Use `bd backup` for restoration. JSONL is useful for inspection and
interchange, but it is not a full database backup.

### Restart the shared server

First stop Abacus and other writers for every project using the shared server.
Then, from any configured project:

```sh
bd dolt stop
bd dolt start
bd dolt status
bd dolt test
```

Because the next `bd` command can auto-start a Beads-managed server, keep the
maintenance window quiet until validation is complete.

## Troubleshooting

### Connection refused or server unavailable

```sh
bd dolt show --json
bd dolt status
bd dolt test
bd doctor --server
```

If the configuration is correct, stop all writers and restart the shared
server. Check for environment overrides such as `BEADS_DOLT_SERVER_HOST`,
`BEADS_DOLT_SERVER_PORT`, and `BEADS_DOLT_SHARED_SERVER`; a stale override can
send one workspace to a different endpoint than the project configuration.

### Abacus reports mismatched Dolt identities

Run `bd -C <workspace> dolt show --json` for every configured workspace and
compare `embedded`, `host`, `port`, and `database`.

- If one workspace is embedded, migrate the project rather than bypassing
  Abacus's check.
- If endpoints differ, remove the unintended environment override or repair the
  workspace configuration.
- If database names differ, determine which database contains authoritative
  work before changing anything. Back up both databases; do not point one at
  the other just to make preflight green.

### Embedded lock contention

`database is locked` in an embedded project means concurrent processes are
competing for a single-writer database. Stop the extra writers and use the
[shared-server migration](#migrate-existing-beads-storage-to-a-shared-server)
before running an Abacus pool.

### Queries fail or data looks inconsistent

Stop writers and take a native backup before repair:

```sh
bd doctor --deep
bd doctor --dry-run
bd doctor --fix
bd doctor --deep
```

Do not delete `~/.beads/shared-server/` or a database directory as a first
response: that server root can contain several unrelated projects. If repair is
not sufficient, restore the affected database from `bd backup`, not the entire
shared-server directory. See the upstream
[database troubleshooting guidance](https://github.com/gastownhall/beads/blob/main/docs/architecture/dolt.md#troubleshooting).

## Roll back to the commit reported by Abacus

At startup Abacus records the current full Dolt `HEAD`. A single-agent run with
a remote pulls first; a multi-agent shared-server run reads the live server
without pulling. The shutdown summary labels this value:

```text
Initial Beads Dolt commit  pjmrvjigiph28prpf6ir4uv0tuv88vnn
```

This is the **initial** commit for that Abacus run, not the final commit and not
a checkpoint created by Abacus. Resetting to it rolls back the entire Beads
database, including unrelated human or agent writes made after startup.

> [!CAUTION]
> A hard reset is destructive and database-wide. Stop every writer, confirm the
> database identity, make a native backup, and preserve the current `HEAD` with
> a tag before resetting. Do not perform this while Abacus is running.

### 1. Quiesce and identify the database

Pause new claims with Shift-Tab, let active tickets reach a terminal state,
then stop Abacus with Ctrl-C. Stop any other agents, shells, hooks, or services
that can write this database.

From the repository, capture the connection information and confirm the target
commit:

```sh
bd dolt show --json
bd vc status --json
bd diff pjmrvjigiph28prpf6ir4uv0tuv88vnn HEAD
bd dolt remote list --json
```

Replace the example hash everywhere below with the full value from the Abacus
summary. Review the diff: every change shown after that commit will be removed.

### 2. Back up the current state

```sh
bd backup status
bd backup sync
bd export --all -o /absolute/path/to/pre-rollback-issues.jsonl
```

If no native destination is configured, first run
`bd backup init /absolute/path/to/pre-rollback-native`, then `bd backup sync`.

### 3. Connect to the exact shared database

Use the host, port, user, and database printed by `bd dolt show --json`. For a
typical local shared server:

```sh
dolt version
bd dolt status

dolt \
  --host=127.0.0.1 \
  --port=3308 \
  --user=root \
  --use-db=abacus_myproject_20260904 \
  sql
```

Use your configured Dolt profile or credential mechanism when the server
requires authentication; avoid putting passwords in shell history.

Confirm that the running server uses the Dolt version supported by your Beads
release before continuing. Beads 1.2.2 pins Dolt 2.2.0 and its current Dolt
guide warns that 2.3.x can return `context canceled` from hard reset. Upgrade or
downgrade to the supported pin and restart the server rather than testing a
destructive reset on live data.

Inside the SQL shell, confirm the working set is clean and the target exists:

```sql
SELECT * FROM dolt_status;
SELECT commit_hash, date, message
FROM dolt_log
WHERE commit_hash = 'pjmrvjigiph28prpf6ir4uv0tuv88vnn';
SELECT HASHOF('HEAD') AS current_head;
```

`dolt_status` must return no rows. If it is dirty, stop and identify the writer
or commit/resolve the pending work before continuing.

### 4. Tag the current head and reset

Choose a unique tag name. The tag keeps the pre-rollback history reachable and
provides a quick way to undo a mistaken rollback:

```sql
CALL DOLT_TAG(
  'pre_abacus_rollback_20260904_1530',
  'HEAD',
  '-m',
  'State before rollback to Abacus baseline'
);

SELECT tag_name, tag_hash FROM dolt_tags;

CALL DOLT_RESET(
  '--hard',
  'pjmrvjigiph28prpf6ir4uv0tuv88vnn'
);

SELECT HASHOF('HEAD') AS current_head;
SELECT * FROM dolt_status;
```

Exit the SQL shell, then verify through Beads:

```sh
bd vc status --json
bd doctor --deep
bd list --all
bd ready --json
abacus --health
```

The current commit must equal the Abacus baseline and the working set must be
clean. Inspect the affected tickets before restarting Abacus.

To undo the rollback before removing the safety tag, repeat the outage and
backup steps, then run:

```sql
CALL DOLT_RESET('--hard', 'pre_abacus_rollback_20260904_1530');
```

Delete the tag only after the rollback has been accepted and backed up:

```sql
CALL DOLT_TAG('-d', 'pre_abacus_rollback_20260904_1530');
```

The reset procedure follows Dolt's documented
[`DOLT_RESET`](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sql-procedures/#dolt_reset)
semantics.

### Remote-backed databases

The steps above reset the local shared database. If `bd dolt remote list`
returns a remote, the local branch now intentionally diverges from it. Do not
immediately run `bd dolt push` or let other clones push/pull: decide whether the
remote or the rollback is authoritative, coordinate every user of the remote,
and take backups on both sides. Publishing rewritten history can require a
force update followed by re-bootstrap of other clones; use the upstream Beads
remote recovery guidance for the installed version.

For a remote-backed production database, prefer a forward recovery when
possible: repair specific tickets with normal `bd` commands or use Dolt's
`DOLT_REVERT` procedure to create inverse commits without moving shared history
backward. Test the recovery against a restored backup before applying it live.

## References

- [Beads: Dolt backend, shared servers, backups, and migration](https://github.com/gastownhall/beads/blob/main/docs/architecture/dolt.md)
- [Beads: upgrading and schema migrations](https://github.com/gastownhall/beads/blob/main/docs/getting-started/upgrading.md)
- [Dolt: version-control SQL procedures](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sql-procedures/)
- [Dolt: history and working-set system tables](https://www.dolthub.com/docs/sql-reference/version-control/dolt-system-tables/)
