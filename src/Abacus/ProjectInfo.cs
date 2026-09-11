using System.Text;

namespace Abacus;

public sealed record TicketSummary(
    int Total,
    int Open,
    int InProgress,
    int Blocked,
    int Closed,
    int Unknown,
    int NeedsUserAttention);

public sealed record ProjectInfoReport(
    string RepositoryRoot,
    string ProjectName,
    WorkspaceStatus Workspace,
    string GitCommit,
    string? Origin,
    IReadOnlyList<GitWorktreeHealth> Worktrees,
    DoltIdentity? DoltIdentity,
    bool? DoltRemoteConfigured,
    string? DoltCommit,
    TicketSummary? Tickets,
    TargetRegistry? Targets,
    ReasoningPolicy? Reasoning,
    IReadOnlyList<string> Errors)
{
    public bool IsComplete => DoltIdentity is not null
        && DoltRemoteConfigured.HasValue
        && DoltCommit is not null
        && Tickets is not null
        && Targets is not null
        && Reasoning is not null
        && Errors.Count == 0;

    public string Render(bool color = false)
    {
        var ui = new TerminalUi(color);
        var writer = new StringWriter(new StringBuilder());
        ui.WriteTitle(writer, "Abacus project info");

        ui.WriteSection(writer, "Project");
        ui.WriteKeyValue(writer, "Name", ProjectName);
        ui.WriteKeyValue(writer, "Repository", RepositoryRoot);
        ui.WriteKeyValue(writer, "Branch", Workspace.Branch);
        ui.WriteKeyValue(writer, "Git commit", GitCommit);
        ui.WriteKeyValue(writer, "Working tree", Workspace.IsDirty ? "dirty" : "clean");
        ui.WriteKeyValue(writer, "Origin", Origin ?? "not configured");

        ui.WriteSection(writer, $"Git worktrees ({Worktrees.Count})");
        foreach (var worktree in Worktrees)
        {
            writer.Write("  ");
            writer.Write(ui.Info("•"));
            writer.Write(' ');
            writer.Write(TerminalUi.Sanitize(worktree.Path));
            writer.WriteLine(worktree.Branch is null
                ? $" {ui.Muted("[detached]")}"
                : $" {ui.Muted($"[{worktree.Branch}]")}");
        }

        ui.WriteSection(writer, "Beads / Dolt");
        if (DoltIdentity is { } identity)
        {
            ui.WriteKeyValue(writer, "Database", identity.Database);
            ui.WriteKeyValue(writer, "Storage", identity.Embedded ? "embedded" : "shared server");
            if (!identity.Embedded)
            {
                ui.WriteKeyValue(writer, "Server",
                    identity.Host is null || identity.Port is null
                        ? "not reported"
                        : $"{identity.Host}:{identity.Port}");
                ui.WriteKeyValue(writer, "Connection", identity.ConnectionOk ? "reachable" : "unavailable");
            }
        }
        else
        {
            ui.WriteKeyValue(writer, "Database", "unavailable");
        }
        ui.WriteKeyValue(writer, "Dolt remote", DoltRemoteConfigured switch
        {
            true => "configured",
            false => "not configured",
            null => "unavailable",
        });
        ui.WriteKeyValue(writer, "Dolt commit", DoltCommit ?? "unavailable");

        ui.WriteSection(writer, "Tickets");
        if (Tickets is { } tickets)
        {
            ui.WriteKeyValue(writer, "Total", tickets.Total.ToString());
            ui.WriteKeyValue(writer, "Open", tickets.Open.ToString());
            ui.WriteKeyValue(writer, "In progress", tickets.InProgress.ToString());
            ui.WriteKeyValue(writer, "Blocked", tickets.Blocked.ToString());
            ui.WriteKeyValue(writer, "Closed", tickets.Closed.ToString());
            if (tickets.Unknown > 0)
                ui.WriteKeyValue(writer, "Unknown status", tickets.Unknown.ToString());
            ui.WriteKeyValue(writer, "Needs attention", tickets.NeedsUserAttention.ToString());
        }
        else
        {
            ui.WriteKeyValue(writer, "Summary", "unavailable");
        }

        ui.WriteSection(writer, "Configuration");
        if (Targets is { } targets)
        {
            ui.WriteKeyValue(writer, "Targets", string.Join(", ", targets.Targets.Keys.Order(StringComparer.Ordinal)));
            ui.WriteKeyValue(writer, "Default target", targets.DefaultTarget);
            ui.WriteKeyValue(writer, "Target enforcement", targets.EnforceTargetBranch ? "enabled" : "disabled");
        }
        else
        {
            ui.WriteKeyValue(writer, "Targets", "unavailable");
        }
        ui.WriteKeyValue(writer, "Reasoning labels", Reasoning is null
            ? "unavailable"
            : Reasoning.EnforceLabels ? "required" : "optional");

        if (Errors.Count > 0)
        {
            ui.WriteSection(writer, "Unavailable data");
            foreach (var error in Errors)
                ui.WriteStatus(writer, "ERROR", error);
        }

        return writer.ToString();
    }
}

public sealed class ProjectInfoCollector(
    CommandRunner runner,
    string gitExecutable = "git",
    string beadsExecutable = "bd")
{
    public async Task<ProjectInfoReport> CollectAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var git = new Git(runner, gitExecutable);
        var workspace = await git.GetWorkspaceStatusAsync(repositoryRoot, "abacus-info", cancellationToken);
        var gitCommit = await ReadRequiredGitValueAsync(
            repositoryRoot, ["-C", repositoryRoot, "rev-parse", "HEAD"], "read the Git commit", cancellationToken);
        var worktreeOutput = await ReadRequiredGitValueAsync(
            repositoryRoot, ["-C", repositoryRoot, "worktree", "list", "--porcelain"],
            "list Git worktrees", cancellationToken, allowEmpty: true);
        var worktrees = HealthChecker.ParseWorktrees(worktreeOutput);
        var originResult = await runner.RunAsync(new CommandSpec(
            gitExecutable,
            ["-C", repositoryRoot, "remote", "get-url", "origin"],
            repositoryRoot,
            AgentName: "abacus-info"), cancellationToken);
        var origin = originResult.Succeeded && !string.IsNullOrWhiteSpace(originResult.StandardOutput)
            ? originResult.StandardOutput.Trim()
            : null;

        var errors = new List<string>();
        TargetRegistry? targets = null;
        try
        {
            targets = await TargetRegistry.LoadAsync(
                Path.Combine(repositoryRoot, ".abacus", "targets.json"), cancellationToken);
        }
        catch (TargetException exception)
        {
            errors.Add(exception.Message);
        }

        ReasoningPolicy? reasoning = null;
        try
        {
            reasoning = await ReasoningPolicy.LoadAsync(
                Path.Combine(repositoryRoot, ".abacus", "reasoning.json"), cancellationToken);
        }
        catch (ReasoningPolicyException exception)
        {
            errors.Add(exception.Message);
        }

        DoltIdentity? identity = null;
        bool? remoteConfigured = null;
        string? doltCommit = null;
        TicketSummary? tickets = null;
        try
        {
            var beads = new Beads(runner, beadsExecutable);
            identity = await beads.ReadDoltIdentityAsync(repositoryRoot, agentName: null, cancellationToken);
            remoteConfigured = await beads.HasRemoteAsync(repositoryRoot, agentName: null, cancellationToken);
            doltCommit = await beads.ReadCurrentDoltCommitAsync(repositoryRoot, agentName: null, cancellationToken);
            var issues = await beads.GetTargetAuditIssuesAsync(repositoryRoot, cancellationToken);
            tickets = new TicketSummary(
                issues.Count,
                issues.Count(static issue => issue.Status is IssueStatus.Open),
                issues.Count(static issue => issue.Status is IssueStatus.InProgress),
                issues.Count(static issue => issue.Status is IssueStatus.Blocked),
                issues.Count(static issue => issue.Status is IssueStatus.Closed),
                issues.Count(static issue => issue.Status is IssueStatus.Unknown),
                issues.Count(static issue => issue.Labels?.Contains(
                    Beads.NeedsUserAttentionLabel, StringComparer.Ordinal) is true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(exception.Message);
        }

        return new ProjectInfoReport(
            repositoryRoot,
            new DirectoryInfo(repositoryRoot).Name,
            workspace,
            gitCommit,
            origin,
            worktrees,
            identity,
            remoteConfigured,
            doltCommit,
            tickets,
            targets,
            reasoning,
            errors);
    }

    private async Task<string> ReadRequiredGitValueAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken,
        bool allowEmpty = false)
    {
        var result = await runner.RunAsync(new CommandSpec(
            gitExecutable, arguments, repositoryRoot, AgentName: "abacus-info"), cancellationToken);
        if (!result.Succeeded || (!allowEmpty && string.IsNullOrWhiteSpace(result.StandardOutput)))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : result.StandardError.Trim();
            throw new ProjectInfoException($"could not {operation}: {detail}");
        }

        return allowEmpty ? result.StandardOutput : result.StandardOutput.Trim();
    }
}

public sealed class ProjectInfoException(string message) : Exception(message);
