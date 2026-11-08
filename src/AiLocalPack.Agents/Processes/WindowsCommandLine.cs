using System.Text;

namespace AiLocalPack.Agents.Processes;

/// <summary>
/// Command lines for Windows batch files (task 41). A program installed by npm (<c>claude.cmd</c>, <c>codex.cmd</c>) is a
/// batch file, which Windows runs through <c>cmd.exe</c>: cmd reads the line with its own rules (<c>&amp;</c>, <c>|</c>,
/// <c>%</c>, <c>^</c>…), not the C runtime's that <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> quotes for,
/// and the npm shim passes the arguments on with <c>%*</c>, so cmd reads them a second time. Each argument is quoted for the
/// C runtime, then every cmd metacharacter is escaped with <c>^</c> twice (once per cmd pass), the scheme of npm's
/// <c>cross-spawn</c>; the whole line goes to <c>cmd.exe /d /s /c "…"</c> as a raw command line.
/// </summary>
public static class WindowsCommandLine
{
    private const string MetaCharacters = "()[]%!^\"`<>&|;, *?";

    /// <summary>Whether <paramref name="executable"/> is a batch file that runs through <c>cmd.exe</c>.</summary>
    public static bool IsBatchFile(string executable) =>
        executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="argument"/> in double quotes for the C runtime's parser (<c>CommandLineToArgvW</c>): backslashes
    /// before a quote and at the end doubled, quotes escaped with a backslash.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }

            backslashes = 0;
            quoted.Append(c);
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// The process that runs the batch file <paramref name="batchFile"/> with <paramref name="arguments"/> exactly as given:
    /// <c>cmd.exe</c> (<c>%ComSpec%</c>) with a raw <c>/d /s /c "…"</c> line. Other settings of <paramref name="template"/>
    /// (folder, environment, standard input) are kept.
    /// </summary>
    public static ProcessSpec ForBatchFile(string batchFile, IReadOnlyList<string> arguments, ProcessSpec template, string? comSpec = null)
    {
        ArgumentNullException.ThrowIfNull(batchFile);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(template);
        var line = new StringBuilder(Escape(batchFile));
        foreach (var argument in arguments)
        {
            line.Append(' ').Append(Escape(Escape(Quote(argument))));
        }

        return template with
        {
            FileName = comSpec ?? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = [],
            RawArguments = $"/d /s /c \"{line}\"",
        };
    }

    /// <summary>Every cmd metacharacter of <paramref name="text"/> escaped with <c>^</c>.</summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (MetaCharacters.Contains(c, StringComparison.Ordinal))
            {
                escaped.Append('^');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
