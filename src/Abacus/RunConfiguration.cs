using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abacus;

// A small JSON-to-CLI boundary: the existing run parser remains the authority for
// semantic validation. Drafts only need a well-formed versioned document to save.
public sealed class RunConfiguration(JsonObject document, string baseDirectory)
{
    public JsonObject Document { get; } = document;
    private string? sourcePath;
    public string BaseDirectory { get; private set; } = Path.GetFullPath(baseDirectory);
    public sealed record Field(string Name, string Option, string Kind, string Hint);
    public static IReadOnlyList<Field> Fields { get; } =
    [
        new("baseConfig", "", "path", "Optional base config path (relative to this file)"),
        new("repo", "--repo", "path", "Main checkout path (relative to this config)"),
        new("mode", "--mode", "string", "opencode, codex, claude, opencode-server; default opencode"),
        new("model", "--model", "string", "Required fallback model; OpenCode uses provider/model"),
        new("effort", "--effort", "string", "Provider-specific effort; default high"),
        new("agents", "--agent", "agents", "Named agent workspaces"),
        new("reasoningModels", "--reasoning-model", "models", "Optional high / medium / low model routes"),
        new("tmuxSession", "--tmux-session", "string", "Optional session name"),
        new("tmuxWindow", "--tmux-window", "string", "Optional window name"),
        new("tmuxLayout", "--tmux-layout", "string", "tiled, even-horizontal, even-vertical, main-horizontal, main-vertical"),
        new("disownTmuxSession", "--disown-tmux-session", "bool", "Keep an automatically created session"),
        new("opencodeServer", "--opencode-server", "string", "Server host:port for opencode-server mode"),
        new("remoteControl", "--remote-control", "bool", "Claude Remote Control"),
        new("targetFilters", "--target-filter", "list", "JSON array of target branches"),
        new("labels", "--label", "list", "JSON array of required labels"),
        new("excludeLabels", "--exclude-label", "list", "JSON array of excluded labels"),
        new("type", "--type", "string", "Beads type filter"),
        new("priority", "--priority", "int", "Priority 0 through 4"),
        new("ticketTimeout", "--ticket-timeout", "string", "Positive duration, e.g. 30s, 15m, 2h"),
        new("appendPrompt", "--append-prompt", "string", "Extra agent prompt text"),
        new("latestComments", "--latest-comments", "int", "Dashboard comments, 1 through 100; default 8"),
        new("notify", "--notify", "string", "off, attention, all"),
        new("notifySound", "--notify-sound", "bool", "Outcome notification sounds"),
        new("once", "--once", "bool", "At most one ticket per agent"),
        new("drain", "--drain", "bool", "Run until the ready queue is empty"),
        new("verbose", "--verbose", "bool", "Log output instead of dashboard"),
        new("stdio", "--stdio", "bool", "JSONL control instead of dashboard"),
        new("eventLog", "--event-log", "path", "JSONL event log path (relative to config)"),
        new("noIntro", "--no-intro", "bool", "Skip startup animation"),
        new("startPaused", "--start-paused", "bool", "Start with claims paused"),
    ];

    public static RunConfiguration Create(string directory) => new(new JsonObject { ["version"] = 1 }, directory);

    public static RunConfiguration Load(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using var parsed = JsonDocument.Parse(text);
            CheckDuplicateKeys(parsed.RootElement);
            var document = JsonNode.Parse(text) as JsonObject
                ?? throw new OptionsException("run config must be a JSON object");
            var config = new RunConfiguration(document, Path.GetDirectoryName(Path.GetFullPath(path))!);
            config.sourcePath = Path.GetFullPath(path);
            config.ValidateShape();
            return config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new OptionsException($"cannot read run config '{path}': {ex.Message}");
        }
    }

    // Resolve the single-parent chain without flattening the editable document.
    // A depth limit also catches cycles through directory symlink aliases.
    internal RunConfiguration ResolveInheritance()
    {
        ValidateShape();
        var layers = new List<RunConfiguration> { this };
        var visited = new HashSet<string>(OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (sourcePath is not null) visited.Add(sourcePath);
        var current = this;
        while (current.Document["baseConfig"] is { } value)
        {
            var reference = value.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reference)) throw new OptionsException("baseConfig must be a nonempty file path or null");
            var path = current.ResolvePath(reference);
            if (!visited.Add(path)) throw new OptionsException($"baseConfig inheritance cycle at '{path}'");
            if (layers.Count >= 64) throw new OptionsException("baseConfig inheritance exceeds 64 files (possible cycle)");
            current = Load(path);
            layers.Add(current);
        }
        var merged = Create(BaseDirectory);
        foreach (var layer in layers.AsEnumerable().Reverse())
        {
            var document = layer.RebasedDocument(BaseDirectory);
            if (document.ContainsKey("once") || document.ContainsKey("drain"))
            {
                merged.Document.Remove("once");
                merged.Document.Remove("drain");
            }
            foreach (var (name, value) in document)
            {
                if (name == "baseConfig") continue;
                if (name == "reasoningModels" && value is JsonObject models
                    && merged.Document[name] is JsonObject previous)
                {
                    foreach (var (tier, model) in models) previous[tier] = model?.DeepClone();
                }
                else merged.Document[name] = value?.DeepClone();
            }
        }
        return merged;
    }

    private static void CheckDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new OptionsException($"duplicate run config property '{property.Name}'");
                CheckDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateKeys(item);
    }

    public void ValidateShape()
    {
        if (Document["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1)
            throw new OptionsException("run config requires version: 1");
        foreach (var (name, value) in Document)
        {
            if (name == "version") continue;
            var field = Fields.FirstOrDefault(f => f.Name == name)
                ?? throw new OptionsException($"unknown run config property '{name}'");
            if (value is null) continue; // null means unset, including in incomplete drafts
            bool IsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out _);
            var valid = field.Kind switch
            {
                "bool" => value is JsonValue b && b.TryGetValue<bool>(out _),
                "int" => value is JsonValue n && n.TryGetValue<int>(out _),
                "list" => value is JsonArray list && list.All(IsString),
                "agents" => value is JsonArray agents && agents.All(a => a is JsonObject obj
                    && obj.All(p => (p.Key is "name" or "workspace") && (p.Value is null || IsString(p.Value)))),
                "models" => value is JsonObject models && models.All(p => (p.Key is "high" or "medium" or "low")
                    && (p.Value is null || IsString(p.Value))),
                _ => IsString(value),
            };
            if (!valid) throw new OptionsException($"run config '{name}' must have type {field.Kind}");
        }
    }

    internal List<string> Arguments(string command, IReadOnlySet<string>? overridden = null,
        IReadOnlySet<string>? overriddenTiers = null)
    {
        ValidateShape();
        var args = new List<string>();
        foreach (var field in Fields)
        {
            if (field.Name == "baseConfig") continue;
            if (overridden?.Contains(field.Option) == true && field.Kind != "models") continue;
            if (field.Option is "--once" or "--drain" &&
                (overridden?.Contains("--once") == true || overridden?.Contains("--drain") == true)) continue;
            // Preflight can inspect a saved run without inheriting its output/lifecycle controls.
            if (command == "preflight" && Options.OptionArity(command, field.Option) < 0) continue;
            var value = Document[field.Name];
            if (value is null) continue;
            string Text(JsonNode node) => node.GetValue<string>();
            switch (field.Kind)
            {
                case "bool":
                    if (value.GetValue<bool>()) args.Add(field.Option);
                    break;
                case "agents":
                    foreach (var agent in (JsonArray)value)
                    {
                        args.AddRange([field.Option, agent!["name"]?.GetValue<string>() ?? "",
                            ResolvePath(agent["workspace"]?.GetValue<string>() ?? "")]);
                    }
                    break;
                case "models":
                    foreach (var (tier, model) in (JsonObject)value)
                        if (model is not null && overriddenTiers?.Contains(tier) != true)
                            args.AddRange([field.Option, tier, Text(model)]);
                    break;
                case "list":
                    foreach (var item in (JsonArray)value) args.AddRange([field.Option, Text(item!)]);
                    break;
                case "int": args.AddRange([field.Option, value.ToJsonString()]); break;
                case "path": args.AddRange([field.Option, ResolvePath(Text(value))]); break;
                default: args.AddRange([field.Option, Text(value)]); break;
            }
        }
        return args;
    }

    private string ResolvePath(string path) => string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path, BaseDirectory);

    public IReadOnlyList<string> Warnings()
    {
        try
        {
            var effective = ResolveInheritance();
            Options.Parse(["run", .. effective.Arguments("run")]);
            return [];
        }
        catch (OptionsException ex) when (ex.Missing is not null) { return ex.Missing; }
        catch (Exception ex) when (ex is OptionsException or ArgumentException or NotSupportedException)
        { return [ex.Message]; }
    }

    // Rebase relative paths on Save As so moving a draft does not redirect its workspaces.
    public JsonObject DocumentFor(string outputPath) =>
        RebasedDocument(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

    private JsonObject RebasedDocument(string directory)
    {
        var copy = (JsonObject)Document.DeepClone();
        string Rebase(string path) => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            ? path : Path.GetRelativePath(directory, ResolvePath(path));
        foreach (var name in new[] { "baseConfig", "repo", "eventLog" })
            if (copy[name] is JsonValue value) copy[name] = Rebase(value.GetValue<string>());
        if (copy["agents"] is JsonArray agents)
            foreach (var agent in agents)
                if (agent?["workspace"] is JsonValue value) agent["workspace"] = Rebase(value.GetValue<string>());
        return copy;
    }

    public void Save(string path, bool overwrite)
    {
        ValidateShape();
        path = Path.GetFullPath(path);
        var copy = DocumentFor(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".abacus-config-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(temporary, path, overwrite);
            Document.Clear();
            foreach (var (key, value) in copy) Document[key] = value?.DeepClone();
            BaseDirectory = Path.GetDirectoryName(path)!;
            sourcePath = path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
