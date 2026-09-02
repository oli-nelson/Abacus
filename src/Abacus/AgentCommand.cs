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
        string effort = "high") => mode switch
    {
        AgentMode.OpenCode => new AgentCommand(
            executable,
            ["--mini", "--prompt"],
            ["--model", $"{model}#{effort}"]),
        AgentMode.Codex => new AgentCommand(
            executable,
            [
                "--cd", workspacePath,
                "--model", model,
                "--config", $"model_reasoning_effort={effort}",
                "--approve-for-me",
            ],
            []),
        AgentMode.Claude => new AgentCommand(
            executable,
            [
                "--model", model,
                "--effort", effort,
                "--permission-mode", "auto",
                "--name", sessionName,
            ],
            []),
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
