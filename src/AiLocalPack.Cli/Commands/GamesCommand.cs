using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Stores;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary><c>games list [--json]</c>: the game catalog after a fresh scan of Steam and the other stores.</summary>
internal static class GamesCommand
{
    public static Command Build(OsEnvironment env)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON instead of a table." };
        var list = new Command(
            "list",
            "Scan Steam and the other stores (Epic Games, GOG, EA app), update the game catalog and list its games with ID, build, engine, anti-cheat, folder and last played time.") { json };
        list.SetAction(result =>
        {
            var paths = AppPaths.Resolve(env);
            // The library folders the player added or removed in Settings → Libraries count here too.
            var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
            var locator = new SteamLocator(env);
            var steamOn = settings.IsStoreEnabled(GameSource.Steam);
            var scan = steamOn ? new SteamLibraryScanner(locator).Scan(settings.SteamLibraries) : SteamScan.Empty;
            var stores = GameStores.Create(new StoreLocations(env))
                .Where(s => s.IsSupportedOnThisOs && settings.IsStoreEnabled(s.Source))
                .Select(s => s.Scan())
                .ToList();
            var catalog = new GameCatalog(paths, NullLogger<GameCatalog>.Instance, TimeProvider.System);
            catalog.Load();
            var changes = catalog.Refresh(new StoreScans(
                steamOn ? scan : null, stores, GameSources.Stores.Where(s => !settings.IsStoreEnabled(s)).ToHashSet()));
            // The registry's anti-cheat rules (local registry or the app's saved pack list, no network) on top of the bundled ones.
            var policy = AntiCheatPolicy.Bundled.WithRegistry(DevCommand.SavedIndex(env, paths));
            new EngineDetector().UpdateCatalog(catalog, changes);
            new AntiCheatDetector(policy).UpdateCatalog(catalog, changes);
            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, scan, stores, catalog.Games, policy, ReadLanguages(locator, scan));
            }
            else
            {
                WriteTable(output, scan, stores, catalog.Games, policy);
            }

            return 0;
        });

        return new Command("games", "Installed games.") { list };
    }

    /// <summary>The games' own languages from the Steam client's app info cache (task 80; no store lookups here).</summary>
    private static SteamAppInfoLanguages ReadLanguages(SteamLocator locator, SteamScan scan)
    {
        var file = locator.FindRoots().Select(SteamAppInfo.FileIn).FirstOrDefault(File.Exists);
        return file is null ? SteamAppInfoLanguages.Missing : SteamAppInfo.ReadLanguages(file, [.. scan.Games.Select(g => g.AppId)]);
    }

    private static void WriteJson(TextWriter output, SteamScan scan, IReadOnlyList<StoreScan> stores, IReadOnlyList<Game> games, AntiCheatPolicy policy, SteamAppInfoLanguages languages)
    {
        var dto = new GamesListJson(
            [.. scan.Libraries.Select(l => new LibraryJson(l.Path, l.IsAvailable, l.IsAddedByHand))],
            [.. games.Select(g => new GameJson(
                g.Id.ToString(),
                GameSources.Name(g.Source),
                g.Id.SteamAppId,
                g.Name,
                g.Version?.BuildId,
                g.Engine,
                g.AntiCheat,
                policy.Resolve(g).IsBlocked,
                g.InstallPath,
                g.LibraryPath,
                g.LastPlayed?.ToUniversalTime(),
                g.IsAvailable,
                g.Id.SteamAppId is { } app ? scan.Games.FirstOrDefault(s => s.AppId == app)?.Language : null,
                g.Id.SteamAppId is { } id && languages.Apps.TryGetValue(id, out var own)
                    ? [.. own.Select(l => new GameLanguageJson(l.Tag, l.Interface, l.Subtitles, l.FullAudio))]
                    : null,
                g.App))],
            [.. stores.Select(s => new StoreJson(GameSources.Name(s.Source), s.Found, s.ReadFrom, s.Games.Count, s.Problem))]);
        output.WriteLine(JsonSerializer.Serialize(dto, CliJsonContext.Default.GamesListJson));
    }

    private static void WriteTable(TextWriter output, SteamScan scan, IReadOnlyList<StoreScan> stores, IReadOnlyList<Game> games, AntiCheatPolicy policy)
    {
        var heading = new List<string>();
        if (!scan.SteamFound)
        {
            heading.Add("Steam not found.");
        }

        foreach (var store in stores.Where(s => s.Found))
        {
            heading.Add(store.Problem is { } problem
                ? $"{GameSources.EnglishName(store.Source)}: {problem}"
                : $"{GameSources.EnglishName(store.Source)}: {store.Games.Count} games ({store.ReadFrom}).");
        }

        if (heading.Count > 0)
        {
            heading.ForEach(output.WriteLine);
            if (games.Count == 0)
            {
                return;
            }

            output.WriteLine();
        }

        string[] header = ["ID", "BUILD", "ENGINE", "ANTI-CHEAT", "LAST PLAYED", "NAME", "FOLDER"];
        var rows = games.Select(g => new[]
        {
            g.Id.ToString(),
            g.Version?.BuildId ?? (g.Version is null ? "-" : "files"),
            EngineKind.TryParse(g.Engine, out var engine) ? engine.DisplayName : "-",
            AntiCheatCell(policy.Resolve(g)),
            g.LastPlayed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never",
            g.Name,
            g.IsAvailable ? g.InstallPath : $"{g.InstallPath} (not available)",
        }).ToList();

        if (rows.Count == 0)
        {
            output.WriteLine("No installed games.");
        }
        else
        {
            var widths = header.Select((h, i) => rows.Max(r => r[i].Length) is var w && w > h.Length ? w : h.Length).ToArray();
            output.WriteLine(Row(header, widths));
            foreach (var row in rows)
            {
                output.WriteLine(Row(row, widths));
            }
        }

        if (scan.Libraries.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine("Libraries:");
        foreach (var library in scan.Libraries)
        {
            var notes = string.Join(", ", new[] { library.IsAddedByHand ? "added by hand" : null, library.IsAvailable ? null : "not available" }.OfType<string>());
            output.WriteLine(notes.Length == 0 ? $"  {library.Path}" : $"  {library.Path} ({notes})");
        }
    }

    private static string AntiCheatCell(AntiCheatVerdict verdict) => verdict switch
    {
        { AntiCheatId: null } => "-",
        { IsAllowed: true } => $"{verdict.Name} (allowed)",
        _ => verdict.Name!,
    };

    /// <summary>Columns padded to <paramref name="widths"/>, the last one left as is.</summary>
    private static string Row(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i])));
}

internal sealed record GamesListJson(IReadOnlyList<LibraryJson> Libraries, IReadOnlyList<GameJson> Games, IReadOnlyList<StoreJson> Stores);

/// <summary>A store beside Steam that was scanned (task 83): found or not, what was read, how many games, a problem.</summary>
internal sealed record StoreJson(string Source, bool Found, string? ReadFrom, int Games, string? Problem);

internal sealed record LibraryJson(string Path, bool Available, bool AddedByHand);

internal sealed record GameJson(
    string Id,
    string Source,
    uint? AppId,
    string Name,
    string? BuildId,
    string? Engine,
    string? AntiCheat,
    bool Blocked,
    string InstallPath,
    string? LibraryPath,
    DateTimeOffset? LastPlayed,
    bool Available,
    string? SteamLanguage,
    IReadOnlyList<GameLanguageJson>? Languages,
    string? App);

/// <summary>A language the game ships (task 80): from the Steam client's app info.</summary>
internal sealed record GameLanguageJson(string Tag, bool Interface, bool Subtitles, bool FullAudio);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n")]
[JsonSerializable(typeof(GamesListJson))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
