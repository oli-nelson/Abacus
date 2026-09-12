namespace Abacus;

/// <summary>
/// Splits a user-supplied argument string into argv tokens the way a POSIX shell
/// would, so extra agent arguments never pass through a shell unquoted.
/// </summary>
public static class AgentArguments
{
    public static IReadOnlyList<string> Empty { get; } = Array.Empty<string>();

    public static IReadOnlyList<string> Split(string text, string option)
    {
        ArgumentNullException.ThrowIfNull(text);
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var started = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\'')
            {
                started = true;
                var closing = text.IndexOf('\'', index + 1);
                if (closing < 0)
                    throw new OptionsException($"{option} has an unterminated ' quote");
                current.Append(text, index + 1, closing - index - 1);
                index = closing;
                continue;
            }

            if (character == '"')
            {
                started = true;
                var closed = false;
                for (index++; index < text.Length; index++)
                {
                    var quoted = text[index];
                    if (quoted == '"') { closed = true; break; }
                    if (quoted == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
                    {
                        current.Append(text[++index]);
                        continue;
                    }

                    current.Append(quoted);
                }

                if (!closed) throw new OptionsException($"{option} has an unterminated \" quote");
                continue;
            }

            if (character == '\\')
            {
                if (index + 1 >= text.Length)
                    throw new OptionsException($"{option} ends with a trailing backslash");
                started = true;
                current.Append(text[++index]);
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (started) arguments.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }

            started = true;
            current.Append(character);
        }

        if (started) arguments.Add(current.ToString());
        if (arguments.Count == 0) throw new OptionsException($"{option} cannot be empty");
        return arguments;
    }
}
