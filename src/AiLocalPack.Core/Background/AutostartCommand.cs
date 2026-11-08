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

    /// <summary>The running app (<see cref="AppLaunch.ForCurrentProcess"/>) with <see cref="BackgroundArgument"/>; <c>null</c> when the process path is unknown.</summary>
    public static AutostartCommand? ForCurrentProcess(OsEnvironment env, string? processPath, string? entryAssembly) =>
        AppLaunch.ForCurrentProcess(env, processPath, entryAssembly) is { } launch
            ? new AutostartCommand(launch.Executable, launch.With(BackgroundArgument))
            : null;
}
