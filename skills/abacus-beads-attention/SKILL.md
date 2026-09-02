---
name: abacus-beads-attention
description: Find every Beads issue carrying Abacus's user-attention label and produce a concise, high-level action report. Use when the user asks what needs their attention, which agent decisions or outside actions are pending, or for a summary of `abacus:needs-user-attention` issues; do not use for a general backlog review.
---

# Abacus Beads Attention

Tell the user what Abacus agents need from them without making them read every ticket or exposing unnecessary implementation detail.

## Find Attention Issues

1. Run `bd prime`. If it returns no context, run `bd where` and stop if the current repository has no Beads workspace.
2. Query exactly the label Abacus uses, including closed issues:

   ```sh
   bd list --label abacus:needs-user-attention --all --limit 0 --json
   ```

   Do not substitute a similarly named label and do not infer attention from `blocked` status alone.
3. If the result is empty, report that no issues currently request user attention and stop.
4. Read each result with `bd show <id> --json`. Include comments when the installed CLI supports it and they may contain the request. Inspect relevant dependencies, linked specifications, or repository context only when needed to explain what the user must decide or do.

The label is persistent by design. Closed issues remain in the report until the label is removed, so check whether each request is current, resolved-but-not-cleared, or retained intentionally.

## Produce the Report

Summarize at decision level rather than retelling ticket implementation. Group items when useful into:

- **Decision needed** — the user must choose between clear options.
- **Action or access needed** — an outside action, permission, credential, resource, or coordination step is required.
- **Blocker or failure to review** — work stopped or recovery failed and the user should assess impact.
- **Possibly stale** — the issue is closed or the recorded request appears resolved, but the attention label remains.

For each issue include:

- issue ID, title, and current status;
- why it is asking for attention, in one or two sentences;
- the concrete question or action for the user;
- impact or urgency when supported by the issue;
- a recommended next step, clearly marked as a recommendation rather than fact.

Surface missing or contradictory context instead of guessing. Put the highest-impact or blocking requests first. End with counts by category and a short “look at these first” list. When many issues share one decision, consolidate them while retaining every affected issue ID.

## Keep the Report Read-Only

Reporting does not authorize issue mutation. Do not remove the label, add notes, close, reopen, reassign, or otherwise update an issue unless the user explicitly asks after reviewing the report.

When the user confirms that an attention request is resolved, preserve any useful resolution context in the issue if requested, then remove the label with:

```sh
bd update <id> --remove-label abacus:needs-user-attention --json
```

Read the issue back after any approved change and follow the repository's Beads synchronization policy.
