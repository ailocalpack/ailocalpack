using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>index show [--game &lt;id&gt;] [--lang &lt;tag&gt;] [--json]</c>: the registry index, fetched or from the saved copy.
/// <c>index build &lt;registry root&gt; --out &lt;file&gt; [--ratings &lt;file&gt;] [--generated &lt;time&gt;] [--github]</c>:
/// <c>index.json</c> from a registry checkout, as the registry's Pages workflow runs it.
/// </summary>
internal static class IndexCommand
{
    // Titles in any script stay readable.
    private static readonly IndexJsonContext JsonWriter = new(new JsonSerializerOptions(IndexJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static Command Build(OsEnvironment env)
    {
        var game = new Option<string?>("--game") { Description = "Only this game (steam:<app id> or fp:<fingerprint>)." };
        var lang = new Option<string?>("--lang") { Description = "Only packs into this language (ru, pt-BR, zh-Hans…)." };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of a table." };
        var show = new Command(
            "show",
            $"Read the registry index (from {RegistryLocation.IndexUrlVariable} if set) and list its packs; offline, the saved copy is shown with its date.")
        {
            game, lang, json,
        };
        show.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            GameId? gameFilter = null;
            if (result.GetValue(game) is { } gameText && !GameId.TryParse(gameText, out gameFilter))
            {
                await error.WriteLineAsync($"'{gameText}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
                return 1;
            }

            string? langFilter = null;
            if (result.GetValue(lang) is { } langText)
            {
                langFilter = GameLanguages.Tags.FirstOrDefault(t => string.Equals(t, langText, StringComparison.OrdinalIgnoreCase));
                if (langFilter is null)
                {
                    await error.WriteLineAsync($"'{langText}' is not a supported language (one of {string.Join(", ", GameLanguages.Tags)}).");
                    return 1;
                }
            }

            var paths = AppPaths.Resolve(env);
            var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
            if (!RegistryLocation.TryResolve(env, settings.RegistryIndexUrl, out var location, out var locationError))
            {
                await error.WriteLineAsync(locationError);
                return 1;
            }

            using var http = CreateHttpClient();
            var snapshot = await new RegistryIndexClient(http, paths).FetchAsync(location, cancellationToken);
            foreach (var skipped in snapshot.Skipped)
            {
                await error.WriteLineAsync($"warning: left out of the index: {skipped}");
            }

            if (snapshot.Index is not { } index)
            {
                await error.WriteLineAsync(snapshot.Problem == RegistryIndexProblem.LocalFileMissing
                    ? $"Can't read the registry: {snapshot.Detail}"
                    : $"Can't read the registry at {location} ({snapshot.Detail}) and there is no saved copy yet.");
                return 1;
            }

            var games = index.Games.Values
                .Where(g => gameFilter is null || g.Id == gameFilter)
                .OrderBy(g => g.Id.ToString(), StringComparer.Ordinal)
                .ToList();
            if (result.GetValue(json))
            {
                WriteJson(output, snapshot, index, games, langFilter);
            }
            else
            {
                WriteTable(output, snapshot, index, games, langFilter);
            }

            return 0;
        });

        return new Command("index", "The registry index of packs.") { show, BuildBuild() };
    }

    private static Command BuildBuild()
    {
        var root = new Argument<string>("registry-root") { Description = "The registry checkout (holds games/ and policy/)." };
        var outFile = new Option<string>("--out") { Description = "Where to write index.json.", Required = true };
        var ratingsFile = new Option<string?>("--ratings") { Description = "ratings.json with likes, works / doesn't work counts and discussion links per pack." };
        var generatedOption = new Option<string?>("--generated")
        {
            Description = "Build time to record (RFC 3339, e.g. 2026-11-04T12:00:00Z); default now. For reproducible output.",
        };
        var github = new Option<bool>("--github") { Description = "Print warnings as GitHub Actions annotations (::warning file=...)." };
        var build = new Command(
            "build",
            "Build index.json from a registry checkout: every valid pack under games/, opt-out and anti-cheat flags from policy/, ratings. Invalid packs are left out with a warning.")
        {
            root, outFile, ratingsFile, generatedOption, github,
        };
        build.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var rootPath = Path.GetFullPath(result.GetValue(root)!);
            var annotate = result.GetValue(github);

            var generated = DateTimeOffset.UtcNow;
            if (result.GetValue(generatedOption) is { } generatedText
                && !DateTimeOffset.TryParse(generatedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out generated))
            {
                error.WriteLine($"'{generatedText}' is not a date and time (e.g. 2026-11-04T12:00:00Z).");
                return Task.FromResult(1);
            }

            IReadOnlyDictionary<RegistryPackKey, RegistryPackRating>? ratings = null;
            if (result.GetValue(ratingsFile) is { } ratingsPath)
            {
                string text;
                try
                {
                    text = File.ReadAllText(ratingsPath);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"Can't read the ratings {ratingsPath}: {e.Message}");
                    return Task.FromResult(1);
                }

                (ratings, var ratingErrors) = RegistryRatings.Read(text);
                if (ratings is null)
                {
                    error.WriteLine($"{ratingsPath} is not a valid ratings file:");
                    foreach (var problem in ratingErrors)
                    {
                        error.WriteLine($"  {problem}");
                    }

                    return Task.FromResult(1);
                }
            }

            var built = RegistryIndexBuilder.Build(rootPath, generated, ratings, cancellationToken);
            if (built.Index is not { } index)
            {
                error.WriteLine($"Can't build the index of {rootPath}:");
                foreach (var problem in built.Problems)
                {
                    error.WriteLine(annotate ? Annotation("error", rootPath, problem) : $"  {problem}");
                }

                return Task.FromResult(1);
            }

            foreach (var problem in built.Problems)
            {
                error.WriteLine(annotate ? Annotation("warning", rootPath, problem) : $"warning: {problem}");
            }

            var outPath = result.GetValue(outFile)!;
            var json = RegistryIndexSerializer.Write(index).ReplaceLineEndings("\n") + "\n";
            try
            {
                AtomicFile.WriteAllBytes(outPath, System.Text.Encoding.UTF8.GetBytes(json));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Can't write {outPath}: {e.Message}");
                return Task.FromResult(1);
            }

            output.WriteLine($"Wrote {outPath}: {index.Games.Count} games, {index.PackCount} packs"
                + (built.Problems.Count > 0 ? $", {built.Problems.Count} warning{(built.Problems.Count == 1 ? "" : "s")}." : "."));
            return Task.FromResult(0);
        });
        return build;
    }

    private static string Annotation(string level, string root, RegistryBuildProblem problem) =>
        GitHubAnnotations.Format(level, problem.File.Length > 0 ? Path.Combine(root, problem.File) : null, problem.Field, problem.Message);

    /// <summary>Same settings as the app's client: decompression, a user agent, no overall timeout (the index client times each try).</summary>
    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ailocalpack-cli", typeof(IndexCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
        return http;
    }

    private static IEnumerable<RegistryPack> PacksOf(RegistryGame game, string? language) =>
        game.Packs
            .Where(p => language is null || p.Key == language)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => p.Value.OrderBy(pack => pack.Id, StringComparer.Ordinal));

    private static void WriteTable(TextWriter output, RegistryIndexSnapshot snapshot, RegistryIndex index, IReadOnlyList<RegistryGame> games, string? language)
    {
        output.WriteLine($"Registry: {snapshot.Location}");
        output.WriteLine($"Generated {Time(index.Generated)}, {(snapshot.IsStale ? "saved" : "fetched")} {Time(snapshot.FetchedAt)}.");
        if (snapshot.IsStale)
        {
            output.WriteLine($"Offline ({snapshot.Detail}): showing the saved copy from {Time(snapshot.FetchedAt)}.");
        }

        output.WriteLine();
        string[] header = ["GAME", "LANG", "PACK", "STATUS", "REV", "COVERAGE", "LIKES", "WORKS", "BUILDS", "AUTHOR", "TITLE"];
        var rows = games.SelectMany(g => PacksOf(g, language)).Select(p => new[]
        {
            p.Game.ToString(),
            p.Language,
            p.Id,
            PackManifestSerializer.StatusText(p.Status),
            p.Revision.ToString(CultureInfo.InvariantCulture),
            p.Coverage.Ratio.ToString("0%", CultureInfo.InvariantCulture),
            p.Rating.Likes.ToString(CultureInfo.InvariantCulture),
            $"{p.Rating.Works}/{p.Rating.DoesNotWork}",
            p.BuildIds.Count > 0 ? string.Join(",", p.BuildIds) : $"{p.BuildCount} file set{(p.BuildCount == 1 ? "" : "s")}",
            p.Author.GitHub,
            p.Title,
        }).ToList();

        if (rows.Count == 0)
        {
            output.WriteLine("No packs.");
        }
        else
        {
            var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
            output.WriteLine(Row(header, widths));
            foreach (var row in rows)
            {
                output.WriteLine(Row(row, widths));
            }
        }

        var flagged = games.Where(g => g.OptOut || g.AntiCheatAllowed).ToList();
        if (flagged.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Game flags:");
            foreach (var game in flagged)
            {
                var flags = new[] { game.OptOut ? "opt-out" : null, game.AntiCheatAllowed ? "anti-cheat allowed" : null }.OfType<string>();
                output.WriteLine($"  {game.Id}: {string.Join(", ", flags)}");
            }
        }
    }

    private static void WriteJson(TextWriter output, RegistryIndexSnapshot snapshot, RegistryIndex index, IReadOnlyList<RegistryGame> games, string? language)
    {
        var location = snapshot.Location!;
        var dto = new IndexShowJson(
            location.ToString(),
            snapshot.IsStale ? "stale" : "fresh",
            snapshot.Problem == RegistryIndexProblem.None ? null : JsonNamingPolicy.KebabCaseLower.ConvertName(snapshot.Problem.ToString()),
            snapshot.Detail,
            snapshot.FetchedAt?.ToUniversalTime(),
            index.Generated.ToUniversalTime(),
            [.. games.Select(g => new IndexGameJson(
                g.Id.ToString(),
                g.OptOut,
                g.AntiCheatAllowed,
                [.. PacksOf(g, language).Select(p => new IndexPackJson(
                    p.Language,
                    p.Id,
                    p.Title,
                    p.Author.GitHub,
                    PackManifestSerializer.StatusText(p.Status),
                    p.Revision,
                    p.Updated.ToUniversalTime(),
                    p.Coverage.Translated,
                    p.Coverage.Total,
                    p.BuildIds,
                    p.BuildCount,
                    location.Resolve(p.ManifestPath).ToString(),
                    p.Size,
                    p.Rating.Likes,
                    p.Rating.Works,
                    p.Rating.DoesNotWork,
                    p.Discussion?.AbsoluteUri))]))]);
        output.WriteLine(JsonSerializer.Serialize(dto, JsonWriter.IndexShowJson));
    }

    private static string Time(DateTimeOffset? time) =>
        time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never";

    private static string Row(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i])));
}

internal sealed record IndexShowJson(
    string Source,
    string State,
    string? Problem,
    string? Detail,
    DateTimeOffset? FetchedAt,
    DateTimeOffset Generated,
    IReadOnlyList<IndexGameJson> Games);

internal sealed record IndexGameJson(string Id, bool OptOut, bool AntiCheatAllowed, IReadOnlyList<IndexPackJson> Packs);

internal sealed record IndexPackJson(
    string Language,
    string Id,
    string Title,
    string Author,
    string Status,
    int Revision,
    DateTimeOffset Updated,
    int Translated,
    int Total,
    IReadOnlyList<string> BuildIds,
    int BuildCount,
    string Manifest,
    long Size,
    int Likes,
    int Works,
    int DoesNotWork,
    string? Discussion);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IndexShowJson))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;
