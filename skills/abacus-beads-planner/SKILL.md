---
name: abacus-beads-planner
description: Turn a user-defined product or engineering concept into a complete, execution-ready Beads issue graph. Use when the user wants work decomposed into Beads epics, tasks, dependencies, labels, priorities, and acceptance criteria; do not use for merely listing ideas or implementing the work.
---

# Abacus Beads Planner

Create a durable Beads graph that another agent can execute without having to rediscover the plan. Preserve the user's intent and the repository's own conventions.

## Establish Context

1. Run `bd prime`. If it returns no context, run `bd where` and stop if the current repository has no Beads workspace.
2. Read repository guidance and the product, architecture, and planning documents relevant to the concept.
3. Inspect related open and recently closed issues with `bd list --json` and `bd show <id> --json`. Reuse or link existing work rather than creating duplicates.
4. Inspect `bd version` and the help for any command whose contract is uncertain. Use the installed CLI rather than assuming a particular Beads release.
5. Learn local conventions from `bd config show --json`, `bd label list-all --json`, and representative issues. Treat observed labels as evidence, not a formal allow-list.

Clarify only decisions that materially change scope, architecture, sequencing, or the definition of done. Inspect the repository before asking questions that the repository can answer. Do not invent product decisions; represent unresolved decisions as explicit decision or investigation issues when that makes the graph executable.

## Design the Graph

Use an epic for the overall concept when it has multiple independently deliverable pieces. Give the epic the problem, intended outcome, boundaries, and concept-level completion criteria.

Make each child issue independently claimable and small enough for one focused agent session. Each issue should contain:

- why the work exists and the observable outcome;
- included work and important exclusions;
- relevant constraints, interfaces, and repository locations when known;
- test or validation expectations;
- concrete acceptance criteria;
- upstream decisions or artifacts it genuinely requires.

Use the description for context and scope, `--acceptance` for completion criteria, and `--design` only for established implementation guidance. Avoid prescribing an implementation when the user has not chosen one.

Choose issue types, priorities, labels, estimates, skills, and metadata from repository conventions. Use priority to communicate scheduling importance, not dependency order. Introduce a new label or metadata key only when it has a clear durable meaning and call it out in the draft.

Model only real blockers. For `bd dep add <dependent> <prerequisite>`, the first issue is blocked by the second. Keep parallel work parallel; do not serialize tasks merely because they were discussed in sequence. Use parent-child relationships for hierarchy, blocking dependencies for execution order, and other dependency types only when their semantics are intentional. Add an integration or verification issue when independently produced work must be combined and validated.

## Review Before Mutation

Present a draft before creating anything. Use stable temporary keys and show, at minimum:

- title, type, priority, and parent;
- concise purpose and acceptance criteria;
- labels or other non-default fields;
- dependencies expressed as “A depends on B”;
- unresolved assumptions or decisions.

Also summarize the expected execution waves so accidental serialization and missing integration points are visible. Ask the user to approve the graph or revise it. Approval of the concept alone is not approval to mutate Beads.

## Materialize Safely

After approval, create issues individually with `bd create ... --json`, capture every returned ID, and then add dependencies with `bd dep add ... --json`. Prefer body or design files for multiline text so shell quoting cannot alter content. Use `--parent` for hierarchy when supported by the installed CLI.

Do not rely on a bulk graph import unless its behavior has been verified for the installed Beads version and every required field. If creation stops partway through, do not delete or recreate issues automatically. Report the created IDs and the exact remaining work, then agree on recovery with the user.

## Verify the Result

Read the created issues back with `bd show <id> --json` and inspect their edges with `bd dep list <id> --json`. Run the available graph checks, normally `bd dep cycles --json` and `bd graph check`, then inspect `bd ready --json` to confirm the intended first wave is actually ready.

Compare the stored graph with the approved draft. Fix only unambiguous creation mistakes; discuss semantic changes with the user. Follow the repository's Beads sync policy, and do not push to a remote unless the user requested it or repository instructions explicitly require it.

Finish with the epic ID, child IDs, ready-first issues, execution waves, and any unresolved decisions.
