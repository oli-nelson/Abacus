namespace Abacus;

public sealed record AgentCommand(
    string Executable,
    IReadOnlyList<string> ArgumentsBeforePrompt,
    IReadOnlyList<string> ArgumentsAfterPrompt)
{
    public IReadOnlyList<string> WithPrompt(string prompt)
    {
        var arguments = new List<string>(ArgumentsBeforePrompt.Count + ArgumentsAfterPrompt.Count + 1);
        arguments.AddRange(ArgumentsBeforePrompt);
        arguments.Add(prompt);
        arguments.AddRange(ArgumentsAfterPrompt);
        return arguments;
    }
}

public static class AgentCommandFactory
{
    public static AgentCommand Create(
        AgentMode mode,
        string executable,
        string model,
        string workspacePath,
        string? serverUrl,
        string sessionName,
        string effort = "high",
        bool remote = false,
        string? remoteSessionName = null)
    {
        if (remote && mode is not AgentMode.Claude)
        {
            throw new ArgumentException("remote control is supported only for Claude Code", nameof(mode));
        }

        return mode switch
        {
            // The interactive OpenCode TUI has no variant option. A #suffix is parsed as part of the model ID.
            AgentMode.OpenCode => new AgentCommand(
                executable,
                ["--prompt"],
                ["--model", model]),
            AgentMode.Codex => CreateCodex(executable, model, workspacePath, effort),
            AgentMode.Claude => CreateClaude(
                executable,
                model,
                effort,
                sessionName,
                remote,
                remoteSessionName),
            AgentMode.OpenCodeServer => new AgentCommand(
                executable,
                ["run"],
                [
                    "--model", model,
                    "--variant", effort,
                    "--attach", RequireServerUrl(serverUrl),
                    "--dir", workspacePath,
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown agent mode"),
        };
    }

    private static AgentCommand CreateCodex(
        string executable,
        string model,
        string workspacePath,
        string effort)
    {
        var arguments = new List<string>([
            "--cd", workspacePath,
            "--model", model,
            "--config", $"model_reasoning_effort={effort}",
            "--approve-for-me",
        ]);
        return new AgentCommand(executable, arguments, []);
    }

    private static AgentCommand CreateClaude(
        string executable,
        string model,
        string effort,
        string sessionName,
        bool remote,
        string? remoteSessionName)
    {
        var arguments = new List<string>
        {
            "--model", model,
            "--effort", effort,
            "--permission-mode", "auto",
            "--name", sessionName,
        };
        if (remote)
        {
            if (string.IsNullOrWhiteSpace(remoteSessionName))
            {
                throw new ArgumentException(
                    "Claude Code remote control requires a session name",
                    nameof(remoteSessionName));
            }

            arguments.AddRange(["--remote-control", remoteSessionName]);
        }

        return new AgentCommand(executable, arguments, []);
    }

    public static string ExecutableName(AgentMode mode) => mode switch
    {
        AgentMode.OpenCode or AgentMode.OpenCodeServer => "opencode",
        AgentMode.Codex => "codex",
        AgentMode.Claude => "claude",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown agent mode"),
    };

    public static string DisplayName(AgentMode mode) => mode switch
    {
        AgentMode.OpenCode => "OpenCode",
        AgentMode.Codex => "Codex",
        AgentMode.Claude => "Claude Code",
        AgentMode.OpenCodeServer => "OpenCode Server",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown agent mode"),
    };

    private static string RequireServerUrl(string? serverUrl) => serverUrl
        ?? throw new ArgumentException("OpenCode Server mode requires a server URL", nameof(serverUrl));
}
