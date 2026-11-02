using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
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
            var scan = new SteamLibraryScanner(new SteamLocator(env)).Scan();
            var catalog = new GameCatalog(AppPaths.Resolve(env), NullLogger<GameCatalog>.Instance, TimeProvider.System);
            catalog.Load();
            var changes = catalog.Refresh(scan);
            new EngineDetector().UpdateCatalog(catalog, changes);
            new AntiCheatDetector().UpdateCatalog(catalog, changes);
            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, scan, catalog.Games);
            }
            else
            {
                WriteTable(output, scan, catalog.Games);
            }

            return 0;
        });

        return new Command("games", "Installed games.") { list };
    }

    private static void WriteJson(TextWriter output, SteamScan scan, IReadOnlyList<Game> games)
    {
        var dto = new GamesListJson(
            [.. scan.Libraries.Select(l => new LibraryJson(l.Path, l.IsAvailable))],
            [.. games.Select(g => new GameJson(
                g.Id.ToString(),
                g.Source.ToString().ToLowerInvariant(),
                g.Id.SteamAppId,
                g.Name,
                g.Version?.BuildId,
                g.Engine,
                g.AntiCheat,
                AntiCheatPolicy.Bundled.Resolve(g).IsBlocked,
                g.InstallPath,
                g.LibraryPath,
                g.LastPlayed?.ToUniversalTime(),
                g.IsAvailable))]);
        output.WriteLine(JsonSerializer.Serialize(dto, CliJsonContext.Default.GamesListJson));
    }

    private static void WriteTable(TextWriter output, SteamScan scan, IReadOnlyList<Game> games)
    {
        if (scan.Libraries.Count == 0)
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
            AntiCheatCell(AntiCheatPolicy.Bundled.Resolve(g)),
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
            output.WriteLine(library.IsAvailable ? $"  {library.Path}" : $"  {library.Path} (not available)");
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

internal sealed record LibraryJson(string Path, bool Available);

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
