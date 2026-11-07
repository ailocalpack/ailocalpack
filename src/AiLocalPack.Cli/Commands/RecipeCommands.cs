using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>toolkit extract &lt;folder&gt; --out strings.json [--lang en]</c>: the game's strings as an editable string table
/// (originals as the values, notes as context, placeholders and length limits under <c>details</c>).
/// <c>toolkit rebuild &lt;folder&gt; --strings strings.json --out &lt;dir&gt;</c>: the game files a string table changes,
/// written into a folder laid out like the game (ready for <c>dev apply-files</c>), the font swap included when the
/// game's font lacks the table's letters (<see cref="FontFixer"/>; <c>--no-fonts</c> leaves fonts alone).
/// <c>toolkit fonts &lt;folder&gt; --strings strings.json [--json]</c>: the game's fonts against the table's letters and
/// the whitelisted font a swap uses (a pack manifest's <c>fonts</c>).
/// </summary>
internal static class RecipeCommands
{
    private const int ListedUnknownKeys = 20;

    private static readonly JsonSerializerOptions WriteOptions = new(ExtractJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Command BuildExtract(OsEnvironment env)
    {
        var folder = new Argument<string>("folder") { Description = "Game folder." };
        var outFile = new Option<string>("--out") { Description = "File to write (JSON string table).", Required = true };
        var lang = new Option<string>("--lang") { Description = "Language tag written into the table (the game's own language).", DefaultValueFactory = _ => GameLanguages.Fallback };
        var command = new Command("extract", "Extract the translatable strings of a game into a string table whose values are the original texts.")
        {
            folder, outFile, lang,
        };
        command.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var language = result.GetValue(lang)!;
            if (!GameLanguages.Tags.Contains(language, StringComparer.Ordinal))
            {
                error.WriteLine($"'{language}' is not a supported language tag (e.g. en, ru, pt-BR).");
                return Task.FromResult(1);
            }

            if (Tool(result.GetValue(folder)!, error, env) is not var (tool, path, kind))
            {
                return Task.FromResult(1);
            }

            RecipeExtraction extraction;
            try
            {
                extraction = tool.Extract(path, cancellationToken);
            }
            catch (RecipeException e)
            {
                error.WriteLine(e.Message);
                return Task.FromResult(1);
            }

            var target = Path.GetFullPath(result.GetValue(outFile)!);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, ToJson(extraction, language), WriteOptions.GetTypeInfo(typeof(ExtractFileJson)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Can't write {target}: {e.Message}");
                return Task.FromResult(1);
            }

            output.WriteLine($"Extracted {extraction.Strings.Count} strings from {extraction.Files.Count} files of {kind.DisplayName} "
                + $"({extraction.Tool} {Text(extraction.Version)}) into {target}.");
            return Task.FromResult(0);
        });
        return command;
    }

    public static Command BuildRebuild(OsEnvironment env)
    {
        var folder = new Argument<string>("folder") { Description = "Game folder (read only)." };
        var strings = new Option<string>("--strings") { Description = "String table with the translations (JSON or gzip, as 'toolkit extract' or a pack writes it).", Required = true };
        var outDir = new Option<string>("--out") { Description = "Folder for the rebuilt files, laid out like the game folder.", Required = true };
        var fonts = FontsOption();
        var noFonts = new Option<bool>("--no-fonts") { Description = "Leave the game's fonts alone even when they lack letters of the translations." };
        var command = new Command("rebuild", "Rebuild the game files a string table changes, from the game's own files, into another folder.")
        {
            folder, strings, outDir, fonts, noFonts,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (ReadTable(result.GetValue(strings)!, error) is not { } translations)
            {
                return 1;
            }

            if (Tool(result.GetValue(folder)!, error, env) is not var (tool, path, _))
            {
                return 1;
            }

            var target = Path.GetFullPath(result.GetValue(outDir)!);
            if (string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(path), StringComparison.Ordinal))
            {
                error.WriteLine("--out must not be the game folder: the rebuilt files go elsewhere and are installed with 'dev apply-files'.");
                return 1;
            }

            RebuildResult rebuilt;
            FontFixResult? fixedFonts = null;
            var rebuildOutput = new DirectoryRebuildOutput(target);
            try
            {
                rebuilt = tool.Rebuild(path, translations.Language, translations.Strings, rebuildOutput, cancellationToken);
                if (!result.GetValue(noFonts))
                {
                    fixedFonts = await CreateFixer(env).ApplyAsync(
                        tool, path, translations.Language, translations.Strings, Fonts(result.GetValue(fonts)), rebuildOutput, cancellationToken: cancellationToken);
                }
            }
            catch (RecipeException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            if (fixedFonts is { Failure: { } failure })
            {
                error.WriteLine($"The game's font lacks letters of the translations, and the font {fixedFonts.FailedFont!.Name} couldn't be had: {Kebab(failure.Problem)} ({failure.Detail}).");
                error.WriteLine("Retry when the registry's font source is reachable, or pass --no-fonts.");
                return 1;
            }

            var files = rebuilt.ChangedFiles.Union(fixedFonts?.Files.Files ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            output.WriteLine($"Rebuilt {files.Count} files ({rebuilt.Applied} strings translated) into {target}.");
            foreach (var file in files)
            {
                output.WriteLine($"  {file}");
            }

            if (fixedFonts is not null)
            {
                WriteFontLines(output, fixedFonts);
            }

            if (rebuilt.UnknownKeys.Count > 0)
            {
                error.WriteLine($"warning: {rebuilt.UnknownKeys.Count} keys are not in this game (left out):");
                foreach (var key in rebuilt.UnknownKeys.Take(ListedUnknownKeys))
                {
                    error.WriteLine($"  {key}");
                }

                if (rebuilt.UnknownKeys.Count > ListedUnknownKeys)
                {
                    error.WriteLine($"  … and {rebuilt.UnknownKeys.Count - ListedUnknownKeys} more");
                }
            }

            return 0;
        });
        return command;
    }

    public static Command BuildFonts(OsEnvironment env)
    {
        var folder = new Argument<string>("folder") { Description = "Game folder (read only)." };
        var strings = new Option<string>("--strings") { Description = "String table with the translations (JSON or gzip).", Required = true };
        var fonts = FontsOption();
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var command = new Command("fonts", "Check the game's fonts against the letters of a string table, and name the open font a swap uses (a pack manifest's fonts).")
        {
            folder, strings, fonts, json,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (ReadTable(result.GetValue(strings)!, error) is not { } translations)
            {
                return 1;
            }

            if (Tool(result.GetValue(folder)!, error, env) is not var (tool, path, kind))
            {
                return 1;
            }

            if (tool is not IGameFonts)
            {
                error.WriteLine($"{path}: the toolkit doesn't swap fonts in {kind.DisplayName} games.");
                return 1;
            }

            // The whole swap, written into a throwaway folder: it names the font the pack needs (downloading it if needed).
            var scratch = Path.Combine(Path.GetTempPath(), "ailocalpack-fonts-" + Path.GetRandomFileName());
            FontFixResult fixedFonts;
            try
            {
                fixedFonts = await CreateFixer(env).ApplyAsync(
                    tool, path, translations.Language, translations.Strings, Fonts(result.GetValue(fonts)), new DirectoryRebuildOutput(scratch), cancellationToken: cancellationToken);
            }
            catch (RecipeException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }
            finally
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }

            if (result.GetValue(json))
            {
                output.WriteLine(JsonSerializer.Serialize(FontsJson(fixedFonts), WriteOptions.GetTypeInfo(typeof(FontsFileJson))));
            }
            else
            {
                var check = fixedFonts.Check!;
                output.WriteLine($"The {translations.Language} translations add {check.Letters.Count} letters and {check.Symbols.Count} symbols to the game's text; {check.Fonts.Count} game fonts.");
                foreach (var font in check.Fonts)
                {
                    var family = font.Family is null ? string.Empty : $" \"{font.Family}\"";
                    var state = font.Problem is { } problem ? $"can't be read: {problem}"
                        : font.NeedsSwap ? $"lacks {font.MissingLetters.Count} letters ({FontFixer.Sample(font.MissingLetters)})"
                        : "has every letter";
                    var symbols = font.MissingSymbols.Count > 0 ? $"; lacks {font.MissingSymbols.Count} symbols ({FontFixer.Sample(font.MissingSymbols)})" : string.Empty;
                    output.WriteLine($"  {font.Font.Name}{family} ({font.Font.File}{(font.Font.Entry is null ? string.Empty : " archive")}): {state}{symbols}");
                }

                WriteFontLines(output, fixedFonts);
                output.WriteLine($"pack fonts: {(fixedFonts.ManifestFonts.Count == 0 ? "(none)" : string.Join(", ", fixedFonts.ManifestFonts))}");
            }

            if (fixedFonts is { Failure: { } failure })
            {
                error.WriteLine($"The font {fixedFonts.FailedFont!.Name} couldn't be had: {Kebab(failure.Problem)} ({failure.Detail}).");
                return 1;
            }

            return 0;
        });
        return command;
    }

    private static Option<string[]> FontsOption() => new("--fonts")
    {
        Description = "Whitelisted font IDs to try first, as a pack manifest's fonts (e.g. noto-sans).",
        AllowMultipleArgumentsPerToken = true,
    };

    private static IReadOnlyList<string> Fonts(string[]? ids) =>
        [.. (ids ?? []).SelectMany(id => id.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    /// <summary>A fixer over the bundled whitelist; downloaded fonts are cached like the app's (<c>&lt;cache&gt;/fonts</c>).</summary>
    private static FontFixer CreateFixer(OsEnvironment env) => new(
        FontWhitelist.Bundled,
        new FontStore(
            AppPaths.Resolve(env),
            PackFetcher.CreateHttpClient(new System.Net.Http.Headers.ProductInfoHeaderValue("ailocalpack-cli", typeof(RecipeCommands).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"))));

    private static void WriteFontLines(TextWriter output, FontFixResult fonts)
    {
        foreach (var id in fonts.UnknownFonts)
        {
            output.WriteLine($"fonts: {id} is not on this app's font whitelist (ignored).");
        }

        if (fonts.Check is not { } check)
        {
            return;
        }

        if (!check.NeedsSwap)
        {
            output.WriteLine("fonts: the game's fonts have every letter of the translations.");
        }
        else if (fonts.Replacement is { } replacement)
        {
            output.WriteLine($"fonts: {string.Join(", ", check.ToSwap.Select(f => f.Font.Name))} swapped for {replacement.Name} ({replacement.Id}).");
            if (fonts.Uncovered.Count > 0)
            {
                output.WriteLine($"fonts: {replacement.Name} lacks {fonts.Uncovered.Count} of the letters too ({FontFixer.Sample(fonts.Uncovered)}).");
            }
        }
    }

    private static FontsFileJson FontsJson(FontFixResult fonts)
    {
        var check = fonts.Check!;
        return new FontsFileJson(
            [.. check.Fonts.Select(f => new FontJson(
                f.Font.Name,
                f.Font.File,
                f.Font.Entry,
                f.Family,
                f.NeedsSwap,
                Text(f.MissingLetters),
                Text(f.MissingSymbols),
                f.Problem))],
            check.Letters.Count,
            fonts.Replacement?.Id,
            Text(fonts.Uncovered),
            [.. fonts.Files.Files],
            fonts.ManifestFonts,
            fonts.UnknownFonts,
            fonts.Failure is { } failure ? Kebab(failure.Problem) : null);
    }

    private static string Text(IReadOnlyList<int> codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    private static string Kebab(FontFetchProblem problem) => problem switch
    {
        FontFetchProblem.HttpError => "http-error",
        FontFetchProblem.FileMissing => "file-missing",
        FontFetchProblem.HashMismatch => "hash-mismatch",
        FontFetchProblem.DiskError => "disk-error",
        _ => problem.ToString().ToLowerInvariant(),
    };

    /// <summary>The string table at <paramref name="tablePath"/>, or <c>null</c> with the problems on stderr.</summary>
    private static StringTable? ReadTable(string tablePath, TextWriter error)
    {
        PackReadResult<StringTable> table;
        try
        {
            table = StringTableSerializer.Read(File.ReadAllBytes(tablePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Can't read {tablePath}: {e.Message}");
            return null;
        }

        if (table.Value is not { } translations)
        {
            error.WriteLine($"{tablePath} is not a valid string table:");
            foreach (var problem in table.Errors)
            {
                error.WriteLine(problem.Field.Length == 0 ? $"  {problem.Message}" : $"  {problem.Field}: {problem.Message}");
            }

            return null;
        }

        return translations;
    }

    /// <summary>The recipe tool for the game in <paramref name="folder"/>, or <c>null</c> with the reason on stderr.</summary>
    private static (IRecipeTool Tool, string Path, EngineKind Kind)? Tool(string folder, TextWriter error, OsEnvironment env)
    {
        var path = Path.GetFullPath(folder);
        if (!Directory.Exists(path))
        {
            error.WriteLine($"Folder not found: {path}");
            return null;
        }

        var kind = new EngineDetector().Detect(path).Kind;
        if (kind.Engine == GameEngine.Unknown)
        {
            error.WriteLine($"{path}: the engine is unknown, so the toolkit can't read its strings.");
            return null;
        }

        var tools = RecipeTools.Create(env);
        if (tools.IsDisabled(kind))
        {
            error.WriteLine($"{path}: the toolkit doesn't handle {kind.DisplayName} games on this system for now; a translation has to be made like for an unknown engine.");
            return null;
        }

        if (tools.For(kind) is not { } tool)
        {
            error.WriteLine($"{path}: the toolkit can't read {kind.DisplayName} games yet.");
            return null;
        }

        return (tool, path, kind);
    }

    private static ExtractFileJson ToJson(RecipeExtraction extraction, string language)
    {
        var strings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var context = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var details = new SortedDictionary<string, ExtractDetailJson>(StringComparer.Ordinal);
        foreach (var item in extraction.Strings)
        {
            strings[item.Key] = item.Original;
            if (item.Context is { } note)
            {
                context[item.Key] = note;
            }

            if (item.MaxLength is not null || item.Placeholders.Count > 0)
            {
                details[item.Key] = new ExtractDetailJson(item.MaxLength, item.Placeholders.Count > 0 ? item.Placeholders : null);
            }
        }

        return new ExtractFileJson(
            StringTable.CurrentSchemaVersion,
            language,
            extraction.Tool,
            Text(extraction.Version),
            [.. extraction.Files],
            strings,
            context,
            details.Count > 0 ? details : null);
    }

    private static string Text(Version version) => version.ToString(3);
}

/// <summary>A string table (<c>schemaVersion</c>, <c>language</c>, <c>strings</c>, <c>context</c>) plus what the extractor knows; readers of string tables ignore the rest.</summary>
internal sealed record ExtractFileJson(
    int SchemaVersion,
    string Language,
    string Tool,
    string ToolVersion,
    IReadOnlyList<string> Files,
    SortedDictionary<string, string> Strings,
    SortedDictionary<string, string> Context,
    SortedDictionary<string, ExtractDetailJson>? Details);

internal sealed record ExtractDetailJson(int? MaxLength, IReadOnlyList<string>? Placeholders);

/// <summary><c>toolkit fonts --json</c>: each game font, the letters needed, the replacement and the manifest's <c>fonts</c>.</summary>
internal sealed record FontsFileJson(
    IReadOnlyList<FontJson> Fonts,
    int Letters,
    string? Replacement,
    string Uncovered,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> ManifestFonts,
    IReadOnlyList<string> UnknownFonts,
    string? Problem);

internal sealed record FontJson(string Name, string File, string? Entry, string? Family, bool NeedsSwap, string MissingLetters, string MissingSymbols, string? Problem);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExtractFileJson))]
[JsonSerializable(typeof(FontsFileJson))]
internal sealed partial class ExtractJsonContext : JsonSerializerContext;
