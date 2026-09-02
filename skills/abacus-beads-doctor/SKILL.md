---
name: abacus-beads-doctor
description: Audit Beads issues and dependency graphs for agent readiness, then collaborate with the user on precise repairs. Use when descriptions, acceptance criteria, dependencies, labels, types, priorities, metadata, or issue boundaries may prevent agents from completing work effectively; this complements rather than replaces `bd doctor` database-health checks.
---

# Abacus Beads Doctor

Find content and graph problems that make Beads work ambiguous, unsafe, blocked incorrectly, or difficult for an agent to finish. Diagnose first, then improve the issues with the user; do not silently rewrite their plan.

## Scope and Evidence

1. Run `bd prime`. If it returns no context, run `bd where` and stop if the current repository has no Beads workspace.
2. Establish the requested scope: named issues, an epic and its descendants, active issues, or the whole database. Default to active work rather than auditing closed history.
3. Read repository instructions and relevant product, architecture, and planning documents.
4. Inspect `bd version` and command help when needed. Collect structured data with `bd list --json`, `bd show <id> --json`, `bd dep list <id> --json`, and `bd config show --json`.
5. Use built-in diagnostics such as `bd doctor --agent --json`, `bd graph check`, and `bd dep cycles --json` when available. Their storage and integrity findings are inputs; this skill's main job is issue-content and execution-readiness review.
6. Inspect `bd label list-all --json` and representative healthy issues to infer conventions. An unfamiliar or one-off label is not invalid merely because it is rare.

For large scopes, begin with cheap filters such as `bd list --empty-description --json`, then batch issue reads. State any sampling or scope limits explicitly.

## Agent-Readiness Audit

Evaluate each issue against its purpose and type, not a rigid template.

### Content

- The outcome and reason for the work are understandable.
- Scope and important exclusions are clear enough to avoid unrelated changes.
- Acceptance criteria are observable and testable, or the issue intentionally produces a decision or investigation result.
- Constraints, interfaces, repository locations, and validation expectations are present when they are not discoverable from the codebase.
- The title matches the actual work and the description does not contradict other fields.
- The issue is bounded enough for one agent session, or is correctly represented as an epic.

Do not penalize concise issues when the repository supplies the missing context. Do not invent requirements to make a description look complete.

### Graph

- Every blocking edge has the correct direction: `bd dep add <dependent> <prerequisite>` means the first issue depends on the second.
- Dependencies represent necessary inputs, not narrative order or mere relationship.
- Cycles, missing targets, orphaned children, duplicated edges, premature integration, and needless serialization are absent.
- Parent-child edges express hierarchy; blocking edges express execution order.
- Parallel tasks can become ready in parallel, and integration or verification waits for all actual producers.
- Blocked issues identify a concrete blocker; readiness from `bd ready --json` agrees with the intended execution plan.

### Fields and Conventions

- Type matches the deliverable; priority expresses importance rather than sequence.
- Status, assignee, defer/due state, and parent are internally consistent.
- Labels use the repository's taxonomy without typos, conflicting states, or accidental inherited labels.
- Metadata is valid JSON, uses known keys and value shapes when a schema or convention exists, and does not duplicate a first-class Beads field.
- External references, estimates, required skills, and spec links are present only when meaningful and point to real artifacts.

Distinguish “invalid” from “unfamiliar.” Require repository evidence, configuration, documentation, or a clear contradiction before calling a custom label, type, or metadata key invalid.

## Report and Collaborate

Report findings before mutation, grouped as:

- **Agent-stopper:** an agent cannot safely determine or complete the work.
- **Graph error:** readiness or dependency semantics are wrong.
- **Quality warning:** likely rework, ambiguity, or convention drift.
- **Suggestion:** useful improvement that is not required for execution.

For every finding, include the issue ID, evidence, impact, and a proposed correction. Separate facts from inferences. Consolidate repeated convention questions and ask the user about ambiguous product or dependency intent instead of guessing.

Present a proposed patch set that preserves existing useful text and list dependency changes explicitly as removals and additions. Ask for confirmation before changing any issue. If the user approves only part of the set, apply only that part.

## Repair and Recheck

Use non-interactive commands with `--json`: `bd update` for fields, `bd dep add` and `bd dep remove` for edges, and label commands or update flags for labels. Prefer body/design files for multiline replacements. Never use `bd edit`.

Do not close, delete, reassign, claim, or change issue status unless the user explicitly requested that specific lifecycle change. Never erase human notes or acceptance criteria merely to normalize formatting.

After repairs, read every changed issue and dependency back, rerun applicable graph checks, and compare `bd ready --json` with the intended ready set. Follow the repository's sync policy; do not push unless requested or explicitly required by repository instructions.

Finish with changed issue IDs, the repairs made, the verified ready set, and unresolved findings that still need a user decision.
