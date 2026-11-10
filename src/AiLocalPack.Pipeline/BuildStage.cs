using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Fonts;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline;

/// <summary>What the Build stage needs besides the toolkit: the font swap, where local packs go and the test run.</summary>
public sealed record BuildServices(FontFixer Fonts, LocalPackStore LocalPacks, IGameTestRunner TestRunner);

/// <summary>
/// Stage Build (task 51), one step, no agent: the stored translations are checked again (<see cref="LocalPackBuilder.Collect"/>;
/// none accepted fails the stage), the recipe tool rebuilds the changed game files from the working copy into the job's
/// <c>build/</c> folder (emptied first), with the font swap when the game's fonts lack the translation's letters
/// (<see cref="FontFixer"/>; a font that can't be downloaded fails the stage, to retry); the test run tries the
/// translated copy (<see cref="IGameTestRunner"/>; a crash fails the stage, a skip is recorded); then the local pack is
/// saved (<see cref="LocalPackStore"/>: the job's earlier pack replaced, else a new ID) and checked as registry CI would
/// (<see cref="PackValidator"/>). An overlay a crash left in the working copy is undone first, so the step is safe to repeat.
/// An update job's pack (task 56) is the next revision of its base pack, saved under the base's ID (a local base is
/// replaced by it); the job's glossary is kept with the pack for its own updates.
/// Strings the agent's own extractor found (task 57) are rebuilt by its own script and packed as a binary patch
/// (<see cref="PatchBuild"/>).
/// </summary>
public sealed class BuildStage(Game game, PipelineAgent agent, RecipeTools tools, BuildServices services) : IGenerationStage
{
    public GenerationStage Stage => GenerationStage.Build;

    public async Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var job = context.Job;
        SourceStrings? source;
        StringTable? stored;
        UpdateBase? updateBase;
        try
        {
            TestRunOverlay.Recover(context.Folder, context.CopyFolder);
            source = SourceStringsFile.Read(context.Files);
            stored = context.Files.ReadStrings();
            updateBase = job.Options.Base is null ? null : context.Files.ReadBase()
                ?? throw new GenerationFileException(context.Files.BaseFolder, "the update's base pack is missing");
        }
        catch (GenerationFileException e)
        {
            return Fail(context, GenerationFailureKind.Disk, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(context, GenerationFailureKind.Disk, $"The working copy can't be put back after an earlier test run: {e.Message}");
        }

        if (source is null)
        {
            return Fail(context, GenerationFailureKind.Toolkit, "source.json is missing: the Extract stage didn't store it.");
        }

        var patch = source.Tool == AgentScriptTool.ToolId;
        var found = patch ? null : tools.Find(source.Tool);
        if (!patch && found is null)
        {
            return Fail(context, GenerationFailureKind.Toolkit, $"The toolkit has no '{source.Tool}' tool any more.");
        }

        if (game.Id.SteamAppId is not null && game.Version?.BuildId is not { Length: > 0 })
        {
            return Fail(context, GenerationFailureKind.Toolkit, $"{game.Name}'s Steam build ID is unknown, so the pack can't name the build it fits: refresh the library and retry.");
        }

        if (patch)
        {
            return await new PatchBuild(game, agent, services).RunAsync(context, source, stored, cancellationToken).ConfigureAwait(false);
        }

        var tool = found!;

        Step(context, new BuildProgress(BuildStep.Checking));
        var collected = LocalPackBuilder.Collect(source, stored, tool.Markup);
        foreach (var (key, issue) in collected.Refused)
        {
            context.Log($"Left out '{key}': {issue.Message}");
        }

        if (collected.Translations.Count == 0)
        {
            return Fail(context, GenerationFailureKind.Agent, "No string has an accepted translation, so there is nothing to build.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var build = context.Files.BuildFolder;
        RebuildResult rebuilt;
        FontFixResult fonts;
        IReadOnlyDictionary<string, string> touched;
        try
        {
            if (Directory.Exists(build))
            {
                Directory.Delete(build, recursive: true);
            }

            var output = new DirectoryRebuildOutput(build);
            Step(context, new BuildProgress(BuildStep.Rebuilding));
            rebuilt = tool.Rebuild(context.CopyFolder, job.Language, collected.Translations, output, cancellationToken);
            Step(context, new BuildProgress(BuildStep.Fonts, rebuilt.ChangedFiles.Count));
            fonts = await services.Fonts.ApplyAsync(tool, context.CopyFolder, job.Language, collected.Translations, [], output, cancellationToken: cancellationToken).ConfigureAwait(false);
            touched = Hashes(context.CopyFolder, tool.TouchedFiles(context.CopyFolder, collected.Translations.Keys));
        }
        catch (RecipeException e)
        {
            return Fail(context, GenerationFailureKind.Toolkit, e.File is { } file ? $"Rebuilding {file} failed: {e.Message}" : $"Rebuilding failed: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(context, GenerationFailureKind.Disk, $"The rebuilt files can't be written: {e.Message}");
        }

        if (fonts.Failure is { } failure)
        {
            return Fail(context, GenerationFailureKind.Toolkit, $"The font {fonts.FailedFont?.Name} the translation needs couldn't be downloaded ({failure.Problem}{(failure.Detail is { } d ? $": {d}" : "")}); retry when online.");
        }

        var files = rebuilt.ChangedFiles.Union(fonts.Files.Files, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        context.Log($"Rebuilt {files.Count} files with {tool.Id} {tool.Version.ToString(3)}: {rebuilt.Applied} strings applied, {collected.Coverage.Translated} of {collected.Coverage.Total} translated"
            + (collected.Warnings.Count > 0 ? $", {collected.Warnings.Count} validator warnings." : "."));
        LogFonts(context, fonts);
        var swapped = fonts is { Replacement: { } replacement, Check: { } fontCheck }
            ? new BuildProgress(BuildStep.TestRun, files.Count, [.. fontCheck.ToSwap.Select(f => f.DisplayName)], replacement.Name)
            : new BuildProgress(BuildStep.TestRun, files.Count);
        Step(context, swapped);

        TestRunResult testRun;
        try
        {
            testRun = await services.TestRunner.RunAsync(new TestRunRequest(game, context.CopyFolder, build, files, context.Folder, context.Log), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(context, GenerationFailureKind.Disk, $"The test run couldn't lay the rebuilt files over the working copy: {e.Message}");
        }

        context.Log($"Test run {testRun.Outcome.ToString().ToLowerInvariant()}" + (testRun.Skip is { } skip ? $" ({skip})" : "") + (testRun.Detail is { } detail ? $": {detail}" : "."));
        if (testRun.Outcome == TestRunOutcome.Crashed)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.TestRun, $"Test run failed: {testRun.Detail}");
        }

        Step(context, swapped with { Step = BuildStep.Packing });
        return Save(context, tool, source, collected, touched, fonts, rebuilt, testRun, updateBase);
    }

    /// <summary>The progress card's build step line (task 53); display only.</summary>
    internal static void Step(GenerationStepContext context, BuildProgress step) =>
        PipelineProgressFile.Update(context.Files, p => p with { Build = step });

    private GenerationStepResult Save(
        GenerationStepContext context,
        IRecipeTool tool,
        SourceStrings source,
        BuildTranslations collected,
        IReadOnlyDictionary<string, string> touched,
        FontFixResult fonts,
        RebuildResult rebuilt,
        TestRunResult testRun,
        UpdateBase? updateBase)
    {
        var job = context.Job;
        var store = services.LocalPacks;
        var earlier = store.FindByJob(game.Id, job.Id);
        var id = updateBase is not null ? updateBase.Manifest.Id
            : earlier is { } e && e.Manifest.Language == job.Language ? e.Manifest.Id
            : store.FreeId(game.Id, job.Language, LocalPackBuilder.PreferredId(agent.Id));
        LocalPackUpdate? update = null;
        if (updateBase is not null)
        {
            var diff = UpdateDiff.Of(source.Strings.Select(s => KeyValuePair.Create(s.Key, s.Original)), updateBase.Strings);
            update = new LocalPackUpdate(updateBase.Manifest.Revision, diff.Added.Count, diff.Changed.Count, diff.Removed.Count);
        }

        Glossary? glossary;
        try
        {
            glossary = context.Files.ReadGlossary();
        }
        catch (GenerationFileException ex)
        {
            context.Log($"The glossary isn't kept with the pack: {ex.Message}");
            glossary = null;
        }

        var now = context.Time.GetUtcNow();
        var progress = PipelineProgressFile.Read(context.Files);
        var manifest = LocalPackBuilder.Manifest(new LocalPackInput(
            id, game, job.Language, agent.Id, agent.DisplayName, progress.Agent, job.Options.Model, tool.Id, tool.Version, touched, fonts.ManifestFonts, collected.Coverage, now,
            updateBase?.Manifest, update));
        var report = new LocalPackReport(
            job.Id,
            now,
            testRun,
            fonts.Check is null
                ? LocalPackFonts.NotChecked
                : new LocalPackFonts(true, fonts.Replacement?.Id, [.. fonts.Check.ToSwap.Select(f => f.DisplayName)], fonts.Uncovered),
            collected.Warnings,
            collected.Untranslated,
            collected.KeptAsIs,
            InGameLanguage: rebuilt.InGameLanguage,
            Update: update);
        LocalPack pack;
        try
        {
            pack = store.Save(manifest, LocalPackBuilder.Table(source, collected, job.Language), report, glossary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(context, GenerationFailureKind.Disk, $"The local pack can't be saved: {ex.Message}");
        }

        var validation = PackValidator.Validate(pack.Folder);
        if (!validation.IsValid)
        {
            return Fail(context, GenerationFailureKind.Unexpected, $"The local pack {pack.Folder} doesn't pass the registry's checks: {string.Join("; ", validation.Problems)}");
        }

        context.Log($"Local pack {manifest.Id} rev {manifest.Revision} saved in {pack.Folder} ({pack.Report.SizeBytes} bytes): {collected.Coverage.Translated} of {collected.Coverage.Total} strings, {collected.Untranslated.Count} stay untranslated.");
        return new GenerationStepResult.StageDone(job.Counters with { Translated = collected.Translations.Count });
    }

    /// <exception cref="IOException">A touched file can't be read.</exception>
    private static Dictionary<string, string> Hashes(string copy, IReadOnlyList<string> files)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var stream = File.OpenRead(Path.Combine(copy, file.Replace('/', Path.DirectorySeparatorChar)));
            hashes[file] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        return hashes;
    }

    private static void LogFonts(GenerationStepContext context, FontFixResult fonts)
    {
        if (fonts.Check is not { } check)
        {
            context.Log("Fonts: the engine's fonts aren't checked.");
        }
        else if (fonts.Replacement is { } replacement)
        {
            context.Log($"Fonts: {string.Join(", ", check.ToSwap.Select(f => f.DisplayName))} lack {check.Letters.Count} letters of the translation; swapped for {replacement.Name}"
                + (fonts.Uncovered.Count > 0 ? $", which lacks {FontFixer.Sample(fonts.Uncovered)} too." : "."));
        }
        else
        {
            context.Log("Fonts: the game's fonts have every letter of the translation.");
        }
    }

    internal static GenerationStepResult.Failed Fail(GenerationStepContext context, GenerationFailureKind kind, string message)
    {
        context.Log(message);
        return new GenerationStepResult.Failed(kind, message);
    }
}
