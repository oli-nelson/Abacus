namespace Abacus;

internal static class CliHelp
{
    public const string Overview = """
        Abacus coordinates Beads tasks and interactive coding agents.
        Usage: abacus <command> [options]

        Commands:
          run                  Run agents continuously, or with --once / --drain.
          preflight            Validate a run configuration without starting agents.
          new <name> --agents <count>
                               Create a new multi-agent Git/Beads project and launchers.
          init                 Initialize Abacus in an existing Git/Beads repository.
          skills install       Install bundled skills (confirm before replacement).
          health               Report read-only repository and tool readiness.
          version              Print the embedded build version and exit.
          models               List model IDs grouped by installed agent harness.
          branches prune       Delete local Abacus branches for closed tickets.
          attention list       Print attention-labelled issue IDs, one per line.
          attention resolve <id> [--message <text>] [--reopen]
                               Resolve user attention, optionally comment and reopen.
          targets check [<id> ...]
                               Audit ticket targets; no IDs audits all ticket history.
          targets set <branch> <id> ...
                               Set targets on inactive tickets.
          help [<command> ...] Show command-specific help; also <command> --help / -h.

        Shared repository option:
          --repo <path> selects the main Git checkout (default: cwd inside that checkout).
          Accepted before or after repository-scoped commands, not new, models, or version.
          Linked worktrees cannot be controller roots; agent worktrees are supported.
          Targets load from <repo>/.abacus/targets.json; reasoning policy loads
          from <repo>/.abacus/reasoning.json when present.

        Use -- to end option parsing before positional arguments. Single-value options
        accept --option=value. Quote multi-word values. Commands and options are exact;
        no implicit run, legacy operation flags, or abbreviated commands are accepted.
        """;

    private const string RunOptions = """
        Agent and model:
          --agent, -a <name> <workspace>  Required, repeatable; names and workspaces must be unique.
          --mode <opencode|codex|claude|opencode-server>  Default: opencode.
          --model <model>                Required fallback; OpenCode uses provider/model IDs.
          --reasoning-model <tier> <model>
                                        Repeatable mapping for high, medium, or low ticket labels.
          --effort <effort>              Default: high; provider-specific value.
                                        Interactive OpenCode uses its configured variant.
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

        Repository:
          --repo <path>                 Main checkout; default: cwd inside that checkout.
                                        Targets load from .abacus/targets.json. Missing metadata
                                        uses defaultTarget unless enforceTargetBranch is true.
                                        Reasoning policy loads from .abacus/reasoning.json when present.
        """;

    public static string For(string command) => command switch
    {
        "" => Overview,
        "run" => """
            Usage: abacus run [options] --model <model> --agent <name> <workspace> [--agent ...]
            Runs continuously by default. Before fresh dispatch, preserves and recovers interrupted
            dirty issue workspaces; ambiguous workspaces stop with an alert, never automatic cleaning.
              --once   Process at most one currently ready ticket per agent, then exit.
              --drain  Process ready work until each agent observes an empty queue, then exit.
              --stdio                 JSONL events on stdout; JSONL commands on stdin, no TUI.
              --event-log <path>      Append the same structured activity events to a JSONL file.
              --start-paused          Pause claims initially; resume with Shift-Tab or stdio resume.
              --no-intro              Skip the interactive ASCII startup animation.
            Stdio commands: status, pause, resume, stop, restart, clean-workspace, shutdown.
            Use {"id":"1","command":"status"}; agent actions require "agent".
            clean-workspace also requires "confirm":true. EOF gracefully shuts down.
            --stdio rejects --verbose and desktop notifications.
            --once and --drain are mutually exclusive; finite modes fail on orchestration errors.

            """ + Environment.NewLine + RunOptions,
        "preflight" => """
            Usage: abacus preflight [options] --model <model> --agent <name> <workspace> [--agent ...]
            Uses the same configuration and prerequisites as run. Read-only: no ticket claims,
            workspace changes, panes, processes, cleanup, or run summary. Exits after validation.
            Run-only --once and --drain are not accepted.

            """ + Environment.NewLine + RunOptions,
        "new" => """
            Usage: abacus new <name> --agents <count>
            Requires a new single directory name and a positive agent count. Creates <name>/repo,
            a main branch, shared-server Beads database, bundled skills, an initial commit, detached
            worktrees, and run_abacus_opencode.sh / run_abacus_codex.sh / run_abacus_claude.sh.
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
        "attention" => "Usage: abacus attention <list|resolve> [options]\nUse 'abacus help attention resolve' for resolution options.",
        "attention list" => """
            Usage: abacus attention list [--repo <path>]
            Prints IDs for all issues carrying abacus:needs-user-attention, including closed issues,
            one per line with no heading. Read-only; no agent preflight.
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
