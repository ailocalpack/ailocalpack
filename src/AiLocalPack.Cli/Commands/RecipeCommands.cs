using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>toolkit extract &lt;folder&gt; --out strings.json [--lang en]</c>: the game's strings as an editable string table
/// (originals as the values, notes as context, placeholders and length limits under <c>details</c>).
/// <c>toolkit rebuild &lt;folder&gt; --strings strings.json --out &lt;dir&gt;</c>: the game files a string table changes,
/// written into a folder laid out like the game (ready for <c>dev apply-files</c>).
/// </summary>
internal static class RecipeCommands
{
    private const int ListedUnknownKeys = 20;

    private static readonly JsonSerializerOptions WriteOptions = new(ExtractJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Command BuildExtract()
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

            if (Tool(result.GetValue(folder)!, error) is not var (tool, path, kind))
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

    public static Command BuildRebuild()
    {
        var folder = new Argument<string>("folder") { Description = "Game folder (read only)." };
        var strings = new Option<string>("--strings") { Description = "String table with the translations (JSON or gzip, as 'toolkit extract' or a pack writes it).", Required = true };
        var outDir = new Option<string>("--out") { Description = "Folder for the rebuilt files, laid out like the game folder.", Required = true };
        var command = new Command("rebuild", "Rebuild the game files a string table changes, from the game's own files, into another folder.")
        {
            folder, strings, outDir,
        };
        command.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var tablePath = result.GetValue(strings)!;
            PackReadResult<StringTable> table;
            try
            {
                table = StringTableSerializer.Read(File.ReadAllBytes(tablePath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Can't read {tablePath}: {e.Message}");
                return Task.FromResult(1);
            }

            if (table.Value is not { } translations)
            {
                error.WriteLine($"{tablePath} is not a valid string table:");
                foreach (var problem in table.Errors)
                {
                    error.WriteLine(problem.Field.Length == 0 ? $"  {problem.Message}" : $"  {problem.Field}: {problem.Message}");
                }

                return Task.FromResult(1);
            }

            if (Tool(result.GetValue(folder)!, error) is not var (tool, path, _))
            {
                return Task.FromResult(1);
            }

            var target = Path.GetFullPath(result.GetValue(outDir)!);
            if (string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(path), StringComparison.Ordinal))
            {
                error.WriteLine("--out must not be the game folder: the rebuilt files go elsewhere and are installed with 'dev apply-files'.");
                return Task.FromResult(1);
            }

            RebuildResult rebuilt;
            try
            {
                rebuilt = tool.Rebuild(path, translations.Language, translations.Strings, new DirectoryRebuildOutput(target), cancellationToken);
            }
            catch (RecipeException e)
            {
                error.WriteLine(e.Message);
                return Task.FromResult(1);
            }

            output.WriteLine($"Rebuilt {rebuilt.ChangedFiles.Count} files ({rebuilt.Applied} strings translated) into {target}.");
            foreach (var file in rebuilt.ChangedFiles)
            {
                output.WriteLine($"  {file}");
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

            return Task.FromResult(0);
        });
        return command;
    }

    /// <summary>The recipe tool for the game in <paramref name="folder"/>, or <c>null</c> with the reason on stderr.</summary>
    private static (IRecipeTool Tool, string Path, EngineKind Kind)? Tool(string folder, TextWriter error)
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

        if (RecipeTools.Builtin.For(kind.Engine) is not { } tool)
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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExtractFileJson))]
internal sealed partial class ExtractJsonContext : JsonSerializerContext;
