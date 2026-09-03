using Abacus;

namespace Abacus.Tests;

public sealed class SkillInstallerTests
{
    [Fact]
    public async Task InstallsAllBundledSkillsAndRequiresConfirmationToReplaceThem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-skills-");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "nested"));
            var fakeGit = Path.Combine(root.FullName, "git");
            await File.WriteAllTextAsync(fakeGit, $"#!/bin/sh\nprintf '%s\\n' '{root.FullName}'\n");
            File.SetUnixFileMode(
                fakeGit,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var installer = new SkillInstaller(new CommandRunner(TextWriter.Null), fakeGit);

            var confirmationCalls = 0;
            var installed = await installer.InstallAsync(
                nested.FullName,
                _ =>
                {
                    confirmationCalls++;
                    return false;
                },
                CancellationToken.None);

            Assert.False(installed.Cancelled);
            Assert.Equal(0, confirmationCalls);
            Assert.Equal(
                [
                    "abacus-beads-planner",
                    "abacus-beads-doctor",
                    "abacus-beads-attention",
                    "abacus-git-check",
                ],
                installed.InstalledSkills);
            var installedRoot = installed.SkillsRoot;
            Assert.Equal(Path.Combine(root.FullName, ".agents", "skills"), installedRoot);
            var planner = Path.Combine(installedRoot, "abacus-beads-planner", "SKILL.md");
            var doctor = Path.Combine(installedRoot, "abacus-beads-doctor", "SKILL.md");
            var attention = Path.Combine(installedRoot, "abacus-beads-attention", "SKILL.md");
            var gitCheck = Path.Combine(installedRoot, "abacus-git-check", "SKILL.md");
            Assert.Contains("name: abacus-beads-planner", await File.ReadAllTextAsync(planner));
            Assert.Contains("name: abacus-beads-doctor", await File.ReadAllTextAsync(doctor));
            Assert.Contains("name: abacus-beads-attention", await File.ReadAllTextAsync(attention));
            Assert.Contains("name: abacus-git-check", await File.ReadAllTextAsync(gitCheck));
            Assert.True(File.Exists(Path.Combine(installedRoot, "abacus-beads-planner", "agents", "openai.yaml")));
            Assert.True(File.Exists(Path.Combine(installedRoot, "abacus-beads-doctor", "agents", "openai.yaml")));
            Assert.True(File.Exists(Path.Combine(installedRoot, "abacus-beads-attention", "agents", "openai.yaml")));
            Assert.True(File.Exists(Path.Combine(installedRoot, "abacus-git-check", "agents", "openai.yaml")));

            await File.WriteAllTextAsync(planner, "stale");
            var obsolete = Path.Combine(installedRoot, "abacus-beads-planner", "obsolete.txt");
            await File.WriteAllTextAsync(obsolete, "remove me");

            IReadOnlyList<string>? requestedSkills = null;
            var cancelled = await installer.InstallAsync(
                nested.FullName,
                skills =>
                {
                    requestedSkills = skills;
                    return false;
                },
                CancellationToken.None);

            Assert.True(cancelled.Cancelled);
            Assert.Equal("stale", await File.ReadAllTextAsync(planner));
            Assert.True(File.Exists(obsolete));

            var replaced = await installer.InstallAsync(
                nested.FullName,
                _ => true,
                CancellationToken.None);

            Assert.False(replaced.Cancelled);
            Assert.Equal(
                [
                    "abacus-beads-planner",
                    "abacus-beads-doctor",
                    "abacus-beads-attention",
                    "abacus-git-check",
                ],
                requestedSkills);
            Assert.Contains("name: abacus-beads-planner", await File.ReadAllTextAsync(planner));
            Assert.False(File.Exists(obsolete));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailsClearlyOutsideAGitRepository()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-skills-no-git-");
        try
        {
            var fakeGit = Path.Combine(root.FullName, "git");
            await File.WriteAllTextAsync(fakeGit, "#!/bin/sh\nprintf 'not a repository\\n' >&2\nexit 128\n");
            File.SetUnixFileMode(
                fakeGit,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var installer = new SkillInstaller(new CommandRunner(TextWriter.Null), fakeGit);

            var exception = await Assert.ThrowsAsync<SkillInstallationException>(() =>
                installer.InstallAsync(root.FullName, _ => true, CancellationToken.None));

            Assert.Contains("could not find the Git repository root", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
