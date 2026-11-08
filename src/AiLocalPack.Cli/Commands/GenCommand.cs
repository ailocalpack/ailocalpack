using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>gen estimate &lt;game&gt; [--all-files] [--json]</c>: what generating a pack for a game would take (strings, words,
/// working copy size, free space, agent time, share of the subscription's limits).
/// <c>gen copy &lt;game&gt; [--all-files] [--job &lt;id&gt;]</c>: makes the working copy a job would use and prints where.
/// </summary>
internal static class GenCommand
{
    private static readonly GenJsonContext Json = new(new JsonSerializerOptions(GenJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static Command Build(OsEnvironment env)
    {
        var command = new Command("gen", "Generation: a new pack made by an AI agent on a copy of the game.")
        {
            BuildEstimate(env),
            BuildCopy(env),
        };
        foreach (var job in GenJobCommands.Build(env))
        {
            command.Subcommands.Add(job);
        }

        return command;
    }

    private static Command BuildEstimate(OsEnvironment env)
    {
        var game = GameArgument();
        var allFiles = AllFilesOption();
        var json = new Option<bool>("--json") { Description = "Print JSON instead of text." };
        var command = new Command("estimate", "Estimate a generation: strings and words to translate, working copy size, free space, agent time and subscription limits.")
        {
            game, allFiles, json,
        };
        command.SetAction((result, cancellationToken) =>
        {
            var error = result.InvocationConfiguration.Error;
            if (Find(env, result.GetValue(game)!, error) is not var (paths, found))
            {
                return Task.FromResult(1);
            }

            GenerationSizeEstimate estimate;
            try
            {
                estimate = Estimator(env, paths).Estimate(found.InstallPath, Engine(found), Scope(result.GetValue(allFiles)), cancellationToken);
            }
            catch (WorkingCopyException e)
            {
                error.WriteLine(e.Message);
                return Task.FromResult(1);
            }

            var output = result.InvocationConfiguration.Output;
            if (result.GetValue(json))
            {
                output.WriteLine(JsonSerializer.Serialize(ToJson(found, estimate), Json.GenEstimateJson));
            }
            else
            {
                WriteText(output, found, estimate);
            }

            return Task.FromResult(0);
        });
        return command;
    }

    private static Command BuildCopy(OsEnvironment env)
    {
        var game = GameArgument();
        var allFiles = AllFilesOption();
        var job = new Option<string>("--job") { Description = "Job ID (default: a new one)." };
        var command = new Command("copy", "Make the working copy of a game that a generation job works on, and print its folder.")
        {
            game, allFiles, job,
        };
        command.SetAction((result, cancellationToken) =>
        {
            var error = result.InvocationConfiguration.Error;
            if (Find(env, result.GetValue(game)!, error) is not var (paths, found))
            {
                return Task.FromResult(1);
            }

            var copies = Copies(paths);
            try
            {
                var estimate = Estimator(env, paths).Estimate(found.InstallPath, Engine(found), Scope(result.GetValue(allFiles)), cancellationToken);
                var jobFolder = copies.JobFolder(found.Id, result.GetValue(job) ?? WorkingCopies.NewJobId(TimeProvider.System));
                var copy = copies.Create(estimate.Copy, jobFolder, estimate.ReserveBytes, cancellationToken: cancellationToken);
                var output = result.InvocationConfiguration.Output;
                output.WriteLine($"Copied {Files(copy.Files)} of {found.Name} into {copy.Folder}: {Size(copy.ClonedBytes)} cloned, {Size(copy.CopiedBytes)} copied.");
                foreach (var link in copy.SkippedLinks)
                {
                    output.WriteLine($"  link not recreated: {link}");
                }
            }
            catch (WorkingCopyException e)
            {
                error.WriteLine(e.Message);
                return Task.FromResult(1);
            }
            catch (ArgumentException e)
            {
                error.WriteLine(e.Message);
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        });
        return command;
    }

    private static Argument<string> GameArgument() => new("game") { Description = "The game (steam:<app id> or fp:<fingerprint>), as listed by 'games list'." };

    private static Option<bool> AllFilesOption() => new("--all-files")
    {
        Description = "Copy every file; by default movies and audio are left out when the toolkit reads the engine.",
    };

    private static WorkingCopyScope? Scope(bool allFiles) => allFiles ? WorkingCopyScope.AllFiles : null;

    private static EngineKind? Engine(Game game) => EngineKind.TryParse(game.Engine, out var kind) ? kind : null;

    private static WorkingCopies Copies(AppPaths paths) => new(paths, FileCloner.Create(), new SystemDiskSpace());

    private static GenerationEstimator Estimator(OsEnvironment env, AppPaths paths) => new(RecipeTools.Create(env), Copies(paths));

    private static (AppPaths, Game)? Find(OsEnvironment env, string text, TextWriter error)
    {
        if (!GameId.TryParse(text, out var id))
        {
            error.WriteLine($"'{text}' is not a game ID (steam:<app id> or fp:<64 lowercase hex>).");
            return null;
        }

        var paths = AppPaths.Resolve(env);
        if (PackCommand.FindGame(env, paths, id) is not { } game)
        {
            error.WriteLine($"{id} is not in the game catalog: install it in Steam or add its folder in the app, then run 'games list'.");
            return null;
        }

        if (!game.IsAvailable || !Directory.Exists(game.InstallPath))
        {
            error.WriteLine($"{game.Name} ({id}) is not available: its folder {game.InstallPath} is missing.");
            return null;
        }

        return (paths, game);
    }

    private static void WriteText(TextWriter output, Game game, GenerationSizeEstimate estimate)
    {
        var work = estimate.Work;
        var text = work.Text;
        output.WriteLine($"{game.Name} ({game.Id}), {estimate.Engine.DisplayName}: {SourceText(estimate)}");
        output.WriteLine(Invariant($"strings: {text.Strings:N0} ({text.Words:N0} words, {text.Characters:N0} characters)"));
        var copy = estimate.Copy;
        var left = copy.SkippedFiles > 0 ? $"; movies and audio left out: {Files(copy.SkippedFiles)}, {Size(copy.SkippedBytes)}" : "";
        output.WriteLine($"copy: {Size(copy.TotalBytes)} in {Files(copy.FileCount)}{left}");
        var space = estimate.Space;
        var cloned = space.CanClone ? " (cloned on this disk: the copy takes no space)" : "";
        output.WriteLine(space.FreeBytes is { } free
            ? $"disk: needs {Size(space.NeededBytes)}{cloned}, {Size(free)} free: {(space.IsEnough ? "enough" : "NOT ENOUGH")}"
            : $"disk: needs {Size(space.NeededBytes)}{cloned}, free space unknown");
        output.WriteLine($"agent time: {Duration(work.TimeLow)} to {Duration(work.TimeHigh)}");
        output.WriteLine(Invariant($"subscription: {work.LimitWindowsLow:0.#} to {work.LimitWindowsHigh:0.#} five-hour limit windows of an entry-level plan (about {work.LimitDaysLow:0.#} to {work.LimitDaysHigh:0.#} days)"));
        output.WriteLine(Invariant($"tokens: ~{work.InputTokens:N0} in, ~{work.OutputTokens:N0} out, {work.Chunks:N0} chunks of {GenerationEstimateModel.StringsPerChunk}"));
    }

    private static string SourceText(GenerationSizeEstimate estimate) => estimate.Source switch
    {
        TextSizeSource.Toolkit => $"strings read by the toolkit ({estimate.Tool!.Id} {estimate.Tool.Version.ToString(3)}).",
        TextSizeSource.UnknownEngine => "unknown engine, so the text is a rough guess from file sizes.",
        TextSizeSource.EngineDisabled => "the toolkit doesn't handle this engine on this system for now, so the text is a rough guess from file sizes.",
        TextSizeSource.NoTool => "the toolkit can't read this engine yet, so the text is a rough guess from file sizes.",
        _ => $"the toolkit couldn't read the game ({estimate.ToolkitError}), so the text is a rough guess from file sizes.",
    };

    private static GenEstimateJson ToJson(Game game, GenerationSizeEstimate estimate)
    {
        var work = estimate.Work;
        return new GenEstimateJson(
            game.Id.ToString(),
            game.Name,
            estimate.Engine.ToString(),
            Kebab(estimate.Source.ToString()),
            estimate.Tool?.Id,
            estimate.Tool?.Version.ToString(3),
            estimate.ToolkitError,
            work.Text.Strings,
            work.Text.Words,
            work.Text.Characters,
            new GenCopyJson(
                estimate.Copy.Scope == WorkingCopyScope.AllFiles ? "all-files" : "without-media",
                estimate.Copy.FileCount,
                estimate.Copy.TotalBytes,
                estimate.Copy.SkippedFiles,
                estimate.Copy.SkippedBytes),
            new GenDiskJson(estimate.Space.NeededBytes, estimate.Space.FreeBytes, estimate.Space.CanClone, estimate.Space.IsEnough, estimate.ReserveBytes),
            new GenWorkJson(
                work.Chunks,
                work.InputTokens,
                work.OutputTokens,
                (long)work.TimeLow.TotalSeconds,
                (long)work.TimeHigh.TotalSeconds,
                Math.Round(work.LimitWindowsLow, 2),
                Math.Round(work.LimitWindowsHigh, 2),
                Math.Round(work.LimitDaysLow, 2),
                Math.Round(work.LimitDaysHigh, 2)));
    }

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));

    internal static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? Invariant($"{bytes:N0} bytes") : Invariant($"{value:0.#} {units[unit]}");
    }

    private static string Files(int count) => count == 1 ? "1 file" : Invariant($"{count:N0} files");

    private static string Duration(TimeSpan time) => time.TotalMinutes < 60
        ? Invariant($"{Math.Max(1, Math.Round(time.TotalMinutes)):0} min")
        : Invariant($"{time.TotalHours:0.#} h");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

internal sealed record GenEstimateJson(
    string Game,
    string Name,
    string Engine,
    string Source,
    string? Tool,
    string? ToolVersion,
    string? ToolkitError,
    int Strings,
    long Words,
    long Characters,
    GenCopyJson Copy,
    GenDiskJson Disk,
    GenWorkJson Estimate);

internal sealed record GenCopyJson(string Scope, int Files, long Bytes, int SkippedFiles, long SkippedBytes);

internal sealed record GenDiskJson(long NeededBytes, long? FreeBytes, bool CanClone, bool Enough, long ReserveBytes);

internal sealed record GenWorkJson(
    int Chunks,
    long InputTokens,
    long OutputTokens,
    long TimeLowSeconds,
    long TimeHighSeconds,
    double LimitWindowsLow,
    double LimitWindowsHigh,
    double LimitDaysLow,
    double LimitDaysHigh);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GenEstimateJson))]
internal sealed partial class GenJsonContext : JsonSerializerContext;
