using System.Collections.Concurrent;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Desktop.Registry;

/// <summary>
/// Pack manifests for the Packs tab, fetched when a pack is looked at (<see cref="PackManifestClient"/>) and kept for
/// the session by address and revision, so going back to a pack costs nothing; failures are not kept (Retry fetches
/// again). <see cref="CheckAsync"/> runs <see cref="PackCompatibilityChecker"/> off the UI thread and saves the file
/// hashes it computed.
/// </summary>
public sealed class PackManifests(PackManifestClient client, PackCompatibilityChecker checker, FileHashCache hashes)
{
    private readonly ConcurrentDictionary<string, PackManifest> _loaded = new(StringComparer.Ordinal);

    public async Task<PackManifestFetch> LoadAsync(RegistryLocation location, RegistryPack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(pack);
        var key = $"{location.IndexUri.AbsoluteUri}|{pack.ManifestPath}|{pack.Revision}";
        if (_loaded.TryGetValue(key, out var known))
        {
            return new PackManifestFetch(known, PackManifestProblem.None);
        }

        var fetch = await Task.Run(() => client.FetchAsync(location, pack, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (fetch.Manifest is { } manifest)
        {
            _loaded[key] = manifest;
        }

        return fetch;
    }

    public Task<PackCompatibility> CheckAsync(PackManifest manifest, Game game, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var result = checker.Check(manifest, game, cancellationToken);
                hashes.Save();
                return result;
            },
            cancellationToken);
}
