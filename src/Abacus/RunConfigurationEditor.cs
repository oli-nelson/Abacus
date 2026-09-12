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
        var ui = TerminalUi.ForConsoleOut();
        var controlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        Console.Write("\u001b[?1049h\u001b[?25l");
        try
        {
            while (true)
            {
                var warnings = config.Warnings();
                Console.Write("\u001b[H\u001b[2J");
                Line($"Abacus run config editor{(dirty ? " *" : "")} — {destination ?? "unsaved"}", ui,
                    TerminalUi.Bold, TerminalUi.Cyan);
                Line("────────────────────────────────────────────────────────────────────────", ui, TerminalUi.Dim);
                Line("↑/↓ select  Enter edit/toggle  Delete unset  S save  A save as  Q quit", ui, TerminalUi.Dim);
                Line(status, ui, StatusStyle(status));
                Line(string.Empty, ui);
                var rows = Math.Max(1, Console.WindowHeight - 9);
                var top = Math.Max(0, selected - rows + 1);
                foreach (var (field, index) in RunConfiguration.Fields.Select((f, i) => (f, i)).Skip(top).Take(rows))
                {
                    var value = config.Document[field.Name]?.ToJsonString() ?? "(inherited / default / unset)";
                    var row = $"{(index == selected ? "›" : " ")} {field.Name,-20} {value}";
                    if (index == selected)
                        Line(row, ui, TerminalUi.Bold, TerminalUi.Reverse, TerminalUi.Cyan);
                    else if (!config.Document.ContainsKey(field.Name))
                        Line(row, ui, TerminalUi.Dim);
                    else
                        Line(row, ui);
                }
                Line(string.Empty, ui);
                Line($"Field {selected + 1}/{RunConfiguration.Fields.Count}: {RunConfiguration.Fields[selected].Hint}", ui,
                    TerminalUi.Blue);
                if (warnings.Count == 0)
                    Line("[OK] No option warnings. Git, tools, and repository policies are checked by preflight.", ui,
                        TerminalUi.Green);
                else
                {
                    Line($"[WARN] {warnings.Count} missing/invalid setting(s); saving is still allowed.", ui,
                        TerminalUi.Bold, TerminalUi.Yellow);
                    foreach (var warning in warnings.Take(3)) Line("  ! " + warning, ui, TerminalUi.Yellow);
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
                            Edit(config, RunConfiguration.Fields[selected], ui);
                            dirty = true;
                            break;
                        case ConsoleKey.S:
                        case ConsoleKey.A:
                            var path = destination;
                            if (key.Key == ConsoleKey.A || path is null)
                            {
                                var entered = Ask("Save to file (blank cancels)", ui);
                                if (string.IsNullOrWhiteSpace(entered)) break;
                                path = Path.GetFullPath(entered);
                            }
                            var sameSavedFile = savedHere && path == destination;
                            var overwrite = sameSavedFile;
                            if (File.Exists(path) && !sameSavedFile)
                            {
                                if (!Confirm($"Overwrite '{path}'?", ui)) break;
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
                            if (!dirty || Confirm("Discard unsaved changes?", ui)) return 0;
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
            Console.Write("\u001b[0m\u001b[?25h\u001b[?1049l");
            Console.TreatControlCAsInput = controlCAsInput;
        }
    }

    private static void Edit(RunConfiguration config, RunConfiguration.Field field, TerminalUi ui)
    {
        if (field.Kind == "bool")
        {
            var current = config.Document[field.Name]?.GetValue<bool>() ?? false;
            try { current = config.ResolveInheritance().Document[field.Name]?.GetValue<bool>() ?? false; }
            catch (OptionsException) { /* A broken base remains editable as a draft. */ }
            config.Document[field.Name] = !current;
            return;
        }
        if (field.Kind == "agents") { EditAgents(config, ui); return; }
        if (field.Kind is "models" or "args")
        {
            var routes = (JsonObject?)config.Document[field.Name]?.DeepClone() ?? new JsonObject();
            foreach (var tier in new[] { "high", "medium", "low" })
            {
                var label = field.Kind == "models" ? "model#effort" : "extra arguments";
                var text = Ask($"{tier} {label} [{routes[tier]}] (blank keeps, - clears)", ui);
                if (text == "-") routes.Remove(tier);
                else if (text.Length > 0) routes[tier] = text;
            }
            config.Document[field.Name] = routes;
            return;
        }
        Line(field.Hint, ui, TerminalUi.Blue);
        Line("Current: " + (config.Document[field.Name]?.ToJsonString() ?? "unset"), ui, TerminalUi.Dim);
        var entered = Ask($"{field.Name} (blank keeps, - clears)", ui);
        if (entered.Length == 0) return;
        if (entered == "-") { config.Document.Remove(field.Name); return; }
        JsonNode? value = field.Kind is "int" or "list" ? JsonNode.Parse(entered) : JsonValue.Create(entered);
        var draft = (JsonObject)config.Document.DeepClone();
        draft[field.Name] = value;
        new RunConfiguration(draft, config.BaseDirectory).ValidateShape();
        config.Document[field.Name] = value?.DeepClone();
    }

    private static void EditAgents(RunConfiguration config, TerminalUi ui)
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
            Line("Agent workspaces", ui, TerminalUi.Bold, TerminalUi.Cyan);
            Line("Paths are relative to the config directory", ui, TerminalUi.Dim);
            Line(string.Empty, ui);
            for (var i = 0; i < agents.Count; i++)
                Line($"{i + 1}. {agents[i]?["name"]} — {agents[i]?["workspace"]}", ui);
            if (agents.Count == 0) Line("(no agent workspaces configured)", ui, TerminalUi.Yellow);
            var action = Ask("Agent number to edit, + to add, -number to remove, blank to return", ui);
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
                var text = Ask($"{property} [{agent[property]}] (blank keeps, - clears)", ui);
                if (text == "-") changed |= agent.Remove(property);
                else if (text.Length > 0) { agent[property] = text; changed = true; }
            }
        }
    }

    private static bool Confirm(string message, TerminalUi ui) =>
        Ask(message + " [y/N]", ui).Equals("y", StringComparison.OrdinalIgnoreCase);

    private static string Ask(string prompt, TerminalUi ui)
    {
        Console.Write("\u001b[?25h");
        Line(prompt, ui, TerminalUi.Blue);
        Console.Write(ui.Command("› "));
        Console.Out.Flush();
        var response = Console.ReadLine() ?? "";
        Console.Write("\u001b[?25l");
        return response;
    }

    // Never allow values loaded from a config to inject terminal escape sequences.
    private static void Line(string text, TerminalUi ui, params string[] styles)
    {
        var safe = TerminalUi.Sanitize(text);
        var clipped = safe[..Math.Min(safe.Length, Math.Max(1, Console.WindowWidth - 1))];
        Console.WriteLine(ui.Style(clipped, styles));
    }

    private static string StatusStyle(string status) => status.StartsWith("Not ", StringComparison.Ordinal)
        ? TerminalUi.Red
        : status.StartsWith("Saved", StringComparison.Ordinal)
            ? TerminalUi.Green
            : TerminalUi.Cyan;
}
