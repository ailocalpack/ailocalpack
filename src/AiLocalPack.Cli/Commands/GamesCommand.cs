using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.AntiCheat;
using AiLocalPack.Toolkit.Engines;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary><c>games list [--json]</c>: the game catalog after a fresh Steam scan.</summary>
internal static class GamesCommand
{
    public static Command Build(OsEnvironment env)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON instead of a table." };
        var list = new Command(
            "list",
            "Scan Steam, update the game catalog and list its games with ID, build, engine, anti-cheat, folder and last played time.") { json };
        list.SetAction(result =>
        {
            var paths = AppPaths.Resolve(env);
            // The library folders the player added or removed in Settings → Libraries count here too.
            var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
            var scan = new SteamLibraryScanner(new SteamLocator(env)).Scan(settings.SteamLibraries);
            var catalog = new GameCatalog(paths, NullLogger<GameCatalog>.Instance, TimeProvider.System);
            catalog.Load();
            var changes = catalog.Refresh(scan);
            // The registry's anti-cheat rules (local registry or the app's saved pack list, no network) on top of the bundled ones.
            var policy = AntiCheatPolicy.Bundled.WithRegistry(DevCommand.SavedIndex(env, paths));
            new EngineDetector().UpdateCatalog(catalog, changes);
            new AntiCheatDetector(policy).UpdateCatalog(catalog, changes);
            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, scan, catalog.Games, policy);
            }
            else
            {
                WriteTable(output, scan, catalog.Games, policy);
            }

            return 0;
        });

        return new Command("games", "Installed games.") { list };
    }

    private static void WriteJson(TextWriter output, SteamScan scan, IReadOnlyList<Game> games, AntiCheatPolicy policy)
    {
        var dto = new GamesListJson(
            [.. scan.Libraries.Select(l => new LibraryJson(l.Path, l.IsAvailable, l.IsAddedByHand))],
            [.. games.Select(g => new GameJson(
                g.Id.ToString(),
                g.Source.ToString().ToLowerInvariant(),
                g.Id.SteamAppId,
                g.Name,
                g.Version?.BuildId,
                g.Engine,
                g.AntiCheat,
                policy.Resolve(g).IsBlocked,
                g.InstallPath,
                g.LibraryPath,
                g.LastPlayed?.ToUniversalTime(),
                g.IsAvailable))]);
        output.WriteLine(JsonSerializer.Serialize(dto, CliJsonContext.Default.GamesListJson));
    }

    private static void WriteTable(TextWriter output, SteamScan scan, IReadOnlyList<Game> games, AntiCheatPolicy policy)
    {
        if (!scan.SteamFound)
        {
            output.WriteLine("Steam not found.");
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

internal sealed record GamesListJson(IReadOnlyList<LibraryJson> Libraries, IReadOnlyList<GameJson> Games);

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
    bool Available);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GamesListJson))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
