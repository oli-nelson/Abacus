namespace Abacus;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = Options.Parse(args);
            if (parsed.ShowHelp)
            {
                Console.Out.WriteLine(Options.Usage);
                return 0;
            }

            if (parsed.InstallSkills)
            {
                var installer = new SkillInstaller(new CommandRunner(TextWriter.Null));
                var result = await installer.InstallAsync(
                    Environment.CurrentDirectory,
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
                    .RunAsync(Environment.CurrentDirectory, CancellationToken.None);
                Console.Out.Write(health.Render());
                return health.IsHealthy ? 0 : 1;
            }

            if (parsed.ShowModels)
            {
                var catalog = await new ModelCatalog(new CommandRunner(TextWriter.Null))
                    .CollectAsync(Environment.CurrentDirectory, CancellationToken.None);
                Console.Out.Write(catalog.Render());
                return catalog.HasModels ? 0 : 1;
            }

            if (parsed.ListUserAttention)
            {
                var issues = await new Beads(new CommandRunner(TextWriter.Null))
                    .GetIssuesNeedingUserAttentionAsync(
                        Environment.CurrentDirectory,
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
                    .GetClosedIssuesAsync(Environment.CurrentDirectory, CancellationToken.None);
                var result = await new Git(runner)
                    .PruneClosedIssueBranchesAsync(
                        Environment.CurrentDirectory,
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
                        Environment.CurrentDirectory,
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

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            Console.CancelKeyPress += cancelHandler;
            using var output = new ConsoleOutput(
                Console.Error,
                parsed.Value!.Agents.Select(static agent => agent.Name),
                parsed.Value.Model,
                parsed.Value.Verbose);
            await using var notifier = new DesktopNotifier(
                new CommandRunner(
                    output,
                    commandTimeout: TimeSpan.FromSeconds(3),
                    terminationTimeout: TimeSpan.FromSeconds(1)),
                output,
                Console.Error,
                parsed.Value.NotificationMode,
                parsed.Value.NotificationSound);
            try
            {
                var runner = new CommandRunner(output);
                var preflight = new Preflight(runner);
                var validated = await preflight.RunAsync(parsed.Value, cancellation.Token);
                if (parsed.Value.CheckOnly)
                {
                    await output.SystemAsync(
                        $"Preflight checks passed for {validated.Agents.Count} agent{(validated.Agents.Count == 1 ? string.Empty : "s")}; no tickets claimed");
                    return 0;
                }

                await output.SystemAsync(
                    $"Preflight complete; starting {AgentCommandFactory.DisplayName(parsed.Value.AgentMode)} agent loops");
                await new AbacusApplication(runner, output, notifier)
                    .RunAsync(validated, cancellation.Token);
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
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
