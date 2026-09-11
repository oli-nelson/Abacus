namespace Abacus;

// Invoked only by the interactive run route, after syntactic/semantic option
// validation finds missing required values. No preflight or external tools here.
internal static class RunConfigurationSelection
{
    internal static string? Select(string directory, IReadOnlyList<string> missing, TextReader input, TextWriter output)
    {
        output.WriteLine("Abacus cannot run yet. Missing required arguments:");
        foreach (var item in missing) output.WriteLine(" - " + item);
        output.WriteLine("Searching the working directory for run config JSON files...");
        var candidates = Discover(directory);
        if (candidates.Count == 0)
            throw new OptionsException("no run config files found in the working directory; supply the missing arguments or use --config <file>");
        for (var index = 0; index < candidates.Count; index++)
            output.WriteLine($" {index + 1}. {Display(Path.GetFileName(candidates[index]))}");
        output.Write("Select one config by number (blank or q cancels): ");
        output.Flush();
        var response = input.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(response) || response.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(response, out var selection) || selection < 1 || selection > candidates.Count)
            throw new OptionsException("invalid config selection; run was not started");
        return candidates[selection - 1];
    }

    internal static IReadOnlyList<string> Discover(string directory)
    {
        var candidates = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // Recognize the raw schema only: incomplete configs and configs
                    // with unavailable bases remain selectable and fail after selection.
                    _ = RunConfiguration.Load(path);
                    candidates.Add(Path.GetFullPath(path));
                }
                catch (OptionsException) { /* Not a structurally valid run config. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new OptionsException($"cannot search working directory for configs: {ex.Message}"); }
        return candidates;
    }

    private static string Display(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
