using Abacus;

namespace Abacus.Tests;

public sealed class CommandRunnerTests
{
    [Fact]
    public async Task PassesLiteralArgumentsAndScopesActorToChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("abacus-runner-");
        try
        {
            var script = Path.Combine(directory.FullName, "fake command");
            await File.WriteAllTextAsync(script, """
                #!/bin/sh
                for value in "$@"; do
                  printf 'arg=<%s>\n' "$value"
                done
                printf 'actor=<%s>\n' "$BEADS_ACTOR"
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var parentActor = Environment.GetEnvironmentVariable("BEADS_ACTOR");
            var log = new StringWriter();
            var runner = new CommandRunner(log);
            var result = await runner.RunAsync(new CommandSpec(
                script,
                ["space value", "$HOME; touch nope", "quote\"value", "plain"],
                directory.FullName,
                new Dictionary<string, string?> { ["BEADS_ACTOR"] = "alice" },
                "alice"));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                "arg=<space value>\n" +
                "arg=<$HOME; touch nope>\n" +
                "arg=<quote\"value>\n" +
                "arg=<plain>\n" +
                "actor=<alice>\n",
                result.StandardOutput);
            Assert.Equal(parentActor, Environment.GetEnvironmentVariable("BEADS_ACTOR"));
            Assert.Contains("[alice]", log.ToString());
            Assert.False(File.Exists(Path.Combine(directory.FullName, "nope")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancellationKillsTheChildProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("abacus-cancel-");
        try
        {
            var script = Path.Combine(directory.FullName, "wait-forever");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nwhile :; do sleep 1; done\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var runner = new CommandRunner(TextWriter.Null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
                new CommandSpec(script, [], directory.FullName), cancellation.Token));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
