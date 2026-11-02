using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Commands;

/// <summary><c>games list [--json]</c>: installed games (Steam in MVP).</summary>
internal static class GamesCommand
{
    public static Command Build(OsEnvironment env)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON instead of a table." };
        var list = new Command("list", "List installed Steam games with app ID, build ID, folder and last played time.") { json };
        list.SetAction(result =>
        {
            var scan = new SteamLibraryScanner(new SteamLocator(env)).Scan();
            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, scan);
            }
            else
            {
                WriteTable(output, scan);
            }

            return 0;
        });

        return new Command("games", "Installed games.") { list };
    }

    private static void WriteJson(TextWriter output, SteamScan scan)
    {
        var dto = new GamesListJson(
            [.. scan.Libraries.Select(l => new LibraryJson(l.Path, l.IsAvailable))],
            [.. scan.Games.Select(g => new GameJson(
                $"steam:{g.AppId.ToString(CultureInfo.InvariantCulture)}",
                g.AppId,
                g.Name,
                g.BuildId,
                g.InstallPath,
                g.LibraryPath,
                g.LastPlayed?.ToUniversalTime()))]);
        output.WriteLine(JsonSerializer.Serialize(dto, CliJsonContext.Default.GamesListJson));
    }

    private static void WriteTable(TextWriter output, SteamScan scan)
    {
        if (scan.Libraries.Count == 0)
        {
            output.WriteLine("Steam not found.");
            return;
        }

        string[] header = ["APP ID", "BUILD", "LAST PLAYED", "NAME", "FOLDER"];
        var rows = scan.Games.Select(g => new[]
        {
            g.AppId.ToString(CultureInfo.InvariantCulture),
            g.BuildId ?? "-",
            g.LastPlayed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never",
            g.Name,
            g.InstallPath,
        }).ToList();

        if (rows.Count == 0)
        {
            output.WriteLine("No installed Steam games.");
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

        output.WriteLine();
        output.WriteLine("Libraries:");
        foreach (var library in scan.Libraries)
        {
            output.WriteLine(library.IsAvailable ? $"  {library.Path}" : $"  {library.Path} (not available)");
        }
    }

    /// <summary>Columns padded to <paramref name="widths"/>, the last one left as is.</summary>
    private static string Row(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i])));
}

internal sealed record GamesListJson(IReadOnlyList<LibraryJson> Libraries, IReadOnlyList<GameJson> Games);

internal sealed record LibraryJson(string Path, bool Available);

internal sealed record GameJson(
    string Id,
    uint AppId,
    string Name,
    string? BuildId,
    string InstallPath,
    string LibraryPath,
    DateTimeOffset? LastPlayed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GamesListJson))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
