using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Registry;

/// <summary>
/// The app's view of the registry index: refreshes it in the background (from <c>AILOCALPACK_INDEX_URL</c>, the hidden
/// <see cref="AppSettings.RegistryIndexUrl"/> setting or the public registry) and keeps the latest
/// <see cref="RegistryIndexSnapshot"/>: fresh, the saved copy when offline (stale, with its date), or none. One refresh
/// runs at a time; a request during one joins it. Never throws.
/// </summary>
public sealed class PackIndex(
    RegistryIndexClient client,
    SettingsStore settings,
    OsEnvironment env,
    ILogger<PackIndex> logger)
{
    private readonly Lock _lock = new();
    private Task<RegistryIndexSnapshot>? _running;

    /// <summary>The last refresh's result; <c>null</c> before the first finished.</summary>
    public RegistryIndexSnapshot? Current { get; private set; }

    /// <summary>Raised (on the refreshing thread) with each new <see cref="Current"/>.</summary>
    public event EventHandler<RegistryIndexSnapshot>? Changed;

    public Task<RegistryIndexSnapshot> RefreshAsync()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
            {
                return _running;
            }

            return _running = Task.Run(RefreshCoreAsync);
        }
    }

    private async Task<RegistryIndexSnapshot> RefreshCoreAsync()
    {
        RegistryIndexSnapshot snapshot;
        try
        {
            snapshot = RegistryLocation.TryResolve(env, settings.Current.RegistryIndexUrl, out var location, out var error)
                ? await client.FetchAsync(location).ConfigureAwait(false)
                : RegistryIndexSnapshot.InvalidLocation(error);
            if (snapshot.Problem == RegistryIndexProblem.InvalidLocation)
            {
                logger.LogWarning("Registry address is not valid: {Error}", snapshot.Detail);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Refreshing the registry index failed");
            snapshot = new RegistryIndexSnapshot(null, RegistryIndexState.Unavailable, null, null, RegistryIndexProblem.Offline, e.Message);
        }

        Current = snapshot;
        Changed?.Invoke(this, snapshot);
        return snapshot;
    }
}
