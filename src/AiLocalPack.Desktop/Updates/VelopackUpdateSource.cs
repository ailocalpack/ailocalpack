using AiLocalPack.Core.Storage;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace AiLocalPack.Desktop.Updates;

/// <summary>
/// App updates through Velopack (task 67): the releases of <c>ailocalpack/ailocalpack</c> on GitHub, or a dev feed
/// (<see cref="UrlVariable"/>: a folder <c>vpk pack</c> wrote into, or a web address serving one). Velopack's
/// <c>VelopackApp.Build().Run()</c> in <c>Program.Main</c> handles the install / update hooks before any UI.
/// </summary>
public sealed class VelopackUpdateSource : IAppUpdateSource
{
    /// <summary>Dev: where updates come from instead of the GitHub Releases (a local folder feed, or an http(s) one).</summary>
    public const string UrlVariable = "AILOCALPACK_UPDATE_URL";

    public static readonly Uri Repository = new("https://github.com/ailocalpack/ailocalpack");

    private readonly UpdateManager _manager;

    public VelopackUpdateSource(OsEnvironment env, ILogger<VelopackUpdateSource> logger)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(logger);
        Location = env.GetVariable(UrlVariable) is { Length: > 0 } custom ? custom : Repository.AbsoluteUri;
        _manager = new UpdateManager(SourceFor(Location));
        logger.LogInformation(
            "App updates from {Location}; installed: {Installed}, version {Version}, channel {Channel}",
            Location, _manager.IsInstalled, _manager.CurrentVersion, InstalledChannel ?? "-");
    }

    /// <summary>The feed in use (the GitHub repository, or <see cref="UrlVariable"/>).</summary>
    public string Location { get; }

    public AppInstallKind Install => !_manager.IsInstalled ? AppInstallKind.Development
        : _manager.IsPortable ? AppInstallKind.Portable
        : AppInstallKind.Installed;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <summary>Velopack names each OS's default channel after it; any other channel is shown by name.</summary>
    public string? Channel => InstalledChannel is { } channel && channel is not ("win" or "osx" or "linux") ? channel : null;

    private string? InstalledChannel => _manager.IsInstalled ? VelopackLocator.Current.Channel : null;

    public AppUpdateOffer? PendingRestart => _manager.IsInstalled && _manager.UpdatePendingRestart is { } asset
        ? new AppUpdateOffer(asset.Version.ToString(), asset)
        : null;

    public async Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return info is null ? null : new AppUpdateOffer(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AppUpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return offer.Release is UpdateInfo info
            ? _manager.DownloadUpdatesAsync(info, progress, cancellationToken)
            : Task.CompletedTask; // a pending update is on disk already
    }

    public void ApplyOnExit(AppUpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        VelopackAsset? asset = offer.Release switch
        {
            UpdateInfo info => info.TargetFullRelease,
            VelopackAsset pending => pending,
            _ => null,
        };
        _manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true);
    }

    /// <summary>GitHub Releases for a github.com repository, a web feed for another http(s) address, else a local folder.</summary>
    internal static IUpdateSource SourceFor(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(uri.AbsoluteUri.TrimEnd('/'), accessToken: null, prerelease: false)
                : new SimpleWebSource(uri);
        }

        return new SimpleFileSource(new DirectoryInfo(location));
    }
}

/// <summary>Quits through the desktop lifetime on the UI thread.</summary>
internal sealed class DesktopAppQuit : IAppQuit
{
    public void Quit() => Dispatcher.UIThread.Post(() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
}
