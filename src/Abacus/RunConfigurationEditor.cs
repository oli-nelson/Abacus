using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abacus;

internal static class RunConfigurationEditor
{
    public static int Run(string? input, string? output)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected
            || Environment.GetEnvironmentVariable("TERM") == "dumb")
            throw new OptionsException("config edit requires an interactive terminal; edit the JSON file directly instead");
        var source = input is null ? null : Path.GetFullPath(input);
        var destination = output is null ? source : Path.GetFullPath(output);
        var config = source is null
            ? RunConfiguration.Create(destination is null ? Environment.CurrentDirectory : Path.GetDirectoryName(destination)!)
            : RunConfiguration.Load(source);
        var selected = 0;
        var dirty = source is null;
        var savedHere = source is not null && destination == source;
        var status = "Drafts can be saved with warnings. Run preflight separately to check Git and tools.";
        var controlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        Console.Write("\u001b[?1049h");
        try
        {
            while (true)
            {
                var warnings = config.Warnings();
                Console.Write("\u001b[H\u001b[2J");
                Line($"Abacus run config editor{(dirty ? " *" : "")} — {destination ?? "unsaved"}");
                Line("↑/↓ select • Enter edit/toggle • Delete unset • S save • A save as • Q quit");
                Line(status);
                var rows = Math.Max(1, Console.WindowHeight - 9);
                var top = Math.Max(0, selected - rows + 1);
                foreach (var (field, index) in RunConfiguration.Fields.Select((f, i) => (f, i)).Skip(top).Take(rows))
                {
                    var value = config.Document[field.Name]?.ToJsonString() ?? "(inherited / default / unset)";
                    Line($"{(index == selected ? ">" : " ")} {field.Name,-20} {value}");
                }
                Line($"Field {selected + 1}/{RunConfiguration.Fields.Count}: {RunConfiguration.Fields[selected].Hint}");
                if (warnings.Count == 0) Line("No option warnings. Git, tools, and repository policies are checked by preflight.");
                else
                {
                    Line($"WARNING: {warnings.Count} missing/invalid setting(s); saving is still allowed.");
                    foreach (var warning in warnings.Take(3)) Line("  ! " + warning);
                }
                var key = Console.ReadKey(true);
                try
                {
                    var action = key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                        ? ConsoleKey.Q : key.Key;
                    switch (action)
                    {
                        case ConsoleKey.UpArrow: selected = Math.Max(0, selected - 1); break;
                        case ConsoleKey.DownArrow: selected = Math.Min(RunConfiguration.Fields.Count - 1, selected + 1); break;
                        case ConsoleKey.Home: selected = 0; break;
                        case ConsoleKey.End: selected = RunConfiguration.Fields.Count - 1; break;
                        case ConsoleKey.Delete:
                        case ConsoleKey.Backspace:
                            dirty |= config.Document.Remove(RunConfiguration.Fields[selected].Name);
                            break;
                        case ConsoleKey.Enter:
                            Edit(config, RunConfiguration.Fields[selected]);
                            dirty = true;
                            break;
                        case ConsoleKey.S:
                        case ConsoleKey.A:
                            var path = destination;
                            if (key.Key == ConsoleKey.A || path is null)
                            {
                                var entered = Ask("Save to file (blank cancels)");
                                if (string.IsNullOrWhiteSpace(entered)) break;
                                path = Path.GetFullPath(entered);
                            }
                            var sameSavedFile = savedHere && path == destination;
                            var overwrite = sameSavedFile;
                            if (File.Exists(path) && !sameSavedFile)
                            {
                                if (!Confirm($"Overwrite '{path}'?")) break;
                                overwrite = true;
                            }
                            config.Save(path, overwrite);
                            destination = path;
                            savedHere = true;
                            dirty = false;
                            status = warnings.Count == 0 ? "Saved." : "Saved draft with warnings; complete the settings before running.";
                            break;
                        case ConsoleKey.Q:
                        case ConsoleKey.Escape:
                            if (!dirty || Confirm("Discard unsaved changes?")) return 0;
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                    or OptionsException or ArgumentException or NotSupportedException)
                { status = "Not saved/changed: " + ex.Message; }
            }
        }
        finally
        {
            Console.Write("\u001b[?1049l");
            Console.TreatControlCAsInput = controlCAsInput;
        }
    }

    private static void Edit(RunConfiguration config, RunConfiguration.Field field)
    {
        if (field.Kind == "bool")
        {
            var current = config.Document[field.Name]?.GetValue<bool>() ?? false;
            try { current = config.ResolveInheritance().Document[field.Name]?.GetValue<bool>() ?? false; }
            catch (OptionsException) { /* A broken base remains editable as a draft. */ }
            config.Document[field.Name] = !current;
            return;
        }
        if (field.Kind == "agents") { EditAgents(config); return; }
        if (field.Kind == "models")
        {
            var models = (JsonObject?)config.Document[field.Name]?.DeepClone() ?? new JsonObject();
            foreach (var tier in new[] { "high", "medium", "low" })
            {
                var text = Ask($"{tier} model [{models[tier]}] (blank keeps, - clears)");
                if (text == "-") models.Remove(tier);
                else if (text.Length > 0) models[tier] = text;
            }
            config.Document[field.Name] = models;
            return;
        }
        Line(field.Hint);
        Line("Current: " + (config.Document[field.Name]?.ToJsonString() ?? "unset"));
        var entered = Ask($"{field.Name} (blank keeps, - clears)");
        if (entered.Length == 0) return;
        if (entered == "-") { config.Document.Remove(field.Name); return; }
        JsonNode? value = field.Kind is "int" or "list" ? JsonNode.Parse(entered) : JsonValue.Create(entered);
        var draft = (JsonObject)config.Document.DeepClone();
        draft[field.Name] = value;
        new RunConfiguration(draft, config.BaseDirectory).ValidateShape();
        config.Document[field.Name] = value?.DeepClone();
    }

    private static void EditAgents(RunConfiguration config)
    {
        var source = config.Document["agents"];
        if (!config.Document.ContainsKey("agents"))
        {
            try { source = config.ResolveInheritance().Document["agents"]; }
            catch (OptionsException) { /* A broken base remains editable. */ }
        }
        var agents = (JsonArray?)source?.DeepClone() ?? new JsonArray();
        var changed = false;
        while (true)
        {
            Console.Write("\u001b[H\u001b[2J");
            Line("Agent workspaces — paths are relative to the config directory");
            for (var i = 0; i < agents.Count; i++)
                Line($"{i + 1}. {agents[i]?["name"]} — {agents[i]?["workspace"]}");
            var action = Ask("Agent number to edit, + to add, -number to remove, blank to return");
            if (action.Length == 0)
            {
                if (changed) config.Document["agents"] = agents;
                return;
            }
            if (action.StartsWith('-') && int.TryParse(action[1..], out var remove) && remove > 0 && remove <= agents.Count)
            { agents.RemoveAt(remove - 1); changed = true; continue; }
            JsonObject agent;
            if (action == "+") { agent = new JsonObject(); agents.Add(agent); changed = true; }
            else if (int.TryParse(action, out var index) && index > 0 && index <= agents.Count) agent = (JsonObject)agents[index - 1]!;
            else continue;
            foreach (var property in new[] { "name", "workspace" })
            {
                var text = Ask($"{property} [{agent[property]}] (blank keeps, - clears)");
                if (text == "-") changed |= agent.Remove(property);
                else if (text.Length > 0) { agent[property] = text; changed = true; }
            }
        }
    }

    private static bool Confirm(string message) => Ask(message + " [y/N]").Equals("y", StringComparison.OrdinalIgnoreCase);
    private static string Ask(string prompt)
    {
        Line(prompt);
        Console.Write("> ");
        return Console.ReadLine() ?? "";
    }

    // Never allow values loaded from a config to inject terminal escape sequences.
    private static void Line(string text)
    {
        var safe = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        Console.WriteLine(safe[..Math.Min(safe.Length, Math.Max(1, Console.WindowWidth - 1))]);
    }
}
