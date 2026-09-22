namespace Abacus;

public sealed partial class Beads
{
    // Delta flags preserve labels added by other writers. Never replace the set.
    internal static IReadOnlyList<string> LabelDeltaArguments(IReadOnlyList<string>? add, IReadOnlyList<string>? remove)
    {
        static HashSet<string> Validate(IReadOnlyList<string>? labels)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (labels is null) return result;
            if (labels.Count > 32) throw new ArgumentException("At most 32 label additions/removals per operation.");
            foreach (var label in labels)
            {
                // bd's string-slice flags parse CSV: forbid separators/quoting so
                // a benign-looking label cannot expand into a reserved label.
                if (string.IsNullOrWhiteSpace(label) || label.Length > 100 || label != label.Trim() ||
                    label.Any(char.IsControl) || label.Contains(',') || label.Contains('"') ||
                    label.StartsWith("abacus:", StringComparison.OrdinalIgnoreCase) || label.StartsWith("gt:", StringComparison.OrdinalIgnoreCase) ||
                    !result.Add(label)) throw new ArgumentException("Invalid, duplicate or reserved label.");
            }
            return result;
        }
        var additions = Validate(add); var removals = Validate(remove);
        if (additions.Overlaps(removals)) throw new ArgumentException("Cannot add and remove the same label.");
        return additions.Select(label => "--add-label=" + label)
            .Concat(removals.Select(label => "--remove-label=" + label)).ToArray();
    }
}
