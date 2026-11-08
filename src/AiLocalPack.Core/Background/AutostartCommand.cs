using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Background;

/// <summary>
/// The command the OS runs at sign-in to start the app in the tray (task 39): the app's executable and
/// <see cref="BackgroundArgument"/>.
/// </summary>
/// <param name="Executable">Absolute path of the program to run.</param>
/// <param name="Arguments">Its arguments, <see cref="BackgroundArgument"/> last.</param>
public sealed record AutostartCommand(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>Starts the app without its window, in the tray.</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// The running app: an AppImage by its image (<c>$APPIMAGE</c>: the process runs from a temporary mount), a
    /// framework-dependent run (<c>dotnet AiLocalPack.dll</c>) by <c>dotnet</c> + the assembly, else the process's own
    /// executable (a macOS bundle's <c>Contents/MacOS</c> binary, the Velopack <c>current</c> folder on Windows).
    /// <c>null</c> when the process path is unknown.
    /// </summary>
    public static AutostartCommand? ForCurrentProcess(OsEnvironment env, string? processPath, string? entryAssembly)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.Os == OsKind.Linux && env.GetVariable("APPIMAGE") is { } image && Path.IsPathFullyQualified(image))
        {
            return new AutostartCommand(image, [BackgroundArgument]);
        }

        if (string.IsNullOrEmpty(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(entryAssembly) || !Path.IsPathFullyQualified(entryAssembly)
                ? null
                : new AutostartCommand(processPath, [entryAssembly, BackgroundArgument]);
        }

        return new AutostartCommand(processPath, [BackgroundArgument]);
    }
}
