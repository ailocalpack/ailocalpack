namespace AiLocalPack.Desktop.Updates;

/// <summary>How this copy of the app was installed, which decides whether it can update itself.</summary>
public enum AppInstallKind
{
    /// <summary>Not installed from a release (<c>dotnet run</c>, a plain build): no updates.</summary>
    Development,

    /// <summary>Installed by the release's installer (Windows Setup, the macOS .app, the Linux AppImage).</summary>
    Installed,

    /// <summary>The Windows portable release (a folder of its own).</summary>
    Portable,
}

/// <summary>A newer version found by <see cref="IAppUpdateSource.CheckAsync"/>.</summary>
/// <param name="Version">The version as released ("0.5.0").</param>
/// <param name="Release">The source's own handle of the release (Velopack's <c>UpdateInfo</c>).</param>
public sealed record AppUpdateOffer(string Version, object? Release);

/// <summary>
/// Where app updates come from and how they are applied (task 67): Velopack over GitHub Releases in the app
/// (<see cref="VelopackUpdateSource"/>), a fake in tests. Methods may throw on network and disk problems;
/// <see cref="AppUpdater"/> turns them into its <see cref="AppUpdateStatus.Failed"/> state.
/// </summary>
public interface IAppUpdateSource
{
    AppInstallKind Install { get; }

    /// <summary>The installed version ("0.4.0"); <c>null</c> when not installed (the build's own version is shown then).</summary>
    string? CurrentVersion { get; }

    /// <summary>The release channel when it isn't the stable one; <c>null</c> = stable.</summary>
    string? Channel { get; }

    /// <summary>An update downloaded earlier and not applied yet (the app wasn't restarted); <c>null</c> for none.</summary>
    AppUpdateOffer? PendingRestart { get; }

    /// <summary>The newest release when it is newer than this one; <c>null</c> when this is the latest.</summary>
    Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads <paramref name="offer"/> (deltas when it can), reporting 0–100.</summary>
    Task DownloadAsync(AppUpdateOffer offer, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>Starts the updater, which waits for this process to exit, applies <paramref name="offer"/> and starts the new version.</summary>
    void ApplyOnExit(AppUpdateOffer offer);
}

/// <summary>Quits the app the way the tray's <c>Quit</c> does (windows closed, services disposed).</summary>
public interface IAppQuit
{
    void Quit();
}
