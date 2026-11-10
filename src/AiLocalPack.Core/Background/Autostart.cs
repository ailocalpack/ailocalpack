using System.Security;
using System.Text;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Background;

/// <summary>
/// "Start with the system" (task 39, setting <see cref="Settings.AppSettings.RunInBackground"/>): one entry per OS that
/// runs <see cref="AutostartCommand"/> at sign-in. <see cref="Render"/> is the entry's exact content, so
/// <see cref="Sync"/> rewrites an entry that points elsewhere (the app moved) and leaves a matching one alone.
/// </summary>
public interface IAutostart
{
    /// <summary>Where the entry lives, for logs ("HKCU\…\Run\ailocalpack", a file path).</summary>
    string Location { get; }

    /// <summary>The entry as stored, or <c>null</c> when there is none.</summary>
    string? Read();

    /// <summary>The entry's content for the command.</summary>
    string Render(AutostartCommand command);

    /// <summary>Writes the entry (throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> on failure).</summary>
    void Write(string content);

    /// <summary>Removes the entry; nothing when there is none.</summary>
    void Remove();
}

/// <summary>What <see cref="Autostart.Sync"/> did.</summary>
public enum AutostartChange
{
    /// <summary>The entry already was as wanted.</summary>
    None,

    Written,

    Removed,
}

public static class Autostart
{
    /// <summary>Stable name of the entry: Run value name, LaunchAgent label, autostart file name.</summary>
    public const string Name = "ailocalpack";

    /// <summary>The macOS LaunchAgent label (reverse DNS of the project's GitHub Pages domain).</summary>
    public const string MacLabel = "io.github.ailocalpack";

    /// <summary>The entry for this OS: Windows Run key, macOS LaunchAgent, Linux XDG autostart.</summary>
    public static IAutostart For(OsEnvironment env, IWindowsRegistryWriter registry)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(registry);
        return env.Os switch
        {
            OsKind.Windows => new WindowsAutostart(registry),
            OsKind.MacOS => new MacAutostart(env),
            _ => new LinuxAutostart(env),
        };
    }

    /// <summary>
    /// Makes the entry match the setting: <paramref name="enabled"/> with a command → written unless identical;
    /// otherwise removed when present. Throws what the store throws.
    /// </summary>
    public static AutostartChange Sync(IAutostart autostart, bool enabled, AutostartCommand? command)
    {
        ArgumentNullException.ThrowIfNull(autostart);
        var current = autostart.Read();
        if (enabled && command is not null)
        {
            var wanted = autostart.Render(command);
            if (current == wanted)
            {
                return AutostartChange.None;
            }

            autostart.Write(wanted);
            return AutostartChange.Written;
        }

        if (current is null)
        {
            return AutostartChange.None;
        }

        autostart.Remove();
        return AutostartChange.Removed;
    }
}

/// <summary>Windows: the value <c>ailocalpack</c> under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
public sealed class WindowsAutostart(IWindowsRegistryWriter registry) : IAutostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string Location => $@"HKCU\{RunKey}\{Autostart.Name}";

    public string? Read() => registry.GetCurrentUserString(RunKey, Autostart.Name);

    /// <summary>A command line as <c>CommandLineToArgvW</c> splits it: the executable always quoted, arguments when needed.</summary>
    public string Render(AutostartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var line = new StringBuilder().Append('"').Append(command.Executable).Append('"');
        foreach (var argument in command.Arguments)
        {
            line.Append(' ').Append(Quote(argument));
        }

        return line.ToString();
    }

    public void Write(string content)
    {
        if (!registry.SetCurrentUserString(RunKey, Autostart.Name, content))
        {
            throw new IOException($"Couldn't write the registry value {Location}.");
        }
    }

    public void Remove()
    {
        if (!registry.DeleteCurrentUserValue(RunKey, Autostart.Name))
        {
            throw new IOException($"Couldn't remove the registry value {Location}.");
        }
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        // Backslashes are literal except before a quote; double those, and the ones before the closing quote.
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? (slashes * 2) + 1 : slashes).Append(c);
            slashes = 0;
        }

        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}

/// <summary>
/// macOS: a LaunchAgent <c>~/Library/LaunchAgents/io.github.ailocalpack.plist</c> run at load (sign-in), no keep-alive
/// (Quit from the tray stays quit). Chosen over <c>SMAppService</c>, which needs a signed bundle: the agent works for
/// any build and is plain text.
/// </summary>
public sealed class MacAutostart(OsEnvironment env) : IAutostart
{
    public string Location => Path.Combine(env.HomeDirectory, "Library", "LaunchAgents", Autostart.MacLabel + ".plist");

    public string? Read() => FileText.Read(Location);

    public string Render(AutostartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var plist = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n")
            .Append("<plist version=\"1.0\">\n<dict>\n")
            .Append("\t<key>Label</key>\n\t<string>").Append(Autostart.MacLabel).Append("</string>\n")
            .Append("\t<key>ProgramArguments</key>\n\t<array>\n")
            .Append("\t\t<string>").Append(Xml(command.Executable)).Append("</string>\n");
        foreach (var argument in command.Arguments)
        {
            plist.Append("\t\t<string>").Append(Xml(argument)).Append("</string>\n");
        }

        return plist
            .Append("\t</array>\n")
            .Append("\t<key>RunAtLoad</key>\n\t<true/>\n")
            .Append("\t<key>ProcessType</key>\n\t<string>Interactive</string>\n")
            .Append("</dict>\n</plist>\n")
            .ToString();
    }

    public void Write(string content) => AtomicFile.WriteAllBytes(Location, Encoding.UTF8.GetBytes(content));

    public void Remove() => FileText.Delete(Location);

    private static string Xml(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>
/// Linux: <c>$XDG_CONFIG_HOME/autostart/ailocalpack.desktop</c> (default <c>~/.config</c>; a relative value is
/// ignored), the XDG Autostart spec every desktop follows, Steam Deck's desktop mode (KDE) included.
/// </summary>
public sealed class LinuxAutostart(OsEnvironment env) : IAutostart
{
    public string Location
    {
        get
        {
            var config = env.GetVariable("XDG_CONFIG_HOME") is { } xdg && Path.IsPathFullyQualified(xdg)
                ? xdg
                : Path.Combine(env.HomeDirectory, ".config");
            return Path.Combine(config, "autostart", Autostart.Name + ".desktop");
        }
    }

    public string? Read() => FileText.Read(Location);

    public string Render(AutostartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var exec = string.Join(' ', new[] { command.Executable }.Concat(command.Arguments).Select(Quote));
        return "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=" + AppInfo.DisplayName + "\n"
            + "Comment=Keeps game translations in place after game updates\n"
            + "Exec=" + exec + "\n"
            + "Terminal=false\n"
            + "X-GNOME-Autostart-enabled=true\n";
    }

    public void Write(string content) => AtomicFile.WriteAllBytes(Location, Encoding.UTF8.GetBytes(content));

    public void Remove() => FileText.Delete(Location);

    /// <summary>
    /// An <c>Exec</c> argument (Desktop Entry spec): quoted when it has a reserved character, with <c>" ` $ \</c>
    /// escaped inside the quotes; then <c>%</c> doubled (field codes) and <c>\</c> doubled (the file's string escaping).
    /// </summary>
    private static string Quote(string argument)
    {
        const string reserved = " \t\n\"'\\><~|&;$*?#()`";
        var text = argument;
        if (text.Length == 0 || text.IndexOfAny(reserved.ToCharArray()) >= 0)
        {
            var quoted = new StringBuilder("\"");
            foreach (var c in text)
            {
                if (c is '"' or '`' or '$' or '\\')
                {
                    quoted.Append('\\');
                }

                quoted.Append(c);
            }

            text = quoted.Append('"').ToString();
        }

        return text.Replace("%", "%%", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal);
    }
}

/// <summary>Small file helpers of the autostart entries.</summary>
internal static class FileText
{
    public static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
