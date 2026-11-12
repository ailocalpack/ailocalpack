using System.CommandLine;
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
/// <c>pack fetch &lt;pack&gt; [--game &lt;id&gt;] [--lang &lt;tag&gt;] [--json]</c>: downloads one registry pack into the
/// pack cache (manifest, string table, binary patch), verified against the manifest's hashes. Exit code 1 = the pack
/// isn't in the index, is ambiguous, or couldn't be fetched.
/// </summary>
internal static class PackFetchCommand
{
    private static readonly PackFetchJsonContext JsonWriter = new(new JsonSerializerOptions(PackFetchJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static Command Build(OsEnvironment env)
    {
        var packArgument = new Argument<string>("pack") { Description = "The pack ID (full), or <game id>/<language>/<pack id> (steam:620/ru/full)." };
        var game = new Option<string?>("--game") { Description = "The pack's game (steam:<app id> or fp:<fingerprint>), when the ID is used by several." };
        var lang = new Option<string?>("--lang") { Description = "The pack's language (ru, pt-BR…), when the ID is used by several." };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var fetch = new Command(
            "fetch",
            $"Download a registry pack (from {RegistryLocation.IndexUrlVariable} if set) into the cache, checking every file's hash. A cached revision isn't downloaded again; offline, the cached copy is used.")
        {
            packArgument, game, lang, json,
        };
        fetch.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (!TryReadFilter(result.GetValue(packArgument)!, result.GetValue(game), result.GetValue(lang), out var filter, out var filterError))
            {
                await error.WriteLineAsync(filterError);
                return 1;
            }

            var paths = AppPaths.Resolve(env);
            var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
            if (!RegistryLocation.TryResolve(env, settings.RegistryIndexUrl, out var location, out var locationError))
            {
                await error.WriteLineAsync(locationError);
                return 1;
            }

            using var http = IndexCommand.CreateHttpClient();
            var snapshot = await new RegistryIndexClient(http, paths).FetchAsync(location, cancellationToken);
            if (snapshot.Index is not { } index)
            {
                await error.WriteLineAsync($"Can't read the registry at {location} ({snapshot.Detail}) and there is no saved copy yet.");
                return 1;
            }

            var matches = index.Packs
                .Where(p => p.Id == filter.Id && (filter.Game is null || p.Game == filter.Game) && (filter.Language is null || p.Language == filter.Language))
                .OrderBy(p => p.Game.ToString(), StringComparer.Ordinal)
                .ThenBy(p => p.Language, StringComparer.Ordinal)
                .ToList();
            if (matches.Count == 0)
            {
                await error.WriteLineAsync($"The registry has no pack {filter}.");
                return 1;
            }

            if (matches.Count > 1)
            {
                await error.WriteLineAsync($"Several packs are called '{filter.Id}'; name one with --game / --lang or as <game>/<language>/<id>:");
                foreach (var match in matches)
                {
                    await error.WriteLineAsync($"  {Key(match)}");
                }

                return 1;
            }

            var pack = matches[0];
            using var files = PackFetcher.CreateHttpClient(new System.Net.Http.Headers.ProductInfoHeaderValue("ailocalpack-cli", typeof(PackFetchCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            var fetcher = new PackFetcher(new PackManifestClient(http), files, new PackCache(paths));
            var fetched = await fetcher.FetchAsync(location, pack, cancellationToken: cancellationToken);
            if (result.GetValue(json))
            {
                WriteJson(output, pack, fetched);
            }
            else if (fetched.Pack is { } cached)
            {
                await output.WriteLineAsync(fetched switch
                {
                    { FromCache: true, Detail: { } offline } => $"{Key(pack)} rev {cached.Manifest.Revision}: the registry can't be reached ({offline}); using the cached copy.",
                    { FromCache: true } => $"{Key(pack)} rev {cached.Manifest.Revision}: already in the cache.",
                    _ => $"{Key(pack)} rev {cached.Manifest.Revision}: downloaded {fetched.Downloaded:N0} bytes.",
                });
                await output.WriteLineAsync($"  folder:   {cached.Folder}");
                if (cached.StringsFile is { } strings)
                {
                    await output.WriteLineAsync($"  strings:  {strings}");
                }

                if (cached.PatchFile is { } patch)
                {
                    await output.WriteLineAsync($"  patch:    {patch}");
                }
            }

            if (fetched.Pack is null)
            {
                await error.WriteLineAsync($"Can't fetch {Key(pack)}: {Explain(fetched.Problem)} ({fetched.Detail})");
                foreach (var formatError in fetched.Errors)
                {
                    await error.WriteLineAsync($"  {formatError}");
                }

                return 1;
            }

            return 0;
        });

        return fetch;
    }

    private static bool TryReadFilter(string text, string? gameText, string? langText, out PackFilter filter, out string error)
    {
        filter = null!;
        error = "";
        if (text.Contains('/', StringComparison.Ordinal))
        {
            if (!RegistryPackKey.TryParse(text, out var key))
            {
                error = $"'{text}' is not a pack address (<game id>/<language>/<pack id>, e.g. steam:620/ru/full).";
                return false;
            }

            filter = new PackFilter(key.Id, key.Game, key.Language);
            return true;
        }

        if (!PackManifest.IsValidId(text))
        {
            error = $"'{text}' is not a pack ID (lowercase letters, digits and '-').";
            return false;
        }

        GameId? game = null;
        if (gameText is not null && !GameId.TryParse(gameText, out game))
        {
            error = $"'{gameText}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).";
            return false;
        }

        string? language = null;
        if (langText is not null)
        {
            language = GameLanguages.Tags.FirstOrDefault(t => string.Equals(t, langText, StringComparison.OrdinalIgnoreCase));
            if (language is null)
            {
                error = $"'{langText}' is not a supported language (one of {string.Join(", ", GameLanguages.Tags)}).";
                return false;
            }
        }

        filter = new PackFilter(text, game, language);
        return true;
    }

    private static string Explain(PackFetchProblem problem) => problem switch
    {
        PackFetchProblem.Offline => "the registry can't be reached and the pack isn't cached",
        PackFetchProblem.Timeout => "the server didn't answer in time and the pack isn't cached",
        PackFetchProblem.HttpError => "the server answered with an error",
        PackFetchProblem.FileMissing => "a file of the pack is missing from the registry",
        PackFetchProblem.InvalidManifest => "the manifest is not valid",
        PackFetchProblem.NewerFormat => "the manifest is in a newer format; update the app",
        PackFetchProblem.Mismatch => "the manifest is for another pack than the index lists",
        PackFetchProblem.HashMismatch => "a downloaded file doesn't match the manifest's hash and was deleted",
        PackFetchProblem.InvalidStrings => "the string table can't be read and was deleted",
        PackFetchProblem.DiskError => "the cache folder can't be written",
        _ => problem.ToString(),
    };

    private static void WriteJson(TextWriter output, RegistryPack pack, PackFetchResult fetched)
    {
        var dto = new PackFetchJson(
            Key(pack),
            fetched.Pack?.Manifest.Revision ?? pack.Revision,
            fetched.Pack is null ? JsonNamingPolicy.KebabCaseLower.ConvertName(fetched.Problem.ToString()) : null,
            fetched.Detail,
            fetched.FromCache,
            fetched.Downloaded,
            fetched.Pack?.Folder,
            fetched.Pack?.ManifestFile,
            fetched.Pack?.StringsFile,
            fetched.Pack?.PatchFile);
        output.WriteLine(JsonSerializer.Serialize(dto, JsonWriter.PackFetchJson));
    }

    private static string Key(RegistryPack pack) => $"{pack.Game}/{pack.Language}/{pack.Id}";

    private sealed record PackFilter(string Id, GameId? Game, string? Language)
    {
        public override string ToString() => Game is null && Language is null ? $"'{Id}'" : $"{Game?.ToString() ?? "*"}/{Language ?? "*"}/{Id}";
    }
}

internal sealed record PackFetchJson(
    string Pack,
    int Revision,
    string? Problem,
    string? Detail,
    bool FromCache,
    long Downloaded,
    string? Folder,
    string? Manifest,
    string? Strings,
    string? Patch);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n")]
[JsonSerializable(typeof(PackFetchJson))]
internal sealed partial class PackFetchJsonContext : JsonSerializerContext;
