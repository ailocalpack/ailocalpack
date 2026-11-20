using System.Text.Json;
using AiLocalPack.Core.Games;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Stores;

/// <summary>
/// GOG games (task 85): on Windows the registry GOG Galaxy and GOG's offline installers write
/// (<c>HKLM\Software\GOG.com\Games\&lt;product id&gt;</c>, 32-bit view: <c>gameName</c>, <c>path</c>, <c>buildId</c>;
/// <c>dependsOn</c> set = DLC), on macOS the app bundles in <c>/Applications</c> and <c>~/Applications</c> that hold a
/// <c>Contents/Resources/goggame-&lt;id&gt;.info</c>, on every OS Heroic's list (<c>heroic/gog_store/installed.json</c>:
/// <c>{ "installed": [ { appName, install_path, executable, version, buildId, is_dlc } ] }</c>). The game folder's
/// <c>goggame-&lt;id&gt;.info</c> (<see cref="GogInfo"/>) adds the name and main executable. A product ID found twice keeps
/// the first.
/// </summary>
public sealed class GogStore(StoreLocations locations, ILogger<GogStore> logger) : IGameStore
{
    internal const string GamesKey = @"Software\GOG.com\Games";

    public GameSource Source => GameSource.Gog;

    public bool IsSupportedOnThisOs => true;

    public StoreScan Scan()
    {
        var games = new Dictionary<GameId, StoreGame>();
        string? readFrom = null;
        string? problem = null;
        var found = false;

        var registry = locations.Registry;
        var ids = registry.SubKeyNames(RegistryHiveKind.LocalMachine, GamesKey, registry32: true);
        if (ids.Count > 0)
        {
            found = true;
            readFrom = $@"HKEY_LOCAL_MACHINE\{GamesKey}";
            foreach (var key in ids)
            {
                if (FromRegistry(key) is { } game)
                {
                    games.TryAdd(game.Id, game);
                }
            }
        }

        foreach (var folder in locations.GogAppFolders())
        {
            foreach (var game in FromBundles(folder))
            {
                found = true;
                readFrom ??= folder;
                games.TryAdd(game.Id, game);
            }
        }

        foreach (var heroic in locations.HeroicFolders())
        {
            var file = Path.Combine(heroic, "gog_store", "installed.json");
            if (!File.Exists(file))
            {
                continue;
            }

            found = true;
            readFrom ??= file;
            if (StoreFiles.ReadJson(file) is not { } list
                || !list.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Skipping {File}: no list of installed games", file);
                problem ??= $"{file} can't be read.";
                continue;
            }

            foreach (var entry in installed.EnumerateArray())
            {
                if (FromHeroic(entry) is { } game)
                {
                    games.TryAdd(game.Id, game);
                }
            }
        }

        return new StoreScan(Source, found, readFrom, EpicStore.Sorted(games.Values), problem);
    }

    private StoreGame? FromRegistry(string key)
    {
        var registry = locations.Registry;
        var path = $@"{GamesKey}\{key}";
        string? Value(string name) => registry.GetString(RegistryHiveKind.LocalMachine, path, name, registry32: true);
        if (!string.IsNullOrWhiteSpace(Value("dependsOn")))
        {
            return null;
        }

        var id = GameId.Store(GameIdKind.Gog, Value("gameID") ?? key);
        var folder = Value("path");
        if (id is null || string.IsNullOrWhiteSpace(folder))
        {
            logger.LogWarning("Skipping GOG registry entry {Key}: no product ID or path", key);
            return null;
        }

        folder = Path.TrimEndingDirectorySeparator(folder.Trim());
        var info = GogInfo.Read(folder, id.Value);
        if (info?.IsDlc == true)
        {
            return null;
        }

        return new StoreGame(
            id,
            Value("gameName") ?? info?.Name ?? Path.GetFileName(folder),
            folder,
            StoreFiles.LibraryOf(folder),
            StoreFiles.Version(Value("buildId") ?? info?.BuildId),
            info?.MainExecutable ?? StoreFiles.Relative(folder, Value("exe")),
            StoreApps.GogGalaxy,
            StoreFiles.Exists(folder));
    }

    private IEnumerable<StoreGame> FromBundles(string folder)
    {
        IEnumerable<string> bundles;
        try
        {
            bundles = StoreFiles.Exists(folder) ? [.. Directory.EnumerateDirectories(folder, "*.app").Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Can't list app bundles in {Folder}", folder);
            yield break;
        }

        foreach (var bundle in bundles)
        {
            var resources = Path.Combine(bundle, "Contents", "Resources");
            if (GogInfo.Find(resources) is not { IsDlc: false } info || GameId.Store(GameIdKind.Gog, info.GameId) is not { } id)
            {
                continue;
            }

            yield return new StoreGame(
                id,
                info.Name ?? Path.GetFileNameWithoutExtension(bundle),
                bundle,
                folder,
                StoreFiles.Version(info.BuildId),
                MainExecutable: null,
                StoreApps.GogGalaxy);
        }
    }

    private StoreGame? FromHeroic(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || entry.Flag("is_dlc"))
        {
            return null;
        }

        var id = GameId.Store(GameIdKind.Gog, entry.String("appName"));
        var folder = entry.String("install_path");
        if (id is null || folder is null)
        {
            logger.LogWarning("Skipping a Heroic GOG entry: no product ID or install path");
            return null;
        }

        folder = Path.TrimEndingDirectorySeparator(folder);
        var info = GogInfo.Read(folder, id.Value) ?? GogInfo.Read(Path.Combine(folder, "Contents", "Resources"), id.Value);
        return new StoreGame(
            id,
            info?.Name ?? Path.GetFileName(folder),
            folder,
            StoreFiles.LibraryOf(folder),
            StoreFiles.Version(entry.String("buildId") ?? entry.String("version") ?? info?.BuildId),
            StoreFiles.Relative(folder, entry.String("executable")) ?? info?.MainExecutable,
            StoreApps.Heroic,
            StoreFiles.Exists(folder));
    }
}

/// <summary>
/// A GOG game's <c>goggame-&lt;id&gt;.info</c> (JSON in the game folder): <c>gameId</c>, <c>rootGameId</c> (another ID =
/// DLC), <c>name</c>, <c>buildId</c>, <c>playTasks</c> (the <c>isPrimary</c> one's <c>path</c> = the main executable).
/// </summary>
public sealed record GogInfo(string GameId, string? Name, string? BuildId, string? MainExecutable, bool IsDlc)
{
    /// <summary><c>goggame-&lt;id&gt;.info</c> in <paramref name="folder"/>; <c>null</c> when missing or unreadable.</summary>
    public static GogInfo? Read(string folder, string id) => Parse(Path.Combine(folder, $"goggame-{id}.info"), folder);

    /// <summary>The first base-game <c>goggame-*.info</c> in <paramref name="folder"/>; <c>null</c> when none.</summary>
    public static GogInfo? Find(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            var infos = Directory.EnumerateFiles(folder, "goggame-*.info").Order(StringComparer.Ordinal)
                .Select(f => Parse(f, folder)).OfType<GogInfo>().ToList();
            return infos.FirstOrDefault(i => !i.IsDlc) ?? infos.FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static GogInfo? Parse(string file, string folder)
    {
        if (StoreFiles.ReadJson(file) is not { ValueKind: JsonValueKind.Object } info || info.String("gameId") is not { } id)
        {
            return null;
        }

        string? main = null;
        if (info.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            var primary = tasks.EnumerateArray().FirstOrDefault(t => t.Flag("isPrimary"));
            main = StoreFiles.Relative(folder, primary.String("path"));
        }

        var root = info.String("rootGameId");
        return new GogInfo(id, info.String("name"), info.String("buildId"), main, root is not null && root != id);
    }
}
