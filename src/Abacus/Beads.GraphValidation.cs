using System.Text.Json;

namespace Abacus;

internal sealed record DependencyGraphValidation(bool? CyclesClear, string Message);

public sealed partial class Beads
{
    // Read-only CLI evidence for publication/dependency preflight. It is not a
    // transaction, a readiness check, or proof that prerequisite specs integrated.
    internal static async Task<DependencyGraphValidation> CheckDependencyGraphAsync(
        Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> execute,
        CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var graph = await execute(["--readonly", "graph", "check", "--json"], token);
            var verdict = ParseGraphCheck(graph);
            if (verdict is null) return UnknownGraph();
            if (verdict == false) return new(false, "Beads reports dependency cycles. No write was attempted.");
            token.ThrowIfCancellationRequested();
            var cycles = await execute(["--readonly", "dep", "cycles", "--json"], token);
            if (!cycles.Succeeded) return UnknownGraph();
            using var document = JsonDocument.Parse(cycles.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return UnknownGraph();
            // A nonempty second result contradicts the clean first observation;
            // do not pretend two independent reads were a coherent snapshot.
            if (document.RootElement.GetArrayLength() != 0) return UnknownGraph();
            return new(true, "Both Beads cycle checks were clean. This does not prove readiness, prerequisite integration, or writer quiescence.");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or
            CommandStartException or CommandTimeoutException or CommandOutputLimitException)
        { return UnknownGraph(); }
    }

    private static DependencyGraphValidation UnknownGraph() => new(null,
        "Dependency graph validation is unavailable, unsupported or inconsistent. No write was attempted; do not publish based on this result.");

    private static bool? ParseGraphCheck(CommandResult result)
    {
        if (result.ExitCode is not (0 or 1)) return null;
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        if (!GraphFields(root, "clean", "cycles", "schema_version", "summary") ||
            !GraphInteger(root.GetProperty("schema_version"), out var version) || version != 1 ||
            root.GetProperty("clean").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        var summary = root.GetProperty("summary");
        if (!GraphFields(summary, "cycle_count") || !GraphInteger(summary.GetProperty("cycle_count"), out var count) || count < 0) return null;
        var clean = root.GetProperty("clean").GetBoolean();
        var cycles = root.GetProperty("cycles");
        if (clean) return result.Succeeded && count == 0 && (cycles.ValueKind == JsonValueKind.Null ||
            cycles.ValueKind == JsonValueKind.Array && cycles.GetArrayLength() == 0) ? true : null;
        return result.ExitCode == 1 && count > 0 && cycles.ValueKind == JsonValueKind.Array && cycles.GetArrayLength() == count &&
            cycles.EnumerateArray().All(cycle => cycle.ValueKind == JsonValueKind.Array && cycle.GetArrayLength() > 0 &&
                cycle.EnumerateArray().All(id => id.ValueKind == JsonValueKind.String && Git.IsValidIssueId(id.GetString()!))) ? false : null;
    }

    private static bool GraphInteger(JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    private static bool GraphFields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = value.EnumerateObject().Select(field => field.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.All(name => expected.Contains(name, StringComparer.Ordinal));
    }
}
