using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed partial class DashboardGit
{
    private long worktreeHashBytes, worktreeHashTicks;
    internal long WorktreeHashBytes => Interlocked.Read(ref worktreeHashBytes);
    internal TimeSpan WorktreeHashTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref worktreeHashTicks) / Stopwatch.Frequency);

    private async Task<string> WorktreeFingerprintAsync(WorktreeFact tree, string status, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Worktree fingerprinting requires Unix file modes.");
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Porcelain v2 includes index blob IDs/modes, including conflict stages.
            // Working bytes need their own hashes; names/dirty flags/mtimes are not identity.
            var paths = ChangedWorktreePaths(status);
            var facts = new List<object>();
            var regular = new List<string>();
            long bytes = 0;
            foreach (var path in paths)
            {
                var parts = path.Split('/');
                if (Path.IsPathRooted(path) || parts.Any(p => p is "" or "." or "..")) throw new InvalidDataException("Invalid worktree content path.");
                var parent = tree.Path;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    parent = Path.Combine(parent, parts[i]);
                    if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                        throw new InvalidDataException("Worktree content parent is a symbolic link.");
                }
                var full = Path.Combine(tree.Path, path);
                var file = new FileInfo(full);
                var link = file.LinkTarget;
                if (link is not null)
                {
                    facts.Add(new { path, kind = "symlink", link });
                    bytes += Encoding.UTF8.GetByteCount(link);
                }
                else if (Directory.Exists(full)) throw new InvalidDataException("Nested repositories/submodules require separate review.");
                else if (!file.Exists) facts.Add(new { path, kind = "missing" });
                else
                {
                    bytes += file.Length;
                    if (bytes > 64 * 1024 * 1024) throw new InvalidDataException("Changed worktree content exceeds fingerprint byte limit.");
                    regular.Add("./" + path);
                    facts.Add(new { path, kind = "file", mode = (int)File.GetUnixFileMode(full) });
                }
            }
            var hashes = new StringBuilder();
            foreach (var batch in regular.Chunk(64))
            {
                var output = await RequiredAsync(["-C", tree.Path, "hash-object", "--no-filters", "--", .. batch], token);
                var ids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length != batch.Length || ids.Any(id => !Git.IsCommitId(id))) throw new InvalidDataException("Invalid worktree content hashes.");
                hashes.Append(output);
            }
            Interlocked.Add(ref worktreeHashBytes, bytes);
            var attributes = new StringBuilder();
            foreach (var batch in paths.Chunk(64))
            {
                var output = await RequiredAsync(["-C", tree.Path, "check-attr", "--all", "-z", "--", .. batch], token);
                var values = output.Split('\0');
                if ((values.Length - 1) % 3 != 0 || values[^1] != "") throw new InvalidDataException("Invalid worktree attribute facts.");
                for (var i = 0; i < values.Length - 1; i += 3)
                    if (values[i + 1] == "filter" && values[i + 2] is not ("" or "unset" or "unspecified"))
                        throw new InvalidDataException("External clean/process filters have unbounded external inputs; content identity is unavailable.");
                attributes.Append(output);
                if (attributes.Length > OutputLimit) throw new InvalidDataException("Worktree attributes exceed limit.");
            }
            // Config/attribute changes may alter a patch despite unchanged file bytes.
            // This input is hashed internally, never returned as raw diagnostics/config.
            var config = await RequiredAsync(["-C", tree.Path, "config", "--null", "--list"], token);
            var canonical = JsonSerializer.Serialize(new { tree.Head, status, facts, hashes = hashes.ToString(), attributes = attributes.ToString(), config });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
        finally { Interlocked.Add(ref worktreeHashTicks, Stopwatch.GetTimestamp() - started); }
    }

    internal static string[] ChangedWorktreePaths(string status)
    {
        var fields = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var parts = field.Split(' ', field.StartsWith("u ", StringComparison.Ordinal) ? 11 : field.StartsWith("2 ", StringComparison.Ordinal) ? 10 : field.StartsWith("1 ", StringComparison.Ordinal) ? 9 : 2);
            if (parts[0] is "1" or "2" or "u")
            {
                var expected = parts[0] == "u" ? 11 : parts[0] == "2" ? 10 : 9;
                if (parts.Length != expected) throw new InvalidDataException("Invalid changed path record.");
                paths.Add(parts[^1]);
                if (parts[0] == "2")
                {
                    if (index + 1 >= fields.Length) throw new InvalidDataException("Incomplete rename record.");
                    paths.Add(fields[++index]);
                }
            }
            else if (parts[0] == "?" && parts.Length == 2) paths.Add(parts[1]);
            else throw new InvalidDataException("Unsupported worktree status record.");
            if (paths.Count > 1024) throw new InvalidDataException("Changed worktree path limit exceeded.");
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
}
