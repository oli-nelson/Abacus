using System.Text;
using System.Text.Json;

namespace Abacus;

public sealed record HarnessModels(
    string Harness,
    IReadOnlyList<string> ModelIds,
    string? Note = null);

public sealed record ModelCatalogReport(IReadOnlyList<HarnessModels> Harnesses)
{
    public bool HasModels => Harnesses.Any(static harness => harness.ModelIds.Count > 0);

    public string Render()
    {
        var text = new StringBuilder();
        foreach (var (harness, index) in Harnesses.Select((value, index) => (value, index)))
        {
            if (index > 0)
            {
                text.AppendLine();
            }

            text.Append(harness.Harness).AppendLine(":");
            foreach (var modelId in harness.ModelIds)
            {
                text.Append("  ").AppendLine(modelId);
            }

            if (harness.Note is not null)
            {
                text.Append("  (").Append(harness.Note).AppendLine(")");
            }
        }

        return text.ToString();
    }
}

public sealed class ModelCatalog(CommandRunner runner, string? executablePath = null)
{
    private readonly string path = executablePath
        ?? Environment.GetEnvironmentVariable("PATH")
        ?? string.Empty;

    public async Task<ModelCatalogReport> CollectAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var openCode = CollectOpenCodeAsync(workingDirectory, cancellationToken);
        var codex = CollectCodexAsync(workingDirectory, cancellationToken);

        return new ModelCatalogReport([
            await openCode,
            await codex,
            CollectClaude(),
        ]);
    }

    private async Task<HarnessModels> CollectOpenCodeAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        const string harness = "OpenCode";
        var executable = FindExecutable("opencode");
        if (executable is null)
        {
            return Missing(harness);
        }

        try
        {
            var result = await runner.RunAsync(
                new CommandSpec(executable, ["models"], workingDirectory),
                cancellationToken);
            if (!result.Succeeded)
            {
                return Failed(harness, result);
            }

            var models = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static model => model.Contains('/', StringComparison.Ordinal)
                    && !model.Any(char.IsWhiteSpace))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return models.Length > 0
                ? new HarnessModels(harness, models)
                : Empty(harness);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(harness, exception.Message);
        }
    }

    private async Task<HarnessModels> CollectCodexAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        const string harness = "Codex";
        var executable = FindExecutable("codex");
        if (executable is null)
        {
            return Missing(harness);
        }

        try
        {
            var result = await runner.RunAsync(
                new CommandSpec(executable, ["debug", "models"], workingDirectory),
                cancellationToken);
            if (!result.Succeeded)
            {
                return Failed(harness, result);
            }

            var models = ParseCodexModels(result.StandardOutput);
            return models.Count > 0
                ? new HarnessModels(harness, models)
                : Empty(harness);
        }
        catch (JsonException exception)
        {
            return Failed(harness, $"could not parse model catalog: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(harness, exception.Message);
        }
    }

    private HarnessModels CollectClaude()
    {
        const string harness = "Claude Code";
        return FindExecutable("claude") is null
            ? Missing(harness)
            : new HarnessModels(
                harness,
                [],
                "installed CLI does not expose non-interactive model discovery; use /model inside Claude Code");
    }

    internal static IReadOnlyList<string> ParseCodexModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("expected a models array");
        }

        return models.EnumerateArray()
            .Where(static model => model.ValueKind == JsonValueKind.Object)
            .Where(static model => !model.TryGetProperty("visibility", out var visibility)
                || visibility.ValueKind != JsonValueKind.String
                || !string.Equals(visibility.GetString(), "hide", StringComparison.OrdinalIgnoreCase))
            .Select(static model => model.TryGetProperty("slug", out var slug)
                && slug.ValueKind == JsonValueKind.String
                    ? slug.GetString()?.Trim()
                    : null)
            .Where(static slug => !string.IsNullOrWhiteSpace(slug) && !slug.Any(char.IsWhiteSpace))
            .Select(static slug => slug!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private string? FindExecutable(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var mode = File.GetUnixFileMode(candidate);
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static HarnessModels Missing(string harness) =>
        new(harness, [], "not installed or not executable on PATH");

    private static HarnessModels Empty(string harness) =>
        new(harness, [], "model command returned no available model IDs");

    private static HarnessModels Failed(string harness, CommandResult result) =>
        Failed(harness, string.IsNullOrWhiteSpace(result.StandardError)
            ? $"model command exited with code {result.ExitCode}"
            : $"model command exited with code {result.ExitCode}: {FirstLine(result.StandardError)}");

    private static HarnessModels Failed(string harness, string detail) =>
        new(harness, [], $"unavailable: {detail}");

    private static string FirstLine(string value)
    {
        var line = value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? value.Trim();
        return line.Length <= 240 ? line : line[..240] + "...";
    }
}
