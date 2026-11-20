using System.Text.Json;
using AiLocalPack.Core.Games;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Stores;

/// <summary>
/// Epic Games Store games (task 84): the Epic Games Launcher's manifests (<c>*.item</c>, one JSON object each: <c>AppName</c>,
/// <c>DisplayName</c>, <c>InstallLocation</c>, <c>LaunchExecutable</c>, <c>AppVersionString</c>; DLC, unfinished installs
/// and non-games such as Unreal Engine are skipped), then Heroic's Legendary list
/// (<c>heroic/legendaryConfig/legendary/installed.json</c>) and standalone Legendary's (<c>installed.json</c>): an object
/// of app name → <c>app_name</c>, <c>title</c>, <c>install_path</c>, <c>executable</c>, <c>version</c>, <c>is_dlc</c>.
/// An app name found twice keeps the first (the launcher's).
/// </summary>
public sealed class EpicStore(StoreLocations locations, ILogger<EpicStore> logger) : IGameStore
{
    public GameSource Source => GameSource.Epic;

    public bool IsSupportedOnThisOs => true;

    public StoreScan Scan()
    {
        var games = new Dictionary<GameId, StoreGame>();
        string? readFrom = null;
        string? problem = null;
        var found = false;

        foreach (var folder in locations.EpicManifestFolders())
        {
            if (!StoreFiles.Exists(folder))
            {
                continue;
            }

            found = true;
            readFrom ??= folder;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.item").Order(StringComparer.Ordinal))
                {
                    if (FromManifest(file) is { } game)
                    {
                        games.TryAdd(game.Id, game);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Can't list Epic manifests in {Folder}", folder);
                problem ??= e.Message;
            }
        }

        foreach (var (file, app) in LegendaryLists())
        {
            if (!File.Exists(file))
            {
                continue;
            }

            found = true;
            readFrom ??= file;
            if (StoreFiles.ReadJson(file) is not { ValueKind: JsonValueKind.Object } list)
            {
                logger.LogWarning("Skipping {File}: not a JSON object", file);
                problem ??= $"{file} can't be read.";
                continue;
            }

            foreach (var entry in list.EnumerateObject())
            {
                if (FromLegendary(entry.Name, entry.Value, app) is { } game)
                {
                    games.TryAdd(game.Id, game);
                }
            }
        }

        return new StoreScan(Source, found, readFrom, Sorted(games.Values), problem);
    }

    internal static IReadOnlyList<StoreGame> Sorted(IEnumerable<StoreGame> games) =>
        [.. games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id.ToString(), StringComparer.Ordinal)];

    private IEnumerable<(string File, string App)> LegendaryLists() =>
        locations.HeroicFolders().Select(f => (Path.Combine(f, "legendaryConfig", "legendary", "installed.json"), StoreApps.Heroic))
            .Concat(locations.LegendaryFolders().Select(f => (Path.Combine(f, "installed.json"), StoreApps.Legendary)));

    private StoreGame? FromManifest(string file)
    {
        if (StoreFiles.ReadJson(file) is not { ValueKind: JsonValueKind.Object } item)
        {
            logger.LogWarning("Skipping Epic manifest {File}: not a JSON object", file);
            return null;
        }

        var appName = item.String("AppName");
        if (item.Flag("bIsIncompleteInstall")
            || (item.String("MainGameAppName") is { } main && !string.Equals(main, appName, StringComparison.Ordinal))
            || (item.TryGetProperty("AppCategories", out var categories)
                && categories.ValueKind == JsonValueKind.Array
                && categories.GetArrayLength() > 0
                && !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "games")))
        {
            return null;
        }

        return Game(appName, item.String("DisplayName"), item.String("InstallLocation"), item.String("LaunchExecutable"),
            item.String("AppVersionString"), StoreApps.EpicLauncher, file);
    }

    private StoreGame? FromLegendary(string key, JsonElement entry, string app) =>
        entry.ValueKind != JsonValueKind.Object || entry.Flag("is_dlc")
            ? null
            : Game(entry.String("app_name") ?? key, entry.String("title"), entry.String("install_path"), entry.String("executable"),
                entry.String("version"), app, key);

    private StoreGame? Game(string? appName, string? name, string? installPath, string? executable, string? version, string app, string where)
    {
        if (GameId.Store(GameIdKind.Epic, appName) is not { } id || installPath is null)
        {
            logger.LogWarning("Skipping Epic game {Where}: no valid app name or install folder", where);
            return null;
        }

        var folder = Path.TrimEndingDirectorySeparator(installPath);
        return new StoreGame(
            id,
            name ?? Path.GetFileName(folder),
            folder,
            StoreFiles.LibraryOf(folder),
            StoreFiles.Version(version),
            StoreFiles.Relative(folder, executable),
            app,
            StoreFiles.Exists(folder));
    }
}
