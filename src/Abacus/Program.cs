using System.Reflection;

namespace Abacus;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
            var parsed = Options.Parse(args, interactive
                ? missing => RunConfigurationSelection.Select(Environment.CurrentDirectory, missing, Console.In, Console.Error)
                : null);
            if (parsed.ShowHelp)
            {
                Console.Out.WriteLine(parsed.HelpText ?? Options.Usage);
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
                    .RunAsync(workingDirectory, targetCommand, Console.Out, CancellationToken.None);
            }

            if (parsed.NewMultiAgentRepository is { } repositoryOptions)
            {
                var result = await new MultiAgentRepositoryInitializer(
                        new CommandRunner(TextWriter.Null))
                    .InitializeAsync(
                        workingDirectory,
                        repositoryOptions,
                        CancellationToken.None);
                Console.Out.WriteLine($"Initialized '{repositoryOptions.ProjectName}' at {result.ProjectRoot}");
                Console.Out.WriteLine($"Repository:     {result.RepositoryPath}");
                Console.Out.WriteLine($"Worktrees:      {result.WorktreesPath} (0-{result.AgentCount - 1})");
                Console.Out.WriteLine($"Beads database: {result.BeadsDatabase}");
                Console.Out.WriteLine(
                    $"Configs:        {string.Join(", ", result.ConfigurationPaths.Select(Path.GetFileName))}");
                Console.Out.WriteLine($"Next: change directory to {result.ProjectRoot}, then execute abacus run and select a harness config (not the shared base).");
                Console.Out.WriteLine("For non-interactive use, pass --config <path-to-harness-config> and --start-paused=false (or use stdio controls with notifications disabled).");
                Console.Out.WriteLine("Edit shared settings with abacus config edit abacus_base.json.");
                Console.Out.WriteLine("Generated runs start paused (Shift-Tab resumes claims), with all notifications and sound enabled.");
                Console.Out.WriteLine("Abacus creates its default tmux session when needed.");
                return 0;
            }

            if (parsed.InitializeRepository)
            {
                var result = await new RepositoryInitializer(new CommandRunner(TextWriter.Null))
                    .InitializeAsync(workingDirectory, ConfirmSkillOverwrite, CancellationToken.None);
                if (result.Skills.Cancelled)
                {
                    Console.Out.WriteLine("Initialization cancelled; no files were changed.");
                    return 0;
                }
                Console.Out.WriteLine($"Installed bundled skills in {result.Skills.SkillsRoot}");
                Console.Out.WriteLine($"{(result.CreatedTargets ? "Created" : "Preserved")} target configuration: {result.TargetsPath}");
                Console.Out.WriteLine($"{(result.CreatedReasoning ? "Created" : "Preserved")} reasoning configuration: {result.ReasoningPath}");
                Console.Out.WriteLine("Next: review and commit .abacus/targets.json, .abacus/reasoning.json, and .agents/skills; run abacus health and abacus targets check.");
                Console.Out.WriteLine("Missing ticket targets use defaultTarget unless enforceTargetBranch is true. Set explicit targets with abacus targets set <branch> <issue-id> [...]. No Beads settings, tickets, branches, or commits were changed.");
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
                    Console.Out.WriteLine("Skill installation cancelled; no files were changed.");
                    return 0;
                }

                Console.Out.WriteLine(
                    $"Installed {string.Join(", ", result.InstalledSkills)} in {result.SkillsRoot}");
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

            if (parsed.ShowModels)
            {
                var catalog = await new ModelCatalog(new CommandRunner(TextWriter.Null))
                    .CollectAsync(workingDirectory, CancellationToken.None);
                Console.Out.Write(catalog.Render());
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
                Console.Out.WriteLine(result.DeletedBranches.Count == 0
                    ? "No closed ticket branches to prune."
                    : $"Deleted {result.DeletedBranches.Count} closed ticket branch{(result.DeletedBranches.Count == 1 ? string.Empty : "es")}: {string.Join(", ", result.DeletedBranches)}");
                if (result.SkippedCheckedOutBranches.Count > 0)
                {
                    Console.Out.WriteLine(
                        $"Skipped checked-out branch{(result.SkippedCheckedOutBranches.Count == 1 ? string.Empty : "es")}: {string.Join(", ", result.SkippedCheckedOutBranches)}");
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
                Console.Out.WriteLine($"Resolved user attention for {attentionResolution.IssueId}{action}.");

                return 0;
            }

            return await RunOrchestratorAsync(parsed.Value!);

        }
        catch (OptionsException exception)
        {
            Console.Error.WriteLine($"abacus: {exception.Message}");
            Console.Error.WriteLine(Options.ShortUsage);
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"abacus: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunOrchestratorAsync(Options options)
    {
        using var cancellation = new CancellationTokenSource();
        using var events = options.Stdio || options.EventLogPath is not null
            ? new EventReporter(options.Stdio ? Console.Out : null, options.EventLogPath, message =>
            {
                Console.Error.WriteLine($"abacus: {message}");
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
            ReasoningModels = options.EffectiveReasoningModels,
            options.AgentMode,
            options.ExecutionMode,
            options.Agents,
        });
        try
        {
            if (AsciiIntro.ShouldPlay(options, Console.IsInputRedirected, Console.IsOutputRedirected,
                Console.IsErrorRedirected, Environment.GetEnvironmentVariable("TERM")))
                await AsciiIntro.PlayAsync(Console.Error, cancellation.Token);
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
            Console.Error.WriteLine($"abacus: {exception.Message}");
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
        Console.Error.WriteLine(
            $"The following installed skill directories already exist: {string.Join(", ", existingSkills)}");
        Console.Error.Write("Replace them with the bundled versions? [y/N] ");
        var response = Console.ReadLine()?.Trim();
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
