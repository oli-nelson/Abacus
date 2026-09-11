# Run configurations

Save run settings in a version-1 JSON file instead of repeating CLI arguments:

```sh
abacus config edit                                  # create a draft
abacus config edit run.json                         # load an existing file
abacus config edit template.json --output run.json   # leave the input untouched
abacus run --config run.json
abacus run --config run.json --model gpt-5.6-sol --once
abacus preflight --config run.json
abacus run                                         # picker if required args are missing
```

The editor requires an interactive terminal, but no Git repository, Beads, or
agent tools. Use **Up/Down** to select a field, **Enter** to edit (or toggle a
boolean), **Delete** to unset, **S** to save, **A** for Save As, and **Q** to exit.
Home/End jump to the first/last field. Text prompts keep their current value on
blank input; `-` clears it. Agent editing supports add, edit, and remove; reasoning
models have individual high/medium/low prompts. Label/filter lists use JSON arrays.

Missing models, workspaces, and invalid combinations produce warnings, but do
**not** prevent saving a draft. Saving does not launch agents. Running still
validates options and all normal preflight prerequisites. The editor's warnings
check option validity, not installed tools, Git/Beads state, or repository policy.
Malformed JSON, unknown properties, duplicate keys, wrong JSON types, and versions
other than 1 are rejected rather than silently discarded. Missing properties
inherit from a base when present; `null` clears to unset/default. An explicit input file must exist; omit it for a new file.

Save As confirms before overwriting a different existing file. Unsaved changes
require confirmation before quitting. Saves replace the destination atomically;
a failed save leaves the existing destination intact. Destination directories must
already exist. Editing existing JSON directly in your preferred editor also works.

## Example

```json
{
  "version": 1,
  "repo": "repo",
  "mode": "codex",
  "model": "gpt-5.6-sol",
  "effort": "high",
  "agents": [
    { "name": "agent-0", "workspace": "worktrees/0" },
    { "name": "agent-1", "workspace": "worktrees/1" }
  ],
  "reasoningModels": { "high": "gpt-6-astra" },
  "notify": "attention"
}
```

`repo`, each agent's `workspace`, and `eventLog` are relative to the **config file's
directory**, not the invocation directory. Absolute paths stay absolute. Save As
rebases relative paths so they still point to the same locations. For a brand-new
draft, paths are relative to `--output`'s directory when supplied, otherwise cwd.
CLI paths always remain relative to cwd. No environment-variable or tilde expansion
is performed inside JSON. Without `repo`, the normal cwd-based repository selection
still applies.

## Base config inheritance

Use **one** `--config`. A config can declare one base file inside:

```json
{
  "version": 1,
  "baseConfig": "abacus_base.json",
  "mode": "codex",
  "model": "gpt-5.6-sol"
}
```

`baseConfig` is relative to the file declaring it (or may be absolute). A base can
have its own base, up to 64 files total. Missing/malformed bases, cycles, and
excessive depth fail clearly. `baseConfig: null` means no base. Repeated `--config`
arguments are rejected: replace old CLI chains with base references instead.

Apply the deepest base first, then each derived file, then explicit CLI options.

- Omitted fields inherit. Explicit `null` clears an inherited setting back to
  unset/default; `false` disables an inherited boolean.
- Agent and filter arrays replace the entire previous list. An empty array clears
  the list; clearing all agents makes the final run invalid unless CLI agents
  supply replacements.
- `reasoningModels` merges per tier. A null tier clears that route;
  `"reasoningModels": null` clears all routes. An empty object adds no overrides.
- A file specifying `once` or `drain` replaces the prior execution choice.
  Both true in one file is still an error; false/null can reset to continuous.
- Each file must have `version: 1` and valid JSON field types. Missing required
  settings or invalid semantic values in a base can be supplied/corrected by the
  derived file or CLI. Only the final composition must satisfy runtime validation.
- Paths remain relative to **the file that supplied that value**, even if a derived
  config lives in another directory. Loading inheritance never modifies files.

The editor edits only the selected file and validates inherited settings for its
warnings. Save As rebases `baseConfig` and local paths without flattening settings
from the base into the saved file. Broken/missing base references can be edited
and saved as drafts, with warnings. A shared base missing a model may warn when
edited alone; a harness config inheriting that base can still be complete.

## Interactive fallback selection

When `abacus run` has **no `--config`** and is missing required argument values:

1. Report the missing model, agent names/workspace paths, and server address when
   `--mode opencode-server` requires it.
2. Search only the working directory (not parents or subdirectories) for `.json`
   files matching the run-config schema. Incomplete drafts are included;
   unrelated/malformed JSON files are excluded. Filenames are sorted.
3. Offer a numbered choice, even when there is only one file. Enter a number to
   select it, or blank/`q` to cancel. No automatic selection is made.
4. Load that file and its base, apply existing CLI overrides, and validate once.
   If still incomplete, list all remaining required arguments and exit nonzero.
   There is no second selection attempt. Cancelled/invalid selection, no matching
   files, or an unusable selected config also exits without starting a run.

**Non-interactive routes never discover configs or prompt:** redirected stdin,
stdout, or stderr, `--stdio`, `--verbose`/`-v`, and `preflight` fail directly.
Malformed/invalid CLI options, fully specified runs, help, and explicit `--config`
requests also bypass discovery. The picker only checks argument completeness;
Git, Beads, harness availability, and repository policies still use normal
preflight after a valid selection. No external tools or agents start in the picker.

## CLI precedence

- Explicit CLI scalar values and boolean flags override saved settings, regardless
  of where `--config` appears. Boolean flags accept `=false` to disable a saved
  setting, e.g. `--start-paused=false`; `=true` is also supported.
- CLI `--agent`/`-a` declarations replace the **entire** saved agent list.
- CLI `--label`, `--exclude-label`, and `--target-filter` replace their corresponding
  saved list (not all three lists together).
- `--reasoning-model` overrides only the named tier; other saved tiers remain.
- Explicit `--once` or `--drain` replaces the saved execution choice. To return to
  continuous mode, disable the saved choice, e.g. `--once=false`.
- Duplicate non-repeatable CLI options, duplicate reasoning tiers, and conflicting
  explicit `--once --drain` remain errors. `--config` is not repeatable.
- Preflight accepts the same inherited config, ignoring saved run-only controls (`once`,
  `drain`, `stdio`, `eventLog`, `noIntro`, `startPaused`, `disownTmuxSession`). Those
  options remain rejected when explicitly passed on the preflight CLI.

## Fields

All fields other than `version` are optional in a saved draft. Runtime-required
fields remain required to run. Semantics/defaults match the [CLI reference](cli-reference.md).

| JSON field | Type | CLI equivalent |
| --- | --- | --- |
| `version` | integer, must be `1` | Schema version |
| `baseConfig` | string path, or null | Base run config file (no CLI equivalent) |
| `repo` | string path | `--repo` |
| `mode` | string | `--mode` |
| `model` | string | `--model` |
| `effort` | string | `--effort` |
| `agents` | array of `{ "name": "...", "workspace": "..." }` | `--agent` |
| `reasoningModels` | object with optional `high`, `medium`, `low` strings | `--reasoning-model` |
| `tmuxSession` | string | `--tmux-session` |
| `tmuxWindow` | string | `--tmux-window` |
| `tmuxLayout` | string | `--tmux-layout` |
| `disownTmuxSession` | boolean | `--disown-tmux-session` |
| `opencodeServer` | string | `--opencode-server` |
| `remoteControl` | boolean | `--remote-control` |
| `targetFilters` | array of strings | `--target-filter` |
| `labels` | array of strings | `--label` |
| `excludeLabels` | array of strings | `--exclude-label` |
| `type` | string | `--type` |
| `priority` | integer | `--priority` |
| `ticketTimeout` | string | `--ticket-timeout` |
| `appendPrompt` | string | `--append-prompt` |
| `latestComments` | integer | `--latest-comments` |
| `notify` | string | `--notify` |
| `notifySound` | boolean | `--notify-sound` |
| `once` | boolean | `--once` |
| `drain` | boolean | `--drain` |
| `verbose` | boolean | `--verbose` |
| `stdio` | boolean | `--stdio` |
| `eventLog` | string path | `--event-log` |
| `noIntro` | boolean | `--no-intro` |
| `startPaused` | boolean | `--start-paused` |

Run configs are separate from `<repo>/.abacus/targets.json` and
`<repo>/.abacus/reasoning.json`, which continue to define repository policies.

## Generated projects

`abacus new` writes four config files in the project root, with no shell scripts:

- `abacus_base.json`: shared `repo`, `agents`, default `effort`, and `version`,
  with `startPaused: true`, `notify: "all"`, and `notifySound: true`.
- `abacus_opencode.json`: `version`, `baseConfig`, `mode`, and OpenCode `model`.
- `abacus_codex.json`: `version`, `baseConfig`, `mode`, and Codex `model`.
- `abacus_claude.json`: `version`, `baseConfig`, `mode`, and Claude `model`.

Each harness config declares `"baseConfig": "abacus_base.json"`. Edit the base once
to change shared settings. From the project root, simply execute:

```sh
abacus run
```

Select a harness config, not `abacus_base.json` (which deliberately has no model).
Generated runs start paused; press **Shift-Tab** to allow ticket claims. All desktop
notifications and notification sounds are enabled. Override with
`--start-paused=false`, or `--notify off --notify-sound=false` to disable notifications.
For `--stdio`, disable notifications; for `--verbose`, also disable start-paused.
For non-interactive use or invocation from outside the project, explicitly select
one config; paths inside it remain relative to the file:

```sh
abacus run --config /path/to/project/abacus_codex.json --start-paused=false
```

To add another inheritance level, create `local.json` with
`"baseConfig": "abacus_codex.json"` and use `abacus run --config local.json`.
The base records created worktrees; add new worktrees there explicitly.

**Migration:** new projects no longer include launcher scripts. Use `abacus run`
with the interactive picker or an explicit config and normal CLI flags instead of
script arguments/environment overrides. Replace repeated `--config` arguments
with `baseConfig` references in derived files. Previously generated scripts are
not modified or deleted.
