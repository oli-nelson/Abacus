using System.Text.Json;

namespace Abacus;

public sealed record ModelResolution(string Model, string Effort, string? Label, bool UsedDefaultModel, bool UsedDefaultEffort)
{
    public bool UsedDefault => UsedDefaultModel;
}

/// <summary>Project-owned policy for routing reasoning-labelled tickets to run-local models.</summary>
public sealed class ReasoningPolicy(bool enforceLabels = false)
{
    public const string HighLabel = "abacus:high_reasoning";
    public const string MediumLabel = "abacus:medium_reasoning";
    public const string LowLabel = "abacus:low_reasoning";

    public static IReadOnlyList<string> Labels { get; } =
        [HighLabel, MediumLabel, LowLabel];

    public const string DefaultConfiguration = """
        {
          "version": 1,
          "enforceLabels": false
        }
        """ + "\n";

    public bool EnforceLabels { get; } = enforceLabels;

    public static async Task<ReasoningPolicy> LoadAsync(
        string path,
        CancellationToken cancellationToken,
        bool allowMissing = true)
    {
        if (allowMissing && !File.Exists(path) && !Directory.Exists(path))
            return new ReasoningPolicy();

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            var root = document.RootElement;
            CheckProperties(root, "version", "enforceLabels");
            if (root.GetProperty("version").GetInt32() != 1)
                throw new ReasoningPolicyException("unsupported reasoning configuration version (expected 1)");
            var enforce = root.TryGetProperty("enforceLabels", out var enforcement)
                && enforcement.GetBoolean();
            return new ReasoningPolicy(enforce);
        }
        catch (ReasoningPolicyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException
            or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ReasoningPolicyException(
                $"could not load reasoning configuration '{path}': {exception.Message}");
        }
    }

    public void ValidateMappings(IReadOnlyDictionary<string, string> mappings)
    {
        if (!EnforceLabels) return;
        var missing = Labels.Where(label => !mappings.ContainsKey(label)).ToArray();
        if (missing.Length > 0)
            throw new ReasoningPolicyException(
                "reasoning label enforcement requires model mappings for: "
                + string.Join(", ", missing));
    }

    public ModelResolution ResolveModel(
        BeadsIssue issue,
        IReadOnlyDictionary<string, string> mappings,
        string defaultModel,
        IReadOnlyDictionary<string, string>? effortMappings = null,
        string defaultEffort = "high")
    {
        var present = (issue.Labels ?? [])
            .Where(Labels.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (present.Length > 1)
            throw new ReasoningLabelException(
                $"ticket has multiple reasoning labels: {string.Join(", ", present)}");
        if (present.Length == 0)
        {
            if (EnforceLabels)
                throw new ReasoningLabelException(
                    $"ticket requires exactly one reasoning label: {string.Join(", ", Labels)}");
            return new ModelResolution(defaultModel, defaultEffort, Label: null,
                UsedDefaultModel: true, UsedDefaultEffort: true);
        }

        var label = present[0];
        var usedDefaultModel = !mappings.TryGetValue(label, out var model);
        if (usedDefaultModel && EnforceLabels)
            throw new ReasoningPolicyException($"no model mapping is configured for {label}");
        var mappedEffort = effortMappings is not null && effortMappings.TryGetValue(label, out var configuredEffort)
            ? configuredEffort
            : null;
        var usedDefaultEffort = mappedEffort is null;
        return new ModelResolution(model ?? defaultModel, mappedEffort ?? defaultEffort, label,
            usedDefaultModel, usedDefaultEffort);
    }

    public static string LabelForTier(string tier) => tier switch
    {
        "high" => HighLabel,
        "medium" => MediumLabel,
        "low" => LowLabel,
        _ => throw new ArgumentException($"unknown reasoning tier '{tier}'", nameof(tier)),
    };

    private static void CheckProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ReasoningPolicyException("expected a reasoning configuration object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new ReasoningPolicyException(
                    $"unknown or duplicate reasoning configuration property '{property.Name}'");
    }
}

public sealed class ReasoningPolicyException(string message) : Exception(message);
public sealed class ReasoningLabelException(string message) : Exception(message);
