# Ticket targets and safe branch preparation

The controller's `enforceTargetBranch` boolean defaults to **false**. When false,
a ticket without `metadata.abacus_target` uses `defaultTarget` (which defaults to
`main`). An explicit target overrides the default. When true, every work ticket
requires its own nonempty string target, including epics, decisions, and closed
history. Internal Beads records labelled `gt:slot` are exempt.

Defaults apply only to an absent target key. Explicit null, non-string, empty,
or unconfigured targets are invalid in either mode. There is no label routing
or implicit parent/dependency inheritance. A backport is a separate linked ticket.

Abacus still does not perform merges or verify delivery. Agents merge into the
bound local destination and own ticket outcomes, as before. No fetch or Git push
has been added.

## Configure the controller

Run `abacus init` in an existing Git/Beads project to install skills and create
a default `.abacus/targets.json` allowing `main` only if absent. It validates
Beads and local target branches before installing; it never overwrites existing
configuration or assigns ticket targets. Alternatively, create the file manually
in the main checkout selected by `--repo <path>`. For example:

```json
{
  "version": 1,
  "enforceTargetBranch": false,
  "defaultTarget": "main",
  "targets": {
    "main": {},
    "release/1.2": {
      "mergeInstructions": "merge/release.md"
    }
  }
}
```

No repository ID is required or recorded. Legacy `repositoryId` configuration
and `repository` binding fields are ignored for compatibility; existing files
need not be rewritten. The new-repository initializer allows `main`, commits the
config, and includes it in every worktree. Its shared run config selects
`"repo": "repo"` relative to the project-root config file.

The controller must be the **main checkout**, never a linked worktree. With no
`--repo`, the current directory must be inside that checkout. To invoke from
an outer project folder, a linked worktree, or elsewhere, pass
`--repo /path/to/main-checkout`. Config always comes from
`<repo>/.abacus/targets.json`; `--config` now selects a separate run config, not a target registry. Agent `-a`
paths may still point to linked worktrees.

All configured targets must already exist as **local branches** in each assigned
workspace's repository. Abacus rejects revision expressions, remote refs,
reserved `abacus/` names, empty registries, duplicate/unknown configuration keys,
and unsupported schema versions. When enforcement is off, `defaultTarget` must
be in the allowlist; it is never guessed from JSON order or the current branch.
An explicitly supplied default must be configured even when enforcement is on.
Strict registries may omit an unused default. If your existing registry has no
`main`, set `defaultTarget` to an intended allowed branch or enable enforcement.

Instruction paths are relative to the configuration directory. A target without
its own instruction file uses `merge-instructions.md` alongside `targets.json`,
if present, otherwise the built-in merge strategy. An empty instruction file
intentionally suppresses the built-in merge section. Explicit missing files fail
validation. Custom instructions cannot authorize a different destination.

Configuration and merge instructions are loaded **once from the controller** at
startup, not from agents' changing checkouts. `.abacus/append-prompt.md` remains
workspace-specific supplemental guidance. Restart to load policy edits; existing
bindings whose relevant policy changed fail validation rather than silently
adopting the new policy. Stop work and inspect such bindings before an explicit
manual recovery; the setter does not rewrite existing bindings.

```sh
abacus health
abacus health --repo /path/to/main-checkout
```

Health checks presence, schema, instruction files, and local target branches.
Missing or invalid target configuration makes readiness fail. It does not audit
the ticket database; use the dedicated command below for that.

## Audit and set ticket metadata

Read-only audit, with exit code 0 when all selected tickets pass and 1 otherwise:

```sh
abacus targets check
abacus targets check project-123 project-456
```

Without IDs, audit every ticket, including closed tickets, excluding `gt:slot`.
The audit checks target metadata, allowed/local destinations, any execution
binding, and existing branch history. It does not require tmux, a harness, or
normal agent options and does not claim or mutate tickets or workspaces.

Pause dispatch and stop active work before setting metadata:

```sh
abacus targets set release/1.2 project-123 project-456
abacus targets check project-123 project-456
```

Both commands accept `--repo <path>` and run Beads at that selected main checkout.
The setter validates the entire selected
batch before writes, rereads each ticket immediately before updating it, and
reads back each result. It uses `bd update --metadata` (an object-key merge in supported Beads), preserving unrelated
metadata, notes, status, and labels. A CLI failure mid-batch may leave a partial
batch; successful IDs are printed as they are written. It does not push Beads
data; follow your repository's synchronization policy.

The setter rejects active tickets, malformed execution metadata, and changes to
an existing execution-bound destination. It may restore a missing or incorrect
`abacus_target` to the original bound destination, without rewriting the binding.
It does not clear attention or reopen
blocked tickets. After reviewing and fixing an issue, explicitly resolve it:

```sh
abacus attention resolve project-123 --message "Target checked and corrected" --reopen
```

Beads does not provide a compare-and-swap for these metadata edits. The requirement
to stop concurrent dispatch/edits is intentional; the setter is not a live
retargeting mechanism. Runtime revalidation is an additional safeguard, not a
cross-system transaction.

## Routing an agent pool

Normal dispatch handles all configured ticket destinations. Restrict a pool with
repeatable filters:

```sh
abacus run --repo /path/to/main-checkout \
  --target-filter main --target-filter release/1.2 \
  --tmux-session workers --model provider/model -a alice /path/to/worktree
```

`--target-filter` filters eligibility; it never supplies or overrides ticket
metadata. Missing targets participate using the resolved default when enforcement
is off. Existing label/type/priority filters still apply. Target eligibility is
evaluated before priority and newest-comment tie-breaking. Direct-child gates
are unchanged.

Invalid metadata candidates are eligible for an **atomic validation claim** even
in a target-filtered pool. Only the claim winner blocks the ticket, clears its
assignee, adds `abacus:needs-user-attention`, and appends the exact validation
reason and repair commands. Abacus verifies the block, label, and reason, and
pushes when a Beads remote is configured. No agent launches and no Git workspace
mutation occurs. A failed block or synchronization halts the affected loop with
a persistent alert. Other agents' observed ownership and terminal states are
not overwritten. In `--once` mode, a validation claim consumes that agent's one
claim; continuous and drain modes can continue to other ready tickets.

## Durable execution binding

After claiming, Abacus rereads metadata and ownership. It refuses a target change
between selection and claim, then writes `metadata.abacus_execution`:

```json
{
  "version": 1,
  "targetRef": "refs/heads/release/1.2",
  "issueBranch": "abacus/project-123",
  "startCommit": "<full commit ID>",
  "policyIdentity": "<SHA-256 policy identity>"
}
```

For a defaulted ticket, `abacus_target` is not automatically stamped. Its resolved
destination is still recorded in this binding and supplied to the agent prompt.
Changing the default to another branch cannot retarget an existing binding: it
fails validation and requires operator review. Enabling enforcement likewise
requires missing targets to be filled explicitly before those tickets resume.

It reads back the binding and ownership, and synchronizes Beads when configured,
**before** creating a branch. Branch creation explicitly uses the recorded
starting commit, never incidental workspace `HEAD`:

```sh
git switch -c abacus/project-123 <recorded-start-commit>
```

A crash after recording the binding but before creating the branch can therefore
resume safely. Existing bound issue branches must retain the starting commit;
the target must also retain that history. Branches are never reset, rebased, or
recreated to change targets. A branch checked out elsewhere is not forcibly
checked out with `--ignore-other-worktrees`; stop and release that checkout
explicitly before retrying.

Dirty-workspace recovery still bypasses ordinary dispatch filters, but never the
binding, policy, or ownership checks. Invalid recovery preserves the files and
parks the agent. Operator Stop/Restart likewise revalidates the reserved ticket.

Abacus holds an OS-backed exclusive lock in each worktree's Git administrative
directory for the run. Competing runs cannot use that same workspace. Lock files
remain after exit but their ownership ends when the handle/process exits; do not
delete a lock file to try to unlock live work. `preflight` remains read-only and
does not acquire or create ownership files.

## Adopt existing unbound issue branches

Legacy branches have no trustworthy recorded starting point. Abacus refuses to
infer one from ancestry or assign a destination just because it looks plausible.
Stop dispatch and active work, inspect the branch's commits and diff, then select
its actual intended starting commit explicitly:

```sh
abacus targets set release/1.2 project-123 \
  --adopt-existing-branch --start-commit <full-reviewed-commit-id>
abacus targets check project-123
```

Adoption only accepts one inactive, unbound ticket with an existing issue branch.
It verifies that the supplied commit is in both issue and target histories, then
records the target and binding without modifying any Git files or refs. That
check establishes ancestry, not semantic correctness; the operator must review
the chosen destination and starting point. Adoption cannot overwrite a binding.

## Upgrade checklist

1. Stop existing orchestrators and active agent processes. Replace launcher
   `--config <file>` arguments with `--repo <main-checkout>` (not the JSON path).
   Older launchers with neither argument also need `--repo` when run outside Git.
2. Run `abacus init` to install/update skills and create missing configuration.
   Beads must already be initialized. Review the allowlist and ensure its local
   branches exist. If there is no local `main`, write a config naming your intended
   existing branch first; `init` never guesses from the current checkout.
3. Run `abacus health` and `abacus targets check`.
4. Choose `defaultTarget` and enforcement mode. Set explicit destinations when
   required or different from the default; adopt reviewed legacy branches where needed.
5. Resolve metadata-related blocks only after the audit passes.
6. Review and commit configuration and installed skills, and make them available
   to the selected main checkout. Controller configuration is no longer loaded
   from worktree-local or arbitrary external config paths.
7. Run `abacus preflight` with your normal run options, then start the pool.
