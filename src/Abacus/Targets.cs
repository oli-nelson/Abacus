using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus;

public sealed record TargetPolicy(string Branch, string? MergeInstructions, string Identity);
public sealed record ExecutionBinding(
    int Version, string TargetRef, string IssueBranch,
    string StartCommit, string PolicyIdentity);

/// <summary>A controller-owned snapshot. Default routing is opt-out via explicit enforcement, never inferred from labels.</summary>
public sealed class TargetRegistry(IReadOnlyDictionary<string, TargetPolicy> targets,
    bool enforceTargetBranch = false, string defaultTarget = "main")
{
    public const string DefaultConfiguration = """
        {
          "version": 1,
          "enforceTargetBranch": false,
          "defaultTarget": "main",
          "targets": { "main": {} }
        }
        """ + "\n";

    public bool EnforceTargetBranch { get; } = enforceTargetBranch;
    public string DefaultTarget { get; } = defaultTarget;
    public IReadOnlyDictionary<string, TargetPolicy> Targets { get; } = targets;

    public TargetPolicy Resolve(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new TargetException("required metadata 'abacus_target' is missing or is not a nonempty string");
        if (!Targets.TryGetValue(branch, out var policy))
            throw new TargetException($"metadata 'abacus_target' names unconfigured target '{branch}'");
        return policy;
    }

    public static async Task<TargetRegistry> LoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var root = document.RootElement;
            // Accept the retired field for existing configs, but never use or emit it.
            CheckProperties(root, "version", "repositoryId", "targets", "enforceTargetBranch", "defaultTarget");
            if (root.GetProperty("version").GetInt32() != 1)
                throw new TargetException("unsupported target configuration version (expected 1)");
            var enforce = root.TryGetProperty("enforceTargetBranch", out var enforcement)
                && enforcement.GetBoolean();
            var hasDefault = root.TryGetProperty("defaultTarget", out var configuredDefault);
            var defaultBranch = hasDefault ? configuredDefault.GetString() : "main";
            if (string.IsNullOrWhiteSpace(defaultBranch) || !Git.IsValidTargetBranch(defaultBranch))
                throw new TargetException("defaultTarget must name a literal local target branch");
            var targetObject = root.GetProperty("targets");
            if (targetObject.ValueKind != JsonValueKind.Object)
                throw new TargetException("targets must be an object");
            var targets = new Dictionary<string, TargetPolicy>(StringComparer.Ordinal);
            // All paths are relative to the config directory, never an agent's changing checkout.
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var fallbackPath = Path.Combine(directory, "merge-instructions.md");
            var fallback = File.Exists(fallbackPath)
                ? (await File.ReadAllTextAsync(fallbackPath, cancellationToken)).Trim() : null;
            foreach (var property in targetObject.EnumerateObject())
            {
                CheckProperties(property.Value, "mergeInstructions");
                if (!Git.IsValidTargetBranch(property.Name))
                    throw new TargetException($"invalid target branch '{property.Name}'");
                string? instructions = fallback;
                if (property.Value.TryGetProperty("mergeInstructions", out var file))
                {
                    var relative = file.GetString();
                    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                        throw new TargetException("mergeInstructions must be a nonempty relative file path");
                    instructions = (await File.ReadAllTextAsync(Path.Combine(directory, relative), cancellationToken)).Trim();
                }
                var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { version = 1, branch = property.Name, instructions }))));
                if (!targets.TryAdd(property.Name, new(property.Name, instructions, identity)))
                    throw new TargetException($"duplicate target '{property.Name}'");
            }
            if (targets.Count == 0) throw new TargetException("at least one target must be configured");
            if ((!enforce || hasDefault) && !targets.ContainsKey(defaultBranch))
                throw new TargetException($"defaultTarget '{defaultBranch}' must be included in targets; configure the intended default explicitly");
            return new TargetRegistry(targets, enforce, defaultBranch);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException)
        {
            throw new TargetException($"could not load target configuration '{path}': {exception.Message}");
        }
    }

    private static void CheckProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new TargetException("expected a configuration object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new TargetException($"unknown or duplicate configuration property '{property.Name}'");
    }

    public TargetPolicy Validate(BeadsIssue issue)
    {
        if (issue.MetadataError is not null) throw new TargetException(issue.MetadataError);
        if (issue.HasInvalidTargetMetadata)
            throw new TargetException("metadata 'abacus_target' must be a nonempty string when supplied");
        var policy = Resolve(issue.TargetBranch is null && !EnforceTargetBranch ? DefaultTarget : issue.TargetBranch);
        if (issue.Binding is { } binding)
        {
            if (binding.Version != 1
                || binding.TargetRef != $"refs/heads/{policy.Branch}"
                || binding.IssueBranch != $"abacus/{issue.Id}"
                || binding.PolicyIdentity != policy.Identity || !Git.IsCommitId(binding.StartCommit))
                throw new TargetException("execution binding conflicts with the ticket target or merge policy; preserve the branch and request operator recovery");
        }
        return policy;
    }
}

public sealed class TargetException(string message) : Exception(message);
