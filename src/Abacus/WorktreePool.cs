using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus;

/// <summary>A repository-local manifest, external checkouts, and one OS-held controller lease.
/// No background service, Git API, automatic deletion, or adoption of user worktrees.</summary>
public sealed partial class WorktreePool(CommandRunner runner, string repository, string commonDirectory,
    string gitExecutable = "git", string? dataDirectory = null)
{
    public sealed record Slot(string Name, string Path);
    public sealed record Manifest(int Version, string RepositoryId, string CommonDirectory, string Root, List<Slot> Slots);
    public sealed record SlotStatus(string Name, string Path, string State, string Detail, long? Bytes);
    private readonly Git git = new(runner, gitExecutable);
    private string MetadataDirectory => Path.Combine(commonDirectory, "abacus");
    private string ManifestPath => Path.Combine(MetadataDirectory, "pool.json");
    private FileStream? lease;

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "abacus", "worktrees");

    public static async Task<WorktreePool> OpenAsync(CommandRunner runner, string repository,
        CancellationToken token, string gitExecutable = "git", string? dataDirectory = null)
    {
        var result = await runner.RunAsync(new CommandSpec(gitExecutable,
            ["-C", repository, "rev-parse", "--path-format=absolute", "--git-common-dir"], repository), token);
        if (!result.Succeeded || !Path.IsPathFullyQualified(result.StandardOutput.Trim()))
            throw new WorkspacePreparationException("could not resolve the shared Git directory for the worktree pool");
        return new(runner, repository, Path.GetFullPath(result.StandardOutput.Trim()), gitExecutable, dataDirectory);
    }

    // Held through the entire controller run, so maintenance cannot race allocation or live agents.
    public IDisposable AcquireLease()
    {
        Directory.CreateDirectory(MetadataDirectory);
        if (lease is not null) throw new InvalidOperationException("pool lease already held");
        try
        {
            lease = new FileStream(Path.Combine(MetadataDirectory, "pool.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new Lease(this);
        }
        catch (IOException ex)
        {
            throw new StartupInvariantException($"worktree pool is in use by another controller or maintenance command: {ex.Message}");
        }
    }

    private sealed class Lease(WorktreePool pool) : IDisposable
    {
        public void Dispose() { pool.lease?.Dispose(); pool.lease = null; }
    }

    public Manifest? ReadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath))
                ?? throw new JsonException("empty manifest");
            if (manifest.Version != 1 || !Guid.TryParseExact(manifest.RepositoryId, "N", out _)
                || manifest.CommonDirectory != commonDirectory || !Path.IsPathFullyQualified(manifest.Root)
                || manifest.Slots is null || manifest.Slots.Any(s => s is null || string.IsNullOrEmpty(s.Name))
                || manifest.Slots.Select(s => s.Name).Distinct().Count() != manifest.Slots.Count)
                throw new JsonException("invalid or relocated pool manifest; inspect before reuse");
            foreach (var slot in manifest.Slots)
                if (!IsSlotName(slot.Name) || slot.Path != Path.Combine(manifest.Root, slot.Name))
                    throw new JsonException("invalid pool slot path");
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            throw new WorkspacePreparationException($"cannot read worktree pool: {ex.Message}");
        }
    }

    public static bool IsSlotName(string name)
    {
        var prefix = name.StartsWith("slot-", StringComparison.Ordinal) ? "slot-" : "agent-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name[prefix.Length..], out var number) && number > 0 && name == $"{prefix}{number}";
    }

    private Manifest CreateManifest()
    {
        RequireLease();
        var id = Guid.NewGuid().ToString("N");
        // The path key prevents a copied .git directory from sharing another clone's pool.
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(commonDirectory)))[..16];
        var root = Path.Combine(dataDirectory ?? DefaultDataDirectory, $"{key}-{id}");
        var manifest = new Manifest(1, id, commonDirectory, root, []);
        Save(manifest);
        return manifest;
    }

    private void Save(Manifest manifest)
    {
        RequireLease();
        var temporary = ManifestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temporary, ManifestPath, overwrite: true);
    }

    private void RequireLease()
    {
        if (lease is null) throw new InvalidOperationException("pool mutation requires its controller lease");
    }

    public async Task<IReadOnlyList<AgentOptions>> EnsureSlotsAsync(int count, string startingCommit, CancellationToken token)
    {
        RequireLease();
        if (count < 1 || !Git.IsCommitId(startingCommit)) throw new ArgumentException("invalid pool allocation");
        var manifest = ReadManifest() ?? CreateManifest();
        while (manifest.Slots.Count < count)
        {
            var number = 1;
            while (manifest.Slots.Any(s => s.Name == $"slot-{number}")) number++;
            var name = $"slot-{number}";
            var slot = new Slot(name, Path.Combine(manifest.Root, name));
            if (Directory.Exists(slot.Path) || File.Exists(slot.Path))
                throw new WorkspacePreparationException($"unregistered pool path '{slot.Path}' exists; preserved for review");
            manifest.Slots.Add(slot);
            Save(manifest);
            Directory.CreateDirectory(manifest.Root);
            await RequiredAsync(repository, ["worktree", "add", "--detach", slot.Path, startingCommit], token);
        }
        var result = new List<AgentOptions>();
        foreach (var slot in manifest.Slots.Take(count))
        {
            await VerifySlotAsync(slot, token);
            result.Add(new(slot.Name, slot.Path));
        }
        return result;
    }

    public async Task VerifySlotAsync(Slot slot, CancellationToken token)
    {
        if (!Directory.Exists(slot.Path))
            throw new WorkspacePreparationException($"pool slot '{slot.Name}' is missing; run abacus worktrees prune before retrying");
        if (new DirectoryInfo(slot.Path).LinkTarget is not null)
            throw new WorkspacePreparationException($"pool slot '{slot.Name}' is a symlink; preserved for review");
        var root = await git.ResolveWorkspaceRootAsync(slot.Path, slot.Name, token);
        var result = await RequiredAsync(slot.Path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], token);
        if (root != slot.Path || Path.GetFullPath(result.StandardOutput.Trim()) != commonDirectory)
            throw new WorkspacePreparationException($"pool slot '{slot.Name}' does not belong to this repository; preserved for review");
    }

    public async Task<IReadOnlyList<SlotStatus>> InspectAsync(CancellationToken token)
    {
        var manifest = ReadManifest();
        if (manifest is null) return [];
        var statuses = new List<SlotStatus>();
        var inUse = IsInUse();
        foreach (var slot in manifest.Slots)
        {
            try
            {
                await VerifySlotAsync(slot, token);
                var status = await git.GetWorkspaceStatusAsync(slot.Path, slot.Name, token);
                var assignment = ReadAssignment(slot.Name);
                var detail = assignment is null ? status.Branch
                    : $"{status.Branch}; {assignment.Phase}; issue={assignment.IssueId ?? "none"}; last agent={assignment.AgentName}; assignment={assignment.Id}; {assignment.Location}";
                statuses.Add(new(slot.Name, slot.Path, inUse ? "in-use" : assignment?.Phase == "execution-uncertain" || status.IsDirty ? "needs-review"
                        : assignment?.IssueId is not null || Git.TryGetIssueId(status.Branch, out _) ? "reserved" : "available",
                    detail, DirectoryBytes(slot.Path)));
            }
            catch (Exception ex) when (ex is WorkspacePreparationException or PreflightException or IOException)
            {
                statuses.Add(new(slot.Name, slot.Path, "needs-review", ex.Message, null));
            }
        }
        return statuses;
    }

    private bool IsInUse()
    {
        var path = Path.Combine(MetadataDirectory, "pool.lock");
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    // Avoid following directory symlinks while measuring retained caches.
    private static long DirectoryBytes(string path) => new DirectoryInfo(path)
        .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
        .Sum(file => file.Length);

    public Task ReclaimAsync(string name, TargetRegistry targets, Beads beads, CancellationToken token) =>
        MaintainAsync(name, targets, beads, remove: false, token);

    public Task RemoveAsync(string name, TargetRegistry targets, Beads beads, CancellationToken token) =>
        MaintainAsync(name, targets, beads, remove: true, token);

    private async Task MaintainAsync(string name, TargetRegistry targets, Beads beads, bool remove, CancellationToken token)
    {
        RequireLease();
        var manifest = ReadManifest() ?? throw new WorkspacePreparationException("no worktree pool exists");
        var slot = manifest.Slots.SingleOrDefault(s => s.Name == name)
            ?? throw new WorkspacePreparationException($"unknown pool slot '{name}'");
        if (ReadAssignment(slot.Name)?.Phase == "execution-uncertain")
            throw new WorkspacePreparationException($"{slot.Name}: previous execution may still be alive; stop it and use worktrees recover --confirm first");
        await VerifySlotAsync(slot, token);
        using var ownership = await WorkspaceOwnership.AcquireAsync(git,
            [new ValidatedAgent(slot.Name, slot.Path, new DoltIdentity(false, "", null, null, false), false)], token);
        var status = await git.GetWorkspaceStatusAsync(slot.Path, slot.Name, token);
        if (status.IsDirty) throw new WorkspacePreparationException($"'{name}' has local changes; preserved for review");
        var recordedIssue = ReadAssignment(slot.Name)?.IssueId;
        if (recordedIssue is not null)
        {
            var recorded = await beads.GetIssueAsync(repository, "abacus", recordedIssue, token);
            if (recorded?.Status != IssueStatus.Closed)
                throw new WorkspacePreparationException($"{name}: assignment retains unfinished issue {recordedIssue}; preserved");
        }
        if (Git.TryGetIssueId(status.Branch, out var issueId))
        {
            var issue = await beads.GetIssueAsync(repository, "abacus", issueId, token);
            if (issue?.Status != IssueStatus.Closed)
                throw new WorkspacePreparationException($"'{name}' retains unfinished or unknown ticket '{issueId}'; preserved");
        }
        var head = (await RequiredAsync(slot.Path, ["rev-parse", "HEAD"], token)).StandardOutput.Trim();
        var merged = false;
        foreach (var branch in targets.Targets.Keys)
        {
            var ancestor = await runner.RunAsync(new CommandSpec(gitExecutable,
                ["-C", repository, "merge-base", "--is-ancestor", head, $"refs/heads/{branch}"], repository), token);
            if (ancestor.Succeeded) { merged = true; break; }
            if (ancestor.ExitCode != 1) throw new WorkspacePreparationException("could not verify pool work is merged");
        }
        if (!merged) throw new WorkspacePreparationException($"'{name}' has commits not merged into a configured target; preserved");
        // No forced checkout: Git refuses obstructions. Keep caches, branches, and all commits.
        await RequiredAsync(slot.Path, ["switch", "--detach", "--no-overwrite-ignore", head], token);
        DeleteAssignment(slot.Name);
        if (remove)
        {
            // Keep workspace ownership until removal completes; no --force, even with confirmation.
            await RequiredAsync(repository, ["worktree", "remove", slot.Path], token);
            manifest.Slots.Remove(slot);
            Save(manifest);
        }
    }

    public async Task<IReadOnlyList<string>> PruneAsync(CancellationToken token)
    {
        RequireLease();
        var manifest = ReadManifest();
        if (manifest is null) return [];
        var removed = new List<string>();
        // Only forget missing Abacus slots. Do not run global git worktree prune against user worktrees.
        var registered = (await RequiredAsync(repository, ["worktree", "list", "--porcelain", "-z"], token)).StandardOutput;
        foreach (var slot in manifest.Slots.ToArray())
        {
            if (Directory.Exists(slot.Path) || File.Exists(slot.Path)) continue;
            if (registered.Split('\0').Contains("worktree " + slot.Path, StringComparer.Ordinal))
                throw new WorkspacePreparationException($"missing slot '{slot.Name}' still has Git metadata; use git worktree repair if moved, or explicitly remove its stale registration after review");
            if (ReadAssignment(slot.Name) is not null)
                throw new WorkspacePreparationException($"{slot.Name}: missing checkout retains an assignment; review before pruning");
            manifest.Slots.Remove(slot);
            removed.Add(slot.Name);
        }
        Save(manifest);
        return removed;
    }

    private async Task<CommandResult> RequiredAsync(string workspace, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var result = await runner.RunAsync(new CommandSpec(gitExecutable, ["-C", workspace, .. arguments], workspace), token);
        if (!result.Succeeded) throw new WorkspacePreparationException($"pool Git operation failed: {result.StandardError.Trim()}");
        return result;
    }
}
