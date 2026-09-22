namespace Abacus;

internal static class CliHelp
{
    public const string Overview = """
        Abacus coordinates Beads tasks and interactive coding agents.
        Usage: abacus <command> [options]

        Commands:
          dashboard            Host the interactive Beads/Git dashboard without starting agents.
          run                  Run agents continuously, or with --once / --drain.
          config edit [file] [--output <file>]
                               Create/edit a run config in the TUI, with Save As.
          preflight            Validate a run configuration without starting agents.
          new <name> --agents <count>
                               Create a new multi-agent Git/Beads project and run configs.
          init                 Initialize Abacus in an existing Git/Beads repository.
          skills install       Install bundled skills (confirm before replacement).
          health               Report read-only repository and tool readiness.
          info                 Print a concise Git, Beads/Dolt, ticket, and config overview.
          version              Print the embedded build version and exit.
          models               List model IDs grouped by installed agent harness.
          branches prune       Delete local Abacus branches for closed tickets.
          worktrees list       Show managed pool slots, paths, state, and disk usage.
          worktrees reclaim <slot-ID>  Detach a safely merged, idle pool slot for reuse.
          worktrees remove <slot-ID> --confirm  Remove a safe idle slot, including caches.
          worktrees recover <slot-ID> --confirm  Confirm old execution stopped; preserve work.
          worktrees prune      Forget missing slots whose Git registration has been removed.
          attention list       Print attention-labelled issue IDs, one per line.
          attention retry-supervisor <id> [<id> ...]  Allow another supervisor attempt.
          attention resolve <id> [--message <text>] [--reopen]
                               Resolve user attention, optionally comment and reopen.
          targets check [<id> ...]
                               Audit ticket targets; no IDs audits all ticket history.
          targets set <branch> <id> ...
                               Set targets on inactive tickets.
          help [<command> ...] Show command-specific help; also <command> --help / -h.

        Shared repository option:
          --repo <path> selects the main Git checkout (default: cwd inside that checkout).
          Accepted before or after repository-scoped commands, not new, models, version, or config edit.
          Linked worktrees cannot be controller roots; agent worktrees are supported.
          Targets load from <repo>/.abacus/targets.json; reasoning policy loads
          from <repo>/.abacus/reasoning.json when present.

        Use -- to end option parsing before positional arguments. Single-value options
        accept --option=value. Quote multi-word values. Commands and options are exact;
        no implicit run, legacy operation flags, or abbreviated commands are accepted.

        Terminal presentation:
          Interactive help, reports, prompts, verbose events, and summaries use semantic colors.
          Set NO_COLOR to disable ANSI styling. Redirected output is always plain text.
          `version` and `attention list` remain minimal, script-friendly output.
        """;

    private const string RunOptions = """
        Configuration:
          --config <file>               Load one JSON config; optional baseConfig inherits a base file.
                                        CLI agent/filter lists replace configured lists; reasoning
                                        models, efforts, and arguments override per tier.
                                        CLI overrides win over derived/base values.
                                        Paths stay relative to their source file. --config is not repeatable.
                                        Boolean flags accept =false to disable saved settings.
                                        A config may also set a config-only "schedule" of claim windows
                                        that block new tickets during recurring provider peak hours.

        Agent and model:
          --agents <count>              Managed pool workers (default 1); no workspace setup required.
          --agent-name <name>           Optional managed worker label (repeatable); names do not own slots.
          --agent, -a <name> <workspace>  Legacy explicit workspaces; cannot combine with --agents.
          --mode <opencode|codex|claude|opencode-server>  Default: opencode.
          --model <model[#effort]>       Required fallback; effort defaults to high.
                                        OpenCode uses provider/model IDs.
          --reasoning-model <tier> <model[#effort]>
                                        Repeatable model[#effort] mapping for high, medium, or low.
                                        A missing suffix inherits the fallback model's effort.
                                        Interactive OpenCode uses its configured variant.
          --maintainer <model[#effort]>
                                        Enable optional maintenance supervisor in the main checkout.
          --supervisor-extra-args <string>
                                        Separate supervisor harness arguments (no agent args inherited).
          --supervisor-prompt-file <path>
                                        Append this file after the repo's .abacus/supervisor.md.
          --supervisor-timeout <duration>
                                        Positive s/m/h runtime limit; default 90m.
          --continuation-model <model[#effort]>
                                        Enable independent empty-backlog continuation supervisor.
          --continuation-extra-args <string>  Separate continuation harness arguments.
          --continuation-prompt-file <path>   Policy after .abacus/continuation.md.
          --continuation-timeout <duration>  Positive s/m/h limit; default 90m.
          --extra-args <string>          Extra CLI arguments for every agent launch, e.g. -p deepseek.
                                        Split on whitespace; quote values that contain spaces.
          --reasoning-args <tier> <string>
                                        Repeatable extra CLI arguments for high, medium, or low.
                                        A mapped tier replaces the default --extra-args value.
          --remote-control              Claude only; enables interactive Remote Control.

        Hosting:
          --tmux-session <name>          Session to use or create. Interactive modes default to
                                        "abacus - <project-id>".
          --disown-tmux-session          Keep an automatically created default session after exit.
          --tmux-window <name-or-index>  Window to use or create. Default: Abacus Agents.
          --tmux-layout <layout>         Choices: even-horizontal,
                                        even-vertical, main-horizontal, main-vertical, tiled.
                                        Default for pane hosting: tiled.
          --opencode-server <host:port>  Requires explicit --mode opencode-server.
                                        Server mode can run without tmux as direct children.

        Dispatch and supervision:
          --target-filter <branch>       Repeatable resolved-target filter, NOT a destination override.
          --label <label>                Repeatable required labels.
          --exclude-label <label>        Repeatable excluded labels; gt:slot is always excluded.
          --type <types>                 Literal Beads type filter; comma-separated types allowed.
          --priority <0-4>               One priority, 0 highest.
          --ticket-timeout <duration>    Positive s/m/h duration (30s, 15m, 2h); safely recover on timeout.
          --append-prompt <text>         Nonempty fragment appended to every agent prompt, before
                                        <workspace>/.abacus/append-prompt.md when present.

        Output:
          --latest-comments <1-100>      Dashboard comment count; default: 8.
          --notify <off|attention|all>   Desktop notifications; default: off.
          --notify-sound                Outcome sounds; requires --notify attention or all.
          --verbose, -v                 Timestamped state and subprocess logs instead of dashboard.
                                        Levels and final outcome categories are color-coded on a terminal.
                                        Set NO_COLOR to disable ANSI styling.

        Repository:
          --repo <path>                 Main checkout; default: cwd inside that checkout.
                                        Targets load from .abacus/targets.json. Missing metadata
                                        uses defaultTarget unless enforceTargetBranch is true.
                                        Reasoning policy loads from .abacus/reasoning.json when present.
        """;

    public static string For(string command) => command switch
    {
        "dashboard" => """
            Usage: abacus dashboard [--repo <main-checkout>] [options]
            Host the interactive Beads/Git dashboard without starting agents.

              --bind <ip-or-host>       Default 127.0.0.1; IPv4, IPv6 or resolvable hostname.
              --port <port>             Default 8080; range 1–65535, no automatic fallback.
              --actor <display-name>    Default abacus-web; self-declared audit attribution.
              --poll-interval <duration> Default 5s; minimum 1s (s/m/h suffix).

            WARNING: unauthenticated read/write access. Use trusted networks only;
            HTTP is not encrypted. No browser is opened automatically.
            Implementation preview: issue/branch browsing, comparisons and live updates;
            comments, content/attention edits, bounded history and a WebGL timeline. Integrated runs also expose live workers and claim Pause/Resume.
            """,
        "" => Overview,
        "run" => """
            Usage: abacus run [options] --model <model[#effort]> [--agents <count>]
            Without --config, missing required model/server values offer a one-time config
            selection from JSON files in cwd, only on an interactive terminal. If still incomplete,
            report all missing arguments and exit. No picker for --stdio, --verbose, or redirected I/O.
            Invalid CLI options fail directly. Fully specified runs never search for configs.
            Runs continuously by default. Before fresh dispatch, preserves and recovers interrupted
            dirty issue workspaces; ambiguous workspaces stop with an alert, never automatic cleaning.
              --once   Process at most one currently ready ticket per agent, then exit.
              --drain  Process ready work until each agent observes an empty queue, then exit.
              --dashboard             Also host the HTTP issue/Git preview inside this run.
              --dashboard-bind <host>  Default 127.0.0.1; explicit tuning requires --dashboard.
              --dashboard-port <port>  Default 8080; occupied ports fail before workers start.
              --dashboard-actor <name> Self-declared edit attribution; default abacus-web.
              --dashboard-poll-interval <duration> Shared source polling; default 5s, minimum 1s.
              --stdio                 JSONL events on stdout; JSONL commands on stdin, no TUI.
              --event-log <path>      Append the same structured activity events to a JSONL file.
              --start-paused          Pause claims initially; resume with Shift-Tab, stdio, or --dashboard HTTP controls.
              --no-intro              Skip the interactive ASCII startup animation.
              --tui-audio             Play the bundled startup and attention audio; off by default.
            Stdio commands: status, pause, resume, stop, restart, clean-workspace, shutdown.
            Use {"id":"1","command":"status"}; agent actions require "agent".
            clean-workspace also requires "confirm":true. EOF gracefully shuts down.
            --stdio rejects --verbose and desktop notifications.
            HTTP access is unauthenticated read/write; use a trusted network. Claim Pause/Resume
            and worker Stop/Restart/confirmed Clean plus confirmed Stop Run are available;
            supervisor Stop/Restart and confirmed Force Run use tracked outcomes.
            Saved dashboard settings inherit normally; --dashboard=false disables listening.
            --once and --drain are mutually exclusive; finite modes fail on orchestration errors.
            A run config may schedule claim windows (timezone, block, minWindowRemaining) that
            block new tickets during recurring hours such as a provider's peak prices. Those
            windows are config-only. Continuous runs wait for the next window; --once and --drain
            exit 3 without claiming, so exit 0 always means the ready queue was drained.

            """ + Environment.NewLine + RunOptions,
        "preflight" => """
            Usage: abacus preflight [options] --model <model[#effort]> [--agents <count>]
            Uses the same configuration and prerequisites as run. Read-only: no ticket claims,
            workspace changes, panes, processes, cleanup, or run summary. Exits after validation.
            Run-only --once and --drain are not accepted.

            """ + Environment.NewLine + RunOptions,
        "config" or "config edit" => """
            Usage: abacus config edit [file] [--output <file>]
            Opens a terminal editor; omit file to create a draft. No Git/tools required.
            Save or Save As, including incomplete configs with visible warnings.
            --output selects a different save destination without changing the input.
            baseConfig optionally names a base file. Warnings validate inherited settings.
            Relative paths (including baseConfig) are rebased on Save As; inherited fields stay in the base.
            Existing Save As destinations require confirmation. Run still validates all requirements.
            """,
        "new" => """
            Usage: abacus new <name> --agents <count>
            Requires a new single directory name and a positive agent count. Creates <name>/repo,
            a main branch, shared-server Beads database, bundled skills, an initial commit, and managed-pool
            JSON run configs only (no launcher scripts).
            Writes abacus_base.json (shared settings) plus abacus_<mode>.json (baseConfig/mode/model).
            Generated runs start paused, with --notify all, notification sound, and TUI audio enabled.
            Run abacus run from <name> and select a harness config. Non-interactive use requires
            --config <path-to-harness-config> or complete CLI arguments.
            Refuses an existing destination. Does not start tmux. Does not accept --repo.
            """,
        "init" => """
            Usage: abacus init [--repo <path>]
            Requires initialized readable Beads in the main Git checkout. Validates local targets,
            installs bundled skills with overwrite confirmation, and creates absent .abacus/targets.json
            with main as the default target plus absent .abacus/reasoning.json with enforcement off.
            Preserves existing configuration.
            Does not initialize Beads, change branches or tickets, stage files, or commit.
            """,
        "skills" => "Usage: abacus skills install [--repo <path>]\nInstall the bundled agent skills.",
        "skills install" => """
            Usage: abacus skills install [--repo <path>]
            Installs abacus-beads-planner, abacus-beads-doctor, abacus-beads-attention, and abacus-git-check
            under .agents/skills at the main checkout. Requires confirmation before replacing existing
            bundled directories; cancellation changes nothing. Unrelated skills are preserved.
            Does not require Beads or agent configuration and does not start orchestration.
            """,
        "health" => """
            Usage: abacus health [--repo <path>]
            Read-only repository diagnostic: targets, instructions, local branches, Git/Beads versions,
            Beads initialization, no-git-ops, Dolt storage, merge slot, harness/tmux versions, worktrees,
            bundled skills, and single-/multi-agent readiness. No model or agent options required.
            Use preflight to validate a specific run configuration.
            """,
        "info" => """
            Usage: abacus info [--repo <path>]
            Read-only project overview: repository root, branch, Git commit and worktrees;
            Dolt storage, database, server, remote, and commit; ticket counts; and target/reasoning
            policy. Missing project data is shown inline and makes the command exit one.
            No harness, model, agent, or tmux options required.
            """,
        "version" => """
            Usage: abacus version
            Print only the embedded build version, followed by a newline.
            No repository or external tools required. No options except --help / -h.
            """,
        "models" => """
            Usage: abacus models
            Lists model IDs from installed OpenCode and Codex harnesses; failed groups do not hide others.
            Claude provides an interactive /model picker, not a non-interactive catalog.
            Exits zero if any model ID is discovered, one otherwise. No repository required; no --repo.
            """,
        "branches" => "Usage: abacus branches prune [--repo <path>]\nPrune closed-ticket branches.",
        "branches prune" => """
            Usage: abacus branches prune [--repo <path>]
            Force-deletes only local abacus/<issue-id> branches whose Beads tickets are closed.
            Skips and reports branches checked out in any worktree. Never deletes remote refs
            or non-Abacus branches. Does not run agent preflight.
            """,
        "worktrees recover" or "worktrees" or "worktrees list" or "worktrees reclaim" or "worktrees remove" or "worktrees prune" => """
            Usage: abacus worktrees list [--repo <path>]
                   abacus worktrees reclaim <slot-ID> [--repo <path>]
                   abacus worktrees remove <slot-ID> --confirm [--repo <path>]
                   abacus worktrees recover <slot-ID> --confirm [--repo <path>]
                   abacus worktrees prune [--repo <path>]

            Abacus stores reusable checkouts in its application-data directory, with bookkeeping
            in the shared Git directory. list is read-only; all mutations require an idle controller.
            reclaim preserves caches, refuses dirty or unfinished/unmerged work, and detaches HEAD.
            remove additionally deletes the slot and its caches; no forced removal is performed.
            prune forgets missing pool slots only after their Git registrations have been removed.
            recover records your confirmation that all surviving agent processes have stopped.
            It never resets files or changes issues. Never confirm while any old execution is alive.
            User-managed worktrees are never adopted or removed by these commands.
            """,
        "attention" => "Usage: abacus attention <list|resolve|retry-supervisor> [options]\nUse 'abacus help attention resolve' for resolution options.",
        "attention list" => """
            Usage: abacus attention list [--repo <path>]
            Prints IDs for all issues carrying abacus:needs-user-attention, including closed issues,
            one per line with no heading. Read-only; no agent preflight.
            """,
        "attention retry-supervisor" => """
            Usage: abacus attention retry-supervisor <id> [<id> ...] [--repo <path>]

            Remove only abacus:supervisor-cannot-resolve from the specified issues.
            Keep attention labels, issue status, and assignees unchanged. An enabled
            running supervisor can reconsider attention-labelled issues on its next check.
            Does not launch Abacus or a harness. Updates run in order and stop on error;
            earlier successful updates are not rolled back.
            """,
        "attention resolve" => """
            Usage: abacus attention resolve <id> [--message <text>] [--reopen] [--repo <path>]
              --message <text>      Add the exact nonempty comment before removing the attention label.
              --reopen              Also set status open and clear the assignee in the label update.
            If commenting fails the label remains. Without --reopen the ticket status is unchanged.
            No positional message is accepted. Quote multi-word messages.
            """,
        "targets" => "Usage: abacus targets <check|set> [arguments] [options]\nAudit or set ticket target metadata.",
        "targets check" => """
            Usage: abacus targets check [<id> ...] [--repo <path>]
            Read-only audit of resolved targets, bindings and branch history. No IDs audits all tickets,
            including closed history, except gt:slot. Missing metadata uses the default when enforcement
            is off. Does not push Beads.
            """,
        "targets set" => """
            Usage: abacus targets set <branch> <id> ... [--repo <path>]
              --adopt-existing-branch --start-commit <full-commit-id>
                Explicitly adopt one unbound issue branch using a reviewed starting commit.
                Both options are required together, with exactly one issue ID.
            Requires inactive tickets; stop dispatch/active work before edits. Preserves status,
            attention and unrelated metadata. Does not retarget bound work or change Git history.
            Adoption verifies the starting commit is in both issue and target histories. Does not push Beads.
            """,
        _ => throw new OptionsException($"unknown command '{command}'. Run 'abacus help' for commands."),
    };
}
