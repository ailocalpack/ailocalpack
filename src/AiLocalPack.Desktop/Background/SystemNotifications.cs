using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Background;

/// <summary>One OS notification; <see cref="Id"/> comes back through <see cref="ISystemNotifications.Activated"/> when clicked.</summary>
public sealed record SystemNotification(string Id, string Title, string Body);

/// <summary>
/// OS notifications (task 39): Windows toast, macOS <c>UNUserNotificationCenter</c>, Linux
/// <c>org.freedesktop.Notifications</c> over D-Bus. Never throws: a notification that can't be shown is logged.
/// </summary>
public interface ISystemNotifications : IDisposable
{
    Task ShowAsync(SystemNotification notification);

    /// <summary>
    /// Asks the OS for permission ahead of the first notification (macOS shows its prompt only for an app in front), so
    /// the app calls it at start while its window shows and the tray is on. Nothing where no permission is needed.
    /// </summary>
    void Prepare()
    {
    }

    /// <summary>The player clicked a notification (its ID); raised on any thread.</summary>
    event EventHandler<string>? Activated;
}

public static class SystemNotifications
{
    /// <summary>The app's notification identity (Windows AppUserModelID, Linux desktop entry name).</summary>
    public const string AppId = "io.github.ailocalpack";

    public static ISystemNotifications Create(OsEnvironment env, AppPaths paths, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(loggers);
        return env.Os switch
        {
            OsKind.Windows when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) => new WindowsToastNotifications(paths, loggers.CreateLogger<WindowsToastNotifications>()),
            OsKind.MacOS => new MacNotifications(loggers.CreateLogger<MacNotifications>()),
            OsKind.Linux => new FreeDesktopNotifications(loggers.CreateLogger<FreeDesktopNotifications>()),
            _ => new NoSystemNotifications(),
        };
    }
}

/// <summary>An OS without notifications the app can show.</summary>
public sealed class NoSystemNotifications : ISystemNotifications
{
    public event EventHandler<string>? Activated
    {
        add { }
        remove { }
    }

    public Task ShowAsync(SystemNotification notification) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
