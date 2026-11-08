using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Background;

/// <summary>
/// How to start the running app again as another process (autostart at sign-in, the toolkit MCP server an agent
/// starts): the program and the arguments that go before the app's own.
/// </summary>
/// <param name="Executable">Absolute path of the program to run.</param>
/// <param name="Prefix">Arguments before the app's own (the entry assembly for <c>dotnet x.dll</c>), else empty.</param>
public sealed record AppLaunch(string Executable, IReadOnlyList<string> Prefix)
{
    /// <summary>
    /// The running app: an AppImage by its image (<c>$APPIMAGE</c>: the process runs from a temporary mount), a
    /// framework-dependent run (<c>dotnet AiLocalPack.dll</c>) by <c>dotnet</c> + the assembly, else the process's own
    /// executable (a macOS bundle's <c>Contents/MacOS</c> binary, the Velopack <c>current</c> folder on Windows).
    /// <c>null</c> when the process path is unknown.
    /// </summary>
    public static AppLaunch? ForCurrentProcess(OsEnvironment env, string? processPath, string? entryAssembly)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.Os == OsKind.Linux && env.GetVariable("APPIMAGE") is { } image && Path.IsPathFullyQualified(image))
        {
            return new AppLaunch(image, []);
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
                : new AppLaunch(processPath, [entryAssembly]);
        }

        return new AppLaunch(processPath, []);
    }

    /// <summary>The app's arguments after <see cref="Prefix"/>.</summary>
    public IReadOnlyList<string> With(params IEnumerable<string> arguments) => [.. Prefix, .. arguments];
}
