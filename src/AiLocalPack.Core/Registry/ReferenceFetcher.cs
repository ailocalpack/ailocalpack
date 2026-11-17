using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Registry;

/// <summary>
/// Downloads the reference packs of a new translation (decided 2026-11-16, <see cref="ReferenceSelection"/>): a recipe
/// pack's manifest and string table through the pack cache (<see cref="PackFetcher"/>, hash-checked as for an install),
/// a binary patch pack's manifest only (its archive is never downloaded). A pack that can't be fetched is left out and
/// logged: references are hints, never a reason not to start. Never throws but for cancellation.
/// </summary>
public sealed class ReferenceFetcher(PackManifestClient manifests, PackFetcher fetcher, ILogger<ReferenceFetcher>? logger = null)
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public async Task<IReadOnlyList<ReferencePack>> FetchAsync(
        RegistryLocation location,
        RegistryIndex? index,
        Game game,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(game);
        var packs = new List<ReferencePack>();
        foreach (var listed in ReferenceSelection.Select(index, game.Id, language, game.Version?.BuildId, game.Platform))
        {
            var manifest = await manifests.FetchAsync(location, listed, cancellationToken).ConfigureAwait(false);
            if (manifest.Manifest is not { } read)
            {
                _logger.LogWarning("Reference pack {Language}/{Pack} left out: {Problem} ({Detail})", listed.Language, listed.Id, manifest.Problem, manifest.Detail);
                continue;
            }

            if (read.Patch is not null)
            {
                packs.Add(new ReferencePack(read, null, ReferencePack.IsSameBuild(read, game.Version, game.Platform)));
                continue;
            }

            var fetched = await fetcher.FetchAsync(location, listed, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (fetched.Pack is not { } cached || ReadStrings(cached) is not { } strings)
            {
                _logger.LogWarning("Reference pack {Language}/{Pack} left out: {Problem} ({Detail})", listed.Language, listed.Id, fetched.Problem, fetched.Detail);
                continue;
            }

            packs.Add(new ReferencePack(cached.Manifest, strings, ReferencePack.IsSameBuild(cached.Manifest, game.Version, game.Platform)));
        }

        return packs;
    }

    private static Packs.StringTable? ReadStrings(Packs.CachedPack cached)
    {
        try
        {
            return cached.ReadStrings().Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
