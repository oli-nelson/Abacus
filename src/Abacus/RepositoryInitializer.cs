using System.Text.Json;

namespace Abacus;

public sealed record RepositoryInitializationResult(
    string TargetsPath, bool CreatedTargets, SkillInstallationResult Skills,
    string? ReasoningPath = null, bool CreatedReasoning = false);

/// <summary>Opt-in setup for an existing Git/Beads project, not a database bootstrapper.</summary>
public sealed class RepositoryInitializer(
    CommandRunner runner, string gitExecutable = "git", string beadsExecutable = "bd")
{
    public async Task<RepositoryInitializationResult> InitializeAsync(
        string workingDirectory,
        Func<IReadOnlyList<string>, bool> confirmOverwrite,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = await new Git(runner, gitExecutable)
            .ResolveMainRepositoryAsync(workingDirectory, null, cancellationToken);

        // `where` alone can discover an ancestor project or an environment-selected
        // unrelated database. Check the source (before redirects) against this Git repo.
        var where = await RequiredAsync(beadsExecutable, ["where", "--json"], repositoryRoot,
            "find an initialized Beads project; set up this repository with bd init first", cancellationToken);
        string beadsOrigin;
        try
        {
            using var document = JsonDocument.Parse(where);
            var root = document.RootElement;
            var path = root.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path))
                throw new JsonException("missing or invalid Beads directory");
            beadsOrigin = root.TryGetProperty("redirected_from", out var redirected)
                && !string.IsNullOrWhiteSpace(redirected.GetString()) ? redirected.GetString()! : path;
            if (!Path.IsPathRooted(beadsOrigin) || !Directory.Exists(beadsOrigin))
                throw new JsonException("invalid Beads redirect source");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new RepositoryInitializationException(
                $"could not confirm an initialized Beads project: {exception.Message}. Set up this repository with bd init first");
        }

        var commonArgs = new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" };
        var repositoryGit = await RequiredAsync(gitExecutable, commonArgs, repositoryRoot,
            "identify the Git repository", cancellationToken);
        var beadsGit = await RequiredAsync(gitExecutable, commonArgs, beadsOrigin,
            "verify Beads belongs to this Git repository; set up this repository with bd init first", cancellationToken);
        if (!string.Equals(repositoryGit.Trim(), beadsGit.Trim(), StringComparison.Ordinal))
            throw new RepositoryInitializationException(
                "the active Beads project belongs to a different Git repository; check BEADS_DIR/BEADS_DB and set up this repository with bd init first");

        // Confirm a usable project, not just a leftover .beads directory. No writes.
        var issues = await RequiredAsync(beadsExecutable, ["list", "--limit", "1", "--json"], repositoryRoot,
            "read the existing Beads project; check bd init and database connectivity", cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(issues);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("expected a ticket array");
        }
        catch (JsonException exception)
        {
            throw new RepositoryInitializationException($"could not read the Beads project: {exception.Message}");
        }

        var targetsPath = Path.Combine(repositoryRoot, ".abacus", "targets.json");
        var targetsExisting = File.Exists(targetsPath) || Directory.Exists(targetsPath);
        var reasoningPath = Path.Combine(repositoryRoot, ".abacus", "reasoning.json");
        var reasoningExisting = File.Exists(reasoningPath) || Directory.Exists(reasoningPath);
        var branches = targetsExisting
            ? (await TargetRegistry.LoadAsync(targetsPath, cancellationToken)).Targets.Keys.ToArray()
            : ["main"];
        if (reasoningExisting)
            await ReasoningPolicy.LoadAsync(reasoningPath, cancellationToken, allowMissing: false);
        var git = new Git(runner, gitExecutable);
        foreach (var branch in branches)
        {
            try { await git.ResolveTargetCommitAsync(repositoryRoot, "init", branch, cancellationToken); }
            catch (TargetException exception)
            {
                throw new RepositoryInitializationException(
                    $"{exception.Message}. Create the intended local branch or configure {targetsPath} explicitly before running init; no branches are created automatically");
            }
        }

        var skills = await new SkillInstaller(runner, gitExecutable)
            .InstallAsync(repositoryRoot, confirmOverwrite, cancellationToken);
        if (skills.Cancelled)
            return new(targetsPath, CreatedTargets: false, skills, reasoningPath, CreatedReasoning: false);

        // Publish complete files without ever replacing concurrently created configs.
        var createdTargets = !targetsExisting
            && await CreateConfigurationAsync(targetsPath, TargetRegistry.DefaultConfiguration, cancellationToken);
        var createdReasoning = !reasoningExisting
            && await CreateConfigurationAsync(reasoningPath, ReasoningPolicy.DefaultConfiguration, cancellationToken);
        return new(targetsPath, createdTargets, skills, reasoningPath, createdReasoning);
    }

    private static async Task<bool> CreateConfigurationAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken);
            File.Move(temporaryPath, path, overwrite: false);
            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<string> RequiredAsync(string executable, IReadOnlyList<string> arguments,
        string directory, string operation, CancellationToken token)
    {
        var result = await runner.RunAsync(new CommandSpec(executable, arguments, directory), token);
        if (!result.Succeeded)
            throw new RepositoryInitializationException($"could not {operation}: {result.StandardError.Trim()} (exit {result.ExitCode})");
        return result.StandardOutput;
    }
}
