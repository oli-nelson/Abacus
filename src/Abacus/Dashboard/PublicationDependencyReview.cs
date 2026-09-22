namespace Abacus.Dashboard;

// Coverage only, not readiness: traverse every recorded relationship without
// guessing which relationship kinds gate Beads dispatch or prove integration.
internal sealed record PublicationDependencyReview(bool Complete, bool Truncated,
    int ObservedIssues, int ObservedEdges, string[] MissingIssues, string[] UnknownCollections)
{
    internal static PublicationDependencyReview Inspect(string issueId,
        IReadOnlyDictionary<string, IssueSummary> issues, CancellationToken token,
        int maximumIssues = 1024, int maximumEdges = 8192)
    {
        if (maximumIssues < 1 || maximumEdges < 1) throw new ArgumentOutOfRangeException(nameof(maximumIssues));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(); pending.Enqueue(issueId);
        var discovered = new HashSet<string>(StringComparer.Ordinal) { issueId };
        var edges = 0; var truncated = false;
        while (pending.TryDequeue(out var id))
        {
            token.ThrowIfCancellationRequested();
            visited.Add(id);
            if (!issues.TryGetValue(id, out var issue)) { missing.Add(id); continue; }
            if (issue.Dependencies is not { } dependencies) { unknown.Add(id); continue; }
            foreach (var edge in dependencies)
            {
                token.ThrowIfCancellationRequested();
                if (edges == maximumEdges) { truncated = true; break; }
                edges++;
                if (discovered.Contains(edge.DependsOnId)) continue;
                if (discovered.Count == maximumIssues) { truncated = true; continue; }
                discovered.Add(edge.DependsOnId); pending.Enqueue(edge.DependsOnId);
            }
            if (truncated && edges == maximumEdges) break;
        }
        return new(!truncated && missing.Count == 0 && unknown.Count == 0, truncated,
            visited.Count, edges, missing.ToArray(), unknown.ToArray());
    }
}
