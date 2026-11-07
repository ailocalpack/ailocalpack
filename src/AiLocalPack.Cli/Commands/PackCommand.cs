using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>pack check &lt;manifest&gt; --game &lt;id&gt; [--json]</c>: whether a pack fits the player's copy of a game.
/// Exit code 0 = compatible or probably compatible, 2 = incompatible, 1 = the check couldn't run.
/// <c>pack validate &lt;folder&gt;… [--github]</c>: the checks registry CI runs on a pack; exit code 1 = a pack is invalid.
/// </summary>
internal static class PackCommand
{
    public const int IncompatibleExitCode = 2;

    private static readonly PackJsonWriterContext JsonWriter = new(new JsonSerializerOptions(PackJsonWriterContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static Command Build(OsEnvironment env)
    {
        var manifestArgument = new Argument<string>("manifest") { Description = "The pack's manifest.json, or the pack folder holding it." };
        var gameOption = new Option<string>("--game")
        {
            Description = "The game to check against (steam:<app id> or fp:<fingerprint>), as listed by 'games list'.",
            Required = true,
        };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var check = new Command(
            "check",
            "Check whether a pack is compatible, probably compatible or incompatible with the installed game, and why. Exit code 2 = incompatible.")
        {
            manifestArgument, gameOption, json,
        };
        check.SetAction((result, cancellationToken) =>
        {
            var error = result.InvocationConfiguration.Error;
            var gameText = result.GetValue(gameOption)!;
            if (!GameId.TryParse(gameText, out var gameId))
            {
                error.WriteLine($"'{gameText}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
                return Task.FromResult(1);
            }

            if (ReadManifest(result.GetValue(manifestArgument)!, error) is not { } pack)
            {
                return Task.FromResult(1);
            }

            var paths = AppPaths.Resolve(env);
            if (FindGame(env, paths, gameId) is not { } game)
            {
                error.WriteLine($"{gameId} is not in the game catalog: install it in Steam or add its folder in the app, then run 'games list'.");
                return Task.FromResult(1);
            }

            var hashes = FileHashCache.Load(paths.FileHashes);
            var compatibility = new PackCompatibilityChecker(hashes, RecipeTools.Create(env)).Check(pack, game, cancellationToken);
            hashes.Save();

            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                WriteJson(output, pack, game, compatibility);
            }
            else
            {
                WriteText(output, pack, game, compatibility);
            }

            return Task.FromResult(compatibility.CanInstall ? 0 : IncompatibleExitCode);
        });

        return new Command("pack", "Packs: one localization of one game into one language.") { check, BuildValidate(), PackFetchCommand.Build(env) };
    }

    private static Command BuildValidate()
    {
        var folders = new Argument<string[]>("folder")
        {
            Description = "Pack folders (games/<game>/<language>/<pack id>/ in a registry checkout).",
            Arity = ArgumentArity.OneOrMore,
        };
        var github = new Option<bool>("--github") { Description = "Also print problems as GitHub Actions annotations (::error file=...)." };
        var validate = new Command(
            "validate",
            "Check pack folders as registry CI does: manifest, string table, size and hash, folder layout, no executable files, size limits. Exit code 1 = a pack is invalid.")
        {
            folders, github,
        };
        validate.SetAction((result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var annotate = result.GetValue(github);
            var allValid = true;
            foreach (var folder in result.GetValue(folders)!)
            {
                var validation = PackValidator.Validate(folder, cancellationToken);
                allValid &= validation.IsValid;
                var name = folder.Replace('\\', '/').TrimEnd('/');
                if (validation.IsValid)
                {
                    var pack = validation.Manifest!;
                    output.WriteLine($"{name}: valid ({pack.Game}/{pack.Language}/{pack.Id} rev {pack.Revision}, "
                        + $"{pack.Coverage.Translated} of {pack.Coverage.Total} strings translated)");
                    continue;
                }

                output.WriteLine($"{name}: invalid, {Count(validation.Problems.Count, "problem")}");
                foreach (var problem in validation.Problems)
                {
                    output.WriteLine(annotate
                        ? GitHubAnnotations.Format(
                            "error",
                            Path.Combine(validation.Folder, problem.File.Length > 0 ? problem.File : "manifest.json"),
                            problem.Field,
                            problem.File.Length > 0 ? problem.Message : $"{problem.Message} ({name})")
                        : $"  {problem}");
                }
            }

            return Task.FromResult(allValid ? 0 : 1);
        });
        return validate;
    }

    private static PackManifest? ReadManifest(string path, TextWriter error)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "manifest.json") : path;
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Can't read the manifest {file}: {e.Message}");
            return null;
        }

        var read = PackManifestSerializer.Read(text);
        if (read.Value is null)
        {
            error.WriteLine($"{file} is not a valid pack manifest:");
            foreach (var problem in read.Errors)
            {
                error.WriteLine($"  {problem}");
            }
        }

        return read.Value;
    }

    /// <summary>The game from the catalog, after a Steam scan so the build ID is the installed one.</summary>
    internal static Game? FindGame(OsEnvironment env, AppPaths paths, GameId id)
    {
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance, TimeProvider.System).Load();
        var catalog = new GameCatalog(paths, NullLogger<GameCatalog>.Instance, TimeProvider.System);
        catalog.Load();
        if (id.Kind == GameIdKind.Steam)
        {
            catalog.Refresh(new SteamLibraryScanner(new SteamLocator(env)).Scan(settings.SteamLibraries));
        }

        return catalog.Find(id);
    }

    private static void WriteText(TextWriter output, PackManifest pack, Game game, PackCompatibility result)
    {
        output.WriteLine($"{VerdictText(result.Verdict)}: {Explain(result)}");
        output.WriteLine();
        output.WriteLine($"pack:  {pack.Game}/{pack.Language}/{pack.Id} rev {pack.Revision} \"{pack.Title}\"");
        output.WriteLine($"       {(pack.Recipe is { } recipe ? $"recipe {recipe.Tool} {recipe.Version}" : "binary patch")}; "
            + $"{Count(pack.Builds.Count, "build")}: {BuildList(pack)}");
        output.WriteLine($"game:  {game.Id} \"{game.Name}\", {(game.Version?.BuildId is { } build ? $"build {build}" : "no build ID")}");
        output.WriteLine($"       {game.InstallPath}");
        WriteFiles(output, "changed", result.ChangedFiles);
        WriteFiles(output, "missing", result.MissingFiles);
    }

    private static void WriteFiles(TextWriter output, string title, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"{title}:");
        foreach (var file in files)
        {
            output.WriteLine($"  {file}");
        }
    }

    private static string VerdictText(PackCompatibilityVerdict verdict) => verdict switch
    {
        PackCompatibilityVerdict.Compatible => "compatible",
        PackCompatibilityVerdict.ProbablyCompatible => "probably compatible",
        _ => "incompatible",
    };

    /// <summary>The reason in one plain sentence.</summary>
    internal static string Explain(PackCompatibility result)
    {
        var builtFor = result.ReferenceBuildId is { } reference ? $"built for build {reference}" : "built for other files";
        var yours = result.GameBuildId is { } build ? $"you have build {build}" : "your game has no build ID";
        return result.Reason switch
        {
            PackCompatibilityReason.BuildListed => $"made for build {result.GameBuildId}, which is your build.",
            PackCompatibilityReason.FilesMatch => result.ReferenceBuildId is { } matched
                ? $"{yours}, and the {Count(result.TouchedFileCount, "file")} the pack changes are the same as in build {matched}, which it was made for."
                : $"the {Count(result.TouchedFileCount, "file")} the pack changes are the same as in the copy it was made for.",
            PackCompatibilityReason.FilesChanged =>
                $"{builtFor}; {yours}. {result.ChangedFiles.Count} of {Count(result.TouchedFileCount, "file")} the pack changes "
                + "differ; they are rebuilt from your copy, and text the update added stays untranslated.",
            PackCompatibilityReason.WrongGame => "the pack is for another game.",
            PackCompatibilityReason.GameNotAvailable => "the game folder can't be reached.",
            PackCompatibilityReason.FilesMissing =>
                $"{result.MissingFiles.Count} of {Count(result.TouchedFileCount, "file")} the pack changes "
                + $"{(result.MissingFiles.Count == 1 ? "is" : "are")} not in the game folder.",
            PackCompatibilityReason.FileUnreadable => $"can't read {result.UnreadableFile}.",
            PackCompatibilityReason.PatchFilesChanged =>
                $"{builtFor}; {yours}. The binary patch needs the exact files it was made from, "
                + $"and {result.ChangedFiles.Count} of {Count(result.TouchedFileCount, "file")} differ.",
            PackCompatibilityReason.RecipeToolUnknown => $"this app has no '{result.RecipeTool?.Tool}' tool; update the app.",
            PackCompatibilityReason.EngineUnsupported =>
                $"the toolkit doesn't handle {(EngineKind.TryParse(result.Engine, out var engine) ? engine.DisplayName : result.Engine)} games on this system for now.",
            PackCompatibilityReason.RecipeToolTooNew =>
                $"the pack needs '{result.RecipeTool?.Tool}' {result.RecipeTool?.Version}, newer than this app has; update the app.",
            _ => result.Reason.ToString(),
        };
    }

    private static string BuildList(PackManifest pack) =>
        pack.Builds.All(b => b.BuildId is null)
            ? $"{Count(pack.TouchedFiles.Count, "file")} by hash"
            : string.Join(", ", pack.Builds.Select(b => b.BuildId));

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static void WriteJson(TextWriter output, PackManifest pack, Game game, PackCompatibility result)
    {
        var dto = new PackCheckJson(
            $"{pack.Game}/{pack.Language}/{pack.Id}",
            pack.Revision,
            game.Id.ToString(),
            JsonNamingPolicy.KebabCaseLower.ConvertName(result.Verdict.ToString()),
            JsonNamingPolicy.KebabCaseLower.ConvertName(result.Reason.ToString()),
            Explain(result),
            result.PackBuildIds,
            result.GameBuildId,
            result.ReferenceBuildId,
            result.TouchedFileCount,
            result.ChangedFiles,
            result.MissingFiles,
            result.UnreadableFile);
        output.WriteLine(JsonSerializer.Serialize(dto, JsonWriter.PackCheckJson));
    }
}

internal sealed record PackCheckJson(
    string Pack,
    int Revision,
    string Game,
    string Verdict,
    string Reason,
    string Explanation,
    IReadOnlyList<string> PackBuilds,
    string? GameBuild,
    string? ReferenceBuild,
    int TouchedFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> MissingFiles,
    string? UnreadableFile);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PackCheckJson))]
internal sealed partial class PackJsonWriterContext : JsonSerializerContext;
