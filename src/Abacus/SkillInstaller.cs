using System.Reflection;

namespace Abacus;

public sealed record SkillInstallationResult(
    string SkillsRoot,
    IReadOnlyList<string> InstalledSkills,
    bool Cancelled);

public sealed class SkillInstaller(
    CommandRunner runner,
    string gitExecutable = "git",
    Assembly? resourceAssembly = null)
{
    public static IReadOnlyList<string> InstallableSkillNames { get; } =
    [
        "abacus-beads-planner",
        "abacus-beads-doctor",
        "abacus-beads-attention",
        "abacus-git-check",
    ];

    private sealed record BundledSkill(
        string Name,
        IReadOnlyList<(string ResourceName, string RelativePath)> Files);

    private static readonly BundledSkill[] BundledSkills =
    [
        new(InstallableSkillNames[0],
        [
            ("Abacus.Skills.abacus-beads-planner.SKILL.md", "SKILL.md"),
            ("Abacus.Skills.abacus-beads-planner.agents.openai.yaml", "agents/openai.yaml"),
        ]),
        new(InstallableSkillNames[1],
        [
            ("Abacus.Skills.abacus-beads-doctor.SKILL.md", "SKILL.md"),
            ("Abacus.Skills.abacus-beads-doctor.agents.openai.yaml", "agents/openai.yaml"),
        ]),
        new(InstallableSkillNames[2],
        [
            ("Abacus.Skills.abacus-beads-attention.SKILL.md", "SKILL.md"),
            ("Abacus.Skills.abacus-beads-attention.agents.openai.yaml", "agents/openai.yaml"),
        ]),
        new(InstallableSkillNames[3],
        [
            ("Abacus.Skills.abacus-git-check.SKILL.md", "SKILL.md"),
            ("Abacus.Skills.abacus-git-check.agents.openai.yaml", "agents/openai.yaml"),
        ]),
    ];

    private readonly Assembly assembly = resourceAssembly ?? typeof(SkillInstaller).Assembly;

    public async Task<SkillInstallationResult> InstallAsync(
        string workingDirectory,
        Func<IReadOnlyList<string>, bool> confirmOverwrite,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(confirmOverwrite);

        var rootResult = await runner.RunAsync(
            new CommandSpec(
                gitExecutable,
                ["-C", workingDirectory, "rev-parse", "--show-toplevel"],
                workingDirectory),
            cancellationToken);
        if (!rootResult.Succeeded || string.IsNullOrWhiteSpace(rootResult.StandardOutput))
        {
            var detail = string.IsNullOrWhiteSpace(rootResult.StandardError)
                ? $"exit code {rootResult.ExitCode}"
                : rootResult.StandardError.Trim();
            throw new SkillInstallationException(
                $"could not find the Git repository root from '{workingDirectory}': {detail}");
        }

        var repositoryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootResult.StandardOutput.Trim()));
        var skillsRoot = Path.Combine(repositoryRoot, ".agents", "skills");
        var existingSkills = BundledSkills
            .Where(skill => PathExists(Path.Combine(skillsRoot, skill.Name)))
            .Select(static skill => skill.Name)
            .ToArray();
        var confirmedReplacements = existingSkills.ToHashSet(StringComparer.Ordinal);

        if (existingSkills.Length > 0 && !confirmOverwrite(existingSkills))
        {
            return new SkillInstallationResult(skillsRoot, [], Cancelled: true);
        }

        Directory.CreateDirectory(skillsRoot);
        var stagingRoot = Path.Combine(skillsRoot, $".abacus-install-skills-{Guid.NewGuid():N}");
        try
        {
            foreach (var skill in BundledSkills)
            {
                foreach (var (resourceName, relativePath) in skill.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(
                        stagingRoot,
                        skill.Name,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    await using var source = assembly.GetManifestResourceStream(resourceName)
                        ?? throw new SkillInstallationException(
                            $"bundled skill resource '{resourceName}' is missing");
                    await using var target = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None);
                    await source.CopyToAsync(target, cancellationToken);
                }
            }

            foreach (var skill in BundledSkills)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(skillsRoot, skill.Name);
                if (confirmedReplacements.Contains(skill.Name))
                {
                    DeletePath(destination);
                }

                Directory.Move(Path.Combine(stagingRoot, skill.Name), destination);
            }
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }

        return new SkillInstallationResult(
            skillsRoot,
            BundledSkills.Select(static skill => skill.Name).ToArray(),
            Cancelled: false);
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed class SkillInstallationException(string message) : Exception(message);
