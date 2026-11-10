using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Generation;

/// <summary>The installed pack an update job would build on (task 56), for the start form.</summary>
public interface IUpdateBases
{
    /// <summary>
    /// The installed pack of the game with its files: a local pack from Packs → Mine, a registry pack from the pack cache,
    /// else downloaded again while the registry lists its revision. <c>null</c> = no update possible (nothing installed
    /// in <paramref name="language"/>, a binary patch pack without strings); a failure to read or download it is the
    /// result's problem. Never throws but for cancellation.
    /// </summary>
    Task<UpdateBaseLoad?> LoadAsync(Game game, string language, CancellationToken cancellationToken);
}

/// <summary>The real <see cref="IUpdateBases"/>.</summary>
public sealed class UpdateBases(
    InstalledPackStore installed,
    LocalPackStore localPacks,
    PackCache cache,
    PackIndex packIndex,
    PackFetcher fetcher,
    ILogger<UpdateBases>? logger = null) : IUpdateBases
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public async Task<UpdateBaseLoad?> LoadAsync(Game game, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (installed.Find(game.Id) is not { } record || record.Language != language)
        {
            return null;
        }

        var load = await Task.Run(() => UpdateBaseLoader.Load(record, localPacks, cache), cancellationToken).ConfigureAwait(false);
        if (load is { Problem: UpdateBaseProblem.Missing } && !record.Local
            && packIndex.Current is { Location: { } location, Index: { } index }
            && index.PacksFor(game.Id, record.Language).FirstOrDefault(p => p.Id == record.PackId && p.Revision == record.Revision) is { } listed)
        {
            var fetch = await fetcher.FetchAsync(location, listed, cancellationToken: cancellationToken).ConfigureAwait(false);
            load = fetch.Pack is { } cached
                ? UpdateBaseLoader.Read(cached.Manifest, cached.Folder, null)
                : new UpdateBaseLoad(null, UpdateBaseProblem.Missing, fetch.Detail ?? fetch.Problem.ToString());
        }

        if (load.Problem is { } problem)
        {
            _logger.LogWarning("The installed pack {Pack} rev {Revision} of {Game} can't be an update's base ({Problem}): {Detail}", record.PackId, record.Revision, game.Id, problem, load.Detail);
        }

        return load.Problem == UpdateBaseProblem.NoStrings ? null : load;
    }
}
