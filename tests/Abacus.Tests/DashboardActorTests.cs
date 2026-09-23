using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardActorTests
{
    [Fact]
    public async Task DashboardDefaultsToRepositoryGitUserButKeepsExplicitOverride()
    {
        var root = Directory.CreateTempSubdirectory("abacus-actor-");
        try
        {
            var runner = new CommandRunner(TextWriter.Null);
            Assert.True((await runner.RunAsync(new("git", ["init", "-q"], root.FullName), default)).Succeeded);
            Assert.True((await runner.RunAsync(new("git", ["config", "--local", "user.name", "Repository Operator"], root.FullName), default)).Succeeded);
            var defaults = DashboardOptions.Parse(null, null, null, null);
            Assert.False(defaults.ActorWasProvided);
            Assert.Equal("Repository Operator", await DashboardSession.ResolveActorAsync(runner, root.FullName, defaults, default));

            var explicitActor = DashboardOptions.Parse(null, null, "Named Operator", null);
            Assert.True(explicitActor.ActorWasProvided);
            Assert.Equal("Named Operator", await DashboardSession.ResolveActorAsync(runner, root.FullName, explicitActor, default));
        }
        finally { root.Delete(true); }
    }
}
