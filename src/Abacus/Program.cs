using System.Reflection;

namespace Abacus;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var stdoutUi = TerminalUi.ForConsoleOut();
        var stderrUi = TerminalUi.ForConsoleError();
        try
        {
            var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
            var parsed = Options.Parse(args, interactive
                ? missing => RunConfigurationSelection.Select(
                    Environment.CurrentDirectory, missing, Console.In, Console.Error, stderrUi.ColorEnabled)
                : null);
            if (parsed.ShowHelp)
            {
                Console.Out.WriteLine(stdoutUi.RenderHelp(parsed.HelpText ?? Options.Usage));
                return 0;
            }

            if (parsed.ShowVersion)
            {
                Console.Out.WriteLine(typeof(Program).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
                return 0;
            }

            if (parsed.EditConfiguration)
                return RunConfigurationEditor.Run(parsed.ConfigurationInput, parsed.ConfigurationOutput);

            var workingDirectory = Environment.CurrentDirectory;
            if (parsed.Value is null && parsed.NewMultiAgentRepository is null
                && !parsed.ShowModels && !parsed.ShowHealth && parsed.TargetCommand is null)
                workingDirectory = await new Git(new CommandRunner(TextWriter.Null))
                    .ResolveMainRepositoryAsync(workingDirectory, parsed.RepositoryPath, CancellationToken.None);

            if (parsed.TargetCommand is { } targetCommand)
            {
                var runner = new CommandRunner(TextWriter.Null);
                return await new TicketTargets(new Beads(runner), new Git(runner))
                    .RunAsync(workingDirectory, targetCommand, Console.Out, CancellationToken.None,
                        stdoutUi.ColorEnabled);
            }

            if (parsed.NewMultiAgentRepository is { } repositoryOptions)
            {
                var result = await new MultiAgentRepositoryInitializer(
                        new CommandRunner(TextWriter.Null))
                    .InitializeAsync(
                        workingDirectory,
                        repositoryOptions,
                        CancellationToken.None);
                stdoutUi.WriteTitle(Console.Out, "Abacus project created");
                stdoutUi.WriteStatus(Console.Out, "OK", $"Initialized '{repositoryOptions.ProjectName}'.");
                stdoutUi.WriteSection(Console.Out, "Project layout");
                stdoutUi.WriteKeyValue(Console.Out, "Project", result.ProjectRoot);
                stdoutUi.WriteKeyValue(Console.Out, "Repository", result.RepositoryPath);
                stdoutUi.WriteKeyValue(Console.Out, "Worktrees", $"{result.WorktreesPath} (0-{result.AgentCount - 1})");
                stdoutUi.WriteKeyValue(Console.Out, "Beads database", result.BeadsDatabase);
                stdoutUi.WriteKeyValue(Console.Out, "Configs",
                    string.Join(", ", result.ConfigurationPaths.Select(Path.GetFileName)));
                stdoutUi.WriteSection(Console.Out, "Next steps");
                stdoutUi.WriteStep(Console.Out, 1, $"Change directory to {result.ProjectRoot}.");
                stdoutUi.WriteStep(Console.Out, 2, "From the project directory, execute abacus run and select a harness config (not the shared base).");
                stdoutUi.WriteStep(Console.Out, 3, "Edit shared settings with abacus config edit abacus_base.json.");
                Console.Out.WriteLine();
                Console.Out.WriteLine(stdoutUi.Muted("Generated runs start paused; Shift-Tab resumes claims. Desktop notifications, notification sound, and TUI intro audio are enabled, and Abacus creates its default tmux session when needed."));
                Console.Out.WriteLine(stdoutUi.Muted("For non-interactive use, pass --config <path-to-harness-config> and --start-paused=false, or use stdio controls with notifications disabled."));
                return 0;
            }

            if (parsed.InitializeRepository)
            {
                var result = await new RepositoryInitializer(new CommandRunner(TextWriter.Null))
                    .InitializeAsync(workingDirectory, ConfirmSkillOverwrite, CancellationToken.None);
                if (result.Skills.Cancelled)
                {
                    stdoutUi.WriteStatus(Console.Out, "WARN", "Initialization cancelled; no files were changed.");
                    return 0;
                }
                stdoutUi.WriteTitle(Console.Out, "Abacus repository initialized");
                stdoutUi.WriteStatus(Console.Out, "OK", "Repository setup files are ready for review.");
                stdoutUi.WriteSection(Console.Out, "Installed and configured");
                stdoutUi.WriteKeyValue(Console.Out, "Bundled skills", result.Skills.SkillsRoot);
                stdoutUi.WriteKeyValue(Console.Out, result.CreatedTargets ? "Created targets" : "Preserved targets", result.TargetsPath);
                stdoutUi.WriteKeyValue(Console.Out, result.CreatedReasoning ? "Created reasoning" : "Preserved reasoning",
                    result.ReasoningPath ?? "(not configured)");
                stdoutUi.WriteSection(Console.Out, "Next steps");
                stdoutUi.WriteStep(Console.Out, 1, "Review and commit .abacus/targets.json, .abacus/reasoning.json, and .agents/skills.");
                stdoutUi.WriteStep(Console.Out, 2, "Run abacus health.");
                stdoutUi.WriteStep(Console.Out, 3, "Run abacus targets check.");
                Console.Out.WriteLine();
                Console.Out.WriteLine(stdoutUi.Muted("Missing ticket targets use defaultTarget unless enforceTargetBranch is true. Set explicit targets with abacus targets set <branch> <issue-id> [...]. No Beads settings, tickets, branches, or commits were changed."));
                return 0;
            }

            if (parsed.InstallSkills)
            {
                var installer = new SkillInstaller(new CommandRunner(TextWriter.Null));
                var result = await installer.InstallAsync(
                    workingDirectory,
                    ConfirmSkillOverwrite,
                    CancellationToken.None);
                if (result.Cancelled)
                {
                    stdoutUi.WriteStatus(Console.Out, "WARN", "Skill installation cancelled; no files were changed.");
                    return 0;
                }

                stdoutUi.WriteTitle(Console.Out, "Bundled skills installed");
                foreach (var skill in result.InstalledSkills)
                    stdoutUi.WriteStatus(Console.Out, "OK", skill);
                stdoutUi.WriteKeyValue(Console.Out, "Destination", result.SkillsRoot);
                return 0;
            }

            if (parsed.ShowHealth)
            {
                var health = await new HealthChecker(new CommandRunner(TextWriter.Null))
                    .RunAsync(workingDirectory, CancellationToken.None, parsed.RepositoryPath);
                Console.Out.Write(health.Render(HealthReport.ShouldUseColor(
                    Console.IsOutputRedirected,
                    Environment.GetEnvironmentVariable("TERM"),
                    Environment.GetEnvironmentVariable("NO_COLOR"))));
                return health.IsHealthy ? 0 : 1;
            }

            if (parsed.ShowInfo)
            {
                var info = await new ProjectInfoCollector(new CommandRunner(TextWriter.Null))
                    .CollectAsync(workingDirectory, CancellationToken.None);
                Console.Out.Write(info.Render(stdoutUi.ColorEnabled));
                return info.IsComplete ? 0 : 1;
            }

            if (parsed.ShowModels)
            {
                var catalog = await new ModelCatalog(new CommandRunner(TextWriter.Null))
                    .CollectAsync(workingDirectory, CancellationToken.None);
                Console.Out.Write(catalog.Render(stdoutUi.ColorEnabled));
                return catalog.HasModels ? 0 : 1;
            }

            if (parsed.ListUserAttention)
            {
                var issues = await new Beads(new CommandRunner(TextWriter.Null))
                    .GetIssuesNeedingUserAttentionAsync(
                        workingDirectory,
                        CancellationToken.None);
                foreach (var issueId in issues
                    .Select(static issue => issue.Id)
                    .Order(StringComparer.Ordinal))
                {
                    Console.Out.WriteLine(issueId);
                }

                return 0;
            }

            if (parsed.PruneClosedBranches)
            {
                var runner = new CommandRunner(TextWriter.Null);
                var closedIssues = await new Beads(runner)
                    .GetClosedIssuesAsync(workingDirectory, CancellationToken.None);
                var result = await new Git(runner)
                    .PruneClosedIssueBranchesAsync(
                        workingDirectory,
                        closedIssues.Select(static issue => issue.Id),
                        CancellationToken.None);
                stdoutUi.WriteTitle(Console.Out, "Closed branch cleanup");
                if (result.DeletedBranches.Count == 0)
                {
                    stdoutUi.WriteStatus(Console.Out, "OK", "No closed ticket branches to prune.");
                }
                else
                {
                    stdoutUi.WriteStatus(Console.Out, "OK",
                        $"Deleted {result.DeletedBranches.Count} closed ticket branch{(result.DeletedBranches.Count == 1 ? string.Empty : "es")}.");
                    foreach (var branch in result.DeletedBranches)
                        Console.Out.WriteLine($"    {stdoutUi.Success("−")} {TerminalUi.Sanitize(branch)}");
                }
                if (result.SkippedCheckedOutBranches.Count > 0)
                {
                    stdoutUi.WriteSection(Console.Out, "Skipped");
                    stdoutUi.WriteStatus(Console.Out, "WARN",
                        $"{result.SkippedCheckedOutBranches.Count} checked-out branch{(result.SkippedCheckedOutBranches.Count == 1 ? string.Empty : "es")} preserved.");
                    foreach (var branch in result.SkippedCheckedOutBranches)
                        Console.Out.WriteLine($"    {stdoutUi.Warning("•")} {TerminalUi.Sanitize(branch)}");
                }

                return 0;
            }

            if (parsed.AttentionResolution is { } attentionResolution)
            {
                await new Beads(new CommandRunner(TextWriter.Null))
                    .ResolveUserAttentionAsync(
                        workingDirectory,
                        attentionResolution.IssueId,
                        attentionResolution.Message,
                        attentionResolution.Reopen,
                        CancellationToken.None);
                var action = (attentionResolution.Message is not null, attentionResolution.Reopen) switch
                {
                    (false, false) => "",
                    (true, false) => " and recorded the response",
                    (false, true) => " and reopened the ticket",
                    (true, true) => ", recorded the response, and reopened the ticket",
                };
                stdoutUi.WriteTitle(Console.Out, "User attention resolved");
                stdoutUi.WriteStatus(Console.Out, "OK",
                    $"Resolved user attention for {attentionResolution.IssueId}{action}.");

                return 0;
            }

            return await RunOrchestratorAsync(parsed.Value!);

        }
        catch (OptionsException exception)
        {
            Console.Error.WriteLine(stderrUi.Error($"abacus: {exception.Message}"));
            Console.Error.WriteLine(stderrUi.RenderHelp(Options.ShortUsage));
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(stderrUi.Error($"abacus: {exception.Message}"));
            return 1;
        }
    }

    private static async Task<int> RunOrchestratorAsync(Options options)
    {
        var stderrUi = TerminalUi.ForConsoleError();
        using var cancellation = new CancellationTokenSource();
        using var events = options.Stdio || options.EventLogPath is not null
            ? new EventReporter(options.Stdio ? Console.Out : null, options.EventLogPath, message =>
            {
                Console.Error.WriteLine(stderrUi.Error($"abacus: {message}"));
                cancellation.Cancel();
            }) : null;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        var exitCode = 0;
        events?.Emit("run.starting", new
        {
            options.Model,
            options.Effort,
            ReasoningModels = options.EffectiveReasoningModels,
            ReasoningEfforts = options.EffectiveReasoningEfforts,
            ExtraArguments = options.EffectiveExtraArguments,
            ReasoningArguments = options.EffectiveReasoningArguments,
            options.AgentMode,
            options.ExecutionMode,
            options.Agents,
        });
        try
        {
            if (AsciiIntro.ShouldPlay(options, Console.IsInputRedirected, Console.IsOutputRedirected,
                Console.IsErrorRedirected, Environment.GetEnvironmentVariable("TERM")))
            {
                var introSound = options.TuiAudio ? IntroSound.TryStart() : null;
                try
                {
                    if (await AsciiIntro.PlayAsync(Console.Error, cancellation.Token))
                    {
                        introSound?.ContinueInBackground();
                        introSound = null;
                    }
                }
                finally
                {
                    if (introSound is not null) await introSound.DisposeAsync();
                }
            }
            using var output = new ConsoleOutput(
                options.Stdio ? TextWriter.Null : Console.Error,
                options.Agents.Select(static agent => agent.Name),
                options.Model,
                options.Verbose,
                interactive: options.Stdio || options.CheckOnly ? false : null,
                workspacePaths: options.Agents.ToDictionary(
                    static agent => agent.Name, static agent => agent.WorkspacePath, StringComparer.Ordinal),
                events: events,
                startPaused: options.StartPaused,
                effort: options.Effort,
                effortIsRequested: options.AgentMode == AgentMode.OpenCode);
            if (options.StartPaused && !options.Stdio && !output.IsInteractiveDashboard)
                throw new InvalidOperationException(
                    "--start-paused requires an interactive dashboard or --stdio so claims can be resumed");
            await using var notifier = new DesktopNotifier(
                new CommandRunner(output, commandTimeout: TimeSpan.FromSeconds(3),
                    terminationTimeout: TimeSpan.FromSeconds(1)),
                output, Console.Error, options.NotificationMode, options.NotificationSound);
            var runner = new CommandRunner(output);
            var validated = await new Preflight(runner).RunAsync(options, cancellation.Token);
            if (options.CheckOnly)
            {
                await output.SystemAsync(
                    $"Preflight checks passed for {validated.Agents.Count} agent{(validated.Agents.Count == 1 ? string.Empty : "s")}; no tickets claimed");
            }
            else
            {
                await output.SystemAsync(
                    $"Preflight complete; starting {AgentCommandFactory.DisplayName(options.AgentMode)} agent loops");
                await new AbacusApplication(runner, output, notifier).RunAsync(validated, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            exitCode = events?.HasFailed == true ? 1 : 130;
        }
        catch (Exception exception)
        {
            exitCode = 1;
            events?.Emit("run.error", new { message = exception.Message });
            Console.Error.WriteLine(stderrUi.Error($"abacus: {exception.Message}"));
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
        if (events?.HasFailed == true) exitCode = 1;
        events?.Emit("run.exited", new { exitCode });
        return events?.HasFailed == true ? 1 : exitCode;
    }

    private static bool ConfirmSkillOverwrite(IReadOnlyList<string> existingSkills)
    {
        var ui = TerminalUi.ForConsoleError();
        ui.WriteTitle(Console.Error, "Existing bundled skills found");
        ui.WriteStatus(Console.Error, "WARN", "Replacing a skill replaces its complete directory.");
        foreach (var skill in existingSkills)
            Console.Error.WriteLine($"    {ui.Warning("•")} {TerminalUi.Sanitize(skill)}");
        Console.Error.WriteLine();
        Console.Error.Write($"{ui.Label("Replace with bundled versions?")} {ui.Muted("[y/N]")} {ui.Command("›")} ");
        Console.Error.Flush();
        var response = Console.ReadLine()?.Trim();
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
