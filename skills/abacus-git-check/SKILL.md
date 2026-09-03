---
name: abacus-git-check
description: Audit repository agent instructions for misleading claims or blanket rules that prevent agents from using local Git operations. Use when agents may have been told that Git is unavailable, forbidden, or outside their permissions; restrictions on pushing or publishing are allowed.
---

# Abacus Git Check

Check the repository's active agent instructions for rules that could incorrectly stop an agent from using Git. Treat the audit as read-only unless the user separately asks for corrections.

## Find the Instruction Surfaces

1. Resolve the repository root with `git rev-parse --show-toplevel` and stop clearly if the current directory is not in a Git repository.
2. Inventory tracked and untracked, non-ignored instruction files. Include applicable `AGENTS.md` files and common harness-specific surfaces such as `CLAUDE.md`, `GEMINI.md`, `CODEX.md`, `.github/copilot-instructions.md`, `.github/instructions/*.instructions.md`, `.cursor/rules`, `.cursorrules`, `.windsurfrules`, and repository-local skills under `.agents/skills` or `.codex/skills`.
3. Follow links from those files to additional documents only when the linked document supplies agent rules. Do not treat ordinary product documentation, examples, generated artifacts, vendored dependencies, or historical discussions as active instructions merely because they mention Git.
4. Respect directory scope and instruction precedence. A nested instruction file may apply only to part of the tree, and a more specific instruction may intentionally refine a repository-wide rule.

Use filename discovery plus a case-insensitive text search for candidate language such as `git`, `permission`, `read-only`, `forbidden`, `do not`, `never`, `cannot`, `can't`, `must not`, `ask the user`, `commit`, `branch`, `checkout`, `merge`, `rebase`, `reset`, `stash`, `fetch`, `pull`, and `push`. Read each match in context; keyword matches alone are not findings.

## Decide What to Flag

Flag an instruction when it states or materially implies any of these without a repository-specific justification:

- agents lack permission or capability to run Git or write normal local Git metadata;
- all Git commands are forbidden or must be delegated to the user;
- ordinary local operations such as status, diff, add, commit, branch, checkout, merge, rebase, reset, stash, or other repository maintenance are categorically unavailable to agents;
- approval is always required solely because an operation uses Git rather than because the operation is destructive, externally visible, or otherwise governed by an actual safety boundary.

Also flag contradictory instruction sets when one active rule authorizes a local Git workflow and another says agents cannot perform it.

Do **not** flag:

- restrictions on `git push`, force-push, remote publication, or opening/updating pull requests;
- requirements to obtain approval before an externally visible or destructive action;
- branch naming, commit-message, review, merge, worktree, clean-tree, or coordination conventions that guide how Git is used rather than falsely claiming Git is unavailable;
- a narrow prohibition backed by a concrete repository workflow, such as not committing generated files or not rewriting shared history;
- statements that accurately describe the current tool or sandbox after verifying that limitation from direct evidence.

If a restriction might be a legitimate workflow policy but its rationale is unclear, classify it as needing review rather than declaring it wrong. Do not test permissions with a mutating Git command.

## Report

For each finding, provide:

- file path and line number;
- the relevant instruction, quoted briefly or paraphrased;
- the directories or agents to which it applies;
- why it may incorrectly suppress permitted local Git work;
- classification: **incorrect restriction**, **contradiction**, or **needs review**;
- a minimal suggested replacement that preserves any legitimate safety or workflow constraint.

Separate confirmed findings from ambiguous candidates. Explicitly state that push-only restrictions were checked and accepted. If there are no findings, report which instruction surfaces were inspected and say that no active instruction incorrectly restricts local Git operations.

Do not edit instruction files, run mutating Git commands, commit, or push as part of the check. If the user asks for fixes, preserve the original intent, change only the problematic language, and show the resulting diff.
