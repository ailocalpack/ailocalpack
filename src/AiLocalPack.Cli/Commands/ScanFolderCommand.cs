using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Folders;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>toolkit scan-folder &lt;folder&gt; [--json]</c>: the games found in a folder (engine, main executable, fingerprint,
/// version) and the folders skipped with the reason. File hashes are cached in the app cache.
/// </summary>
internal static class ScanFolderCommand
{
    public static Command Build(OsEnvironment env)
    {
        var folder = new Argument<string>("folder") { Description = "A game folder, or a folder holding several games." };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var command = new Command(
            "scan-folder",
            "Find the games in a folder: engine, main executable, fingerprint and version of each, and the folders skipped with the reason.")
        {
            folder,
            json,
        };
        command.SetAction(result =>
        {
            var path = result.GetValue(folder)!;
            if (!Directory.Exists(path))
            {
                result.InvocationConfiguration.Error.WriteLine($"Folder not found: {path}");
                return 1;
            }

            var scan = new FolderRecognizer().Scan(path);
            var cache = FileHashCache.Load(AppPaths.Resolve(env).FileHashes);
            var fingerprinter = new GameFingerprinter(cache);
            var games = scan.Games.Select(g => Identify(fingerprinter, g)).ToList();
            cache.Save();
            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, scan, games);
            }
            else
            {
                WriteText(output, scan, games);
            }

            return 0;
        });
        return command;
    }

    private static Identified Identify(GameFingerprinter fingerprinter, FoundGame game)
    {
        try
        {
            return new Identified(game, fingerprinter.Fingerprint(game), fingerprinter.Version(game), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Identified(game, null, null, e.Message);
        }
    }

    private static void WriteText(TextWriter output, FolderScan scan, List<Identified> games)
    {
        if (games.Count == 0)
        {
            output.WriteLine($"No games found in {scan.Folder}");
        }
        else
        {
            output.WriteLine($"Found {Count(games.Count, "game")} in {scan.Folder}:");
        }

        foreach (var (game, id, version, error) in games)
        {
            output.WriteLine(game.RelativePath.Length == 0 ? "." : game.RelativePath);
            output.WriteLine($"  name: {game.Name}");
            output.WriteLine($"  engine: {game.Engine.Kind.DisplayName}");
            output.WriteLine($"  executable: {game.MainExecutable ?? "-"}");
            output.WriteLine(error is null ? $"  fingerprint: {id}" : $"  fingerprint: error: {error}");
            if (error is null)
            {
                output.WriteLine($"  version: {(version is null ? "-" : Count(version.FileHashes!.Count, "file"))}");
            }

            if (game.InSteamLibrary)
            {
                output.WriteLine("  steam: already in your library via Steam");
            }
        }

        if (scan.Skipped.Count > 0)
        {
            output.WriteLine($"Skipped {Count(scan.Skipped.Count, "folder")}:");
            foreach (var skipped in scan.Skipped)
            {
                output.WriteLine($"  {skipped.RelativePath}: {Reason(skipped.Reason)}");
            }
        }

        if (scan.LimitReached)
        {
            output.WriteLine("The folder is too large: the scan stopped early. Pick a narrower folder.");
        }
    }

    private static void WriteJson(TextWriter output, FolderScan scan, List<Identified> games)
    {
        var dto = new ScanFolderJson(
            scan.Folder,
            [
                .. games.Select(g => new FoundGameJson(
                    g.Game.RelativePath,
                    g.Game.Name,
                    g.Game.Engine.Kind.ToString(),
                    g.Game.Engine.Kind.DisplayName,
                    g.Game.MainExecutable,
                    g.Id?.ToString(),
                    g.Version?.FileHashes,
                    g.Game.InSteamLibrary,
                    g.Error)),
            ],
            [.. scan.Skipped.Select(s => new SkippedFolderJson(s.RelativePath, Reason(s.Reason)))],
            scan.LimitReached);
        output.WriteLine(JsonSerializer.Serialize(dto, ScanFolderJsonContext.Default.ScanFolderJson));
    }

    private static string Reason(SkipReason reason) => reason switch
    {
        SkipReason.NoGame => "no-game",
        _ => reason.ToString().ToLowerInvariant(),
    };

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private sealed record Identified(FoundGame Game, GameId? Id, GameVersion? Version, string? Error);
}

internal sealed record ScanFolderJson(
    string Folder, IReadOnlyList<FoundGameJson> Games, IReadOnlyList<SkippedFolderJson> Skipped, bool LimitReached);

internal sealed record FoundGameJson(
    string Path,
    string Name,
    string Engine,
    string EngineName,
    string? Executable,
    string? Fingerprint,
    IReadOnlyDictionary<string, string>? Version,
    bool InSteamLibrary,
    string? Error);

internal sealed record SkippedFolderJson(string Path, string Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n")]
[JsonSerializable(typeof(ScanFolderJson))]
internal sealed partial class ScanFolderJsonContext : JsonSerializerContext;
