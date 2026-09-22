using System.Runtime.InteropServices;
using System.Text.Json;

namespace Abacus.Dashboard;

internal static class DashboardApplication
{
    public static async Task<int> RunAsync(string repository, DashboardOptions options)
    {
        using var lifetime = new CancellationTokenSource();
        var interrupted = false;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; interrupted = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.Cancel(); });
        try
        {
            await using var session = await DashboardSession.StartAsync(repository, options, Console.Error, lifetime.Token);
            try { await session.Completion.WaitAsync(lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            return interrupted ? 130 : 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return interrupted ? 130 : 0; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task ValidateRepositoryAsync(CommandRunner runner, string repository, CancellationToken token)
    {
        async Task<string> Read(string executable, string[] arguments, string cwd)
        {
            var result = await runner.RunAsync(new CommandSpec(executable, arguments, cwd, MaxOutputCharacters: 64 * 1024), token);
            if (!result.Succeeded) throw new InvalidOperationException($"Dashboard startup could not validate {executable}; check repository setup and CLI availability.");
            return result.StandardOutput;
        }
        var where = await Read("bd", ["--readonly", "where", "--json"], repository);
        string origin;
        try
        {
            using var document = JsonDocument.Parse(where);
            var root = document.RootElement;
            var path = root.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)) throw new JsonException();
            origin = root.TryGetProperty("redirected_from", out var redirect) && !string.IsNullOrWhiteSpace(redirect.GetString()) ? redirect.GetString()! : path;
            if (!Path.IsPathRooted(origin) || !Directory.Exists(origin)) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidOperationException("Dashboard requires an initialized Beads project belonging to this Git repository."); }
        string[] args = ["rev-parse", "--path-format=absolute", "--git-common-dir"];
        var mainGit = (await Read("git", args, repository)).Trim();
        var beadsGit = (await Read("git", args, origin)).Trim();
        if (mainGit != beadsGit) throw new InvalidOperationException("The active Beads project belongs to a different Git repository.");
        // Capability probes are read-only. Actual returned shapes are checked by collectors.
        var exportHelp = await Read("bd", ["export", "--help"], repository);
        if (!exportHelp.Contains("comments", StringComparison.OrdinalIgnoreCase) || !exportHelp.Contains("dependencies", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installed Beads does not advertise the required issue export capability.");
        var historyHelp = await Read("bd", ["history", "--help"], repository);
        if (!historyHelp.Contains("--limit", StringComparison.Ordinal) || !historyHelp.Contains("--json", StringComparison.Ordinal))
            throw new InvalidOperationException("Installed Beads does not advertise bounded JSON issue history.");
        var commentHelp = await Read("bd", ["comment", "--help"], repository);
        var updateHelp = await Read("bd", ["update", "--help"], repository);
        if (!commentHelp.Contains("bd comment", StringComparison.Ordinal) ||
            new[] { "--title", "--description", "--priority", "--append-notes", "--add-label", "--remove-label" }.Any(flag => !updateHelp.Contains(flag, StringComparison.Ordinal)))
            throw new InvalidOperationException("Installed Beads does not advertise the required comment/content-edit capabilities.");
    }
}
