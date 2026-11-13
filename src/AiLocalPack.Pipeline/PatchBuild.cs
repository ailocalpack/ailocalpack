using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline;

/// <summary>
/// The Build stage of a game the toolkit can't read (task 57): the strings came from the agent's own extractor, so its
/// own rebuild script makes the translated files, and the pack is a **binary patch** (the agent's code stays in the job
/// folder and never goes into a pack or onto another player's machine).
/// <list type="number">
/// <item>The accepted translations (<see cref="LocalPackBuilder.Collect"/>) are written to the workspace's
/// <c>translations.json</c> and <c>out/</c> is emptied.</item>
/// <item>The agent runs its rebuild script in its sandbox (<see cref="PromptTemplate.RebuildUnknown"/>) and checks the
/// round trip.</item>
/// <item>The app takes the files in <c>out/</c>: each must be a regular file at the path of an existing game file
/// (a patch can only change files the game has; a link, a new file or a path outside fails the stage, Agent kind);
/// a file equal to the game's is left out; none changed fails the stage.</item>
/// <item>The changed files go into the job's <c>build/</c>, the test run tries them as for a recipe pack (no font
/// swap: the toolkit can't read the engine's fonts).</item>
/// <item><see cref="PatchArchiveWriter"/> makes the archive from the working copy's originals (every delta decoded again
/// and checked), the manifest gets <c>patch</c> (URL = <see cref="LocalPackStore.PatchUrl"/>) and the one build with
/// the originals' hashes; the string table is kept in the pack too (keys + translations, no original texts).</item>
/// </list>
/// </summary>
internal sealed class PatchBuild(Game game, PipelineAgent agent, BuildServices services)
{
    private static readonly JsonSerializerOptions TranslationsJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<GenerationStepResult> RunAsync(GenerationStepContext context, SourceStrings source, StringTable? stored, CancellationToken cancellationToken)
    {
        var job = context.Job;
        var files = context.Files;
        BuildStage.Step(context, new BuildProgress(BuildStep.Checking));
        var collected = LocalPackBuilder.Collect(source, stored, SourceMarkup.Of(AiLocalPack.Toolkit.Recipes.RecipeTools.Builtin, source));
        foreach (var (key, issue) in collected.Refused)
        {
            context.Log($"Left out '{key}': {issue.Message}");
        }

        if (collected.Translations.Count == 0)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Agent, "No string has an accepted translation, so there is nothing to build.");
        }

        try
        {
            Directory.CreateDirectory(files.AgentFolder);
            var translations = new SortedDictionary<string, string>(collected.Translations.ToDictionary(), StringComparer.Ordinal);
            AtomicFile.WriteAllBytes(files.AgentTranslationsPath, JsonSerializer.SerializeToUtf8Bytes(new { language = job.Language, strings = translations }, TranslationsJson));
            DeleteFolder(files.AgentOutputFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Disk, $"The agent's workspace can't be prepared: {e.Message}");
        }

        BuildStage.Step(context, new BuildProgress(BuildStep.Rebuilding));
        var run = await agent.RunAsync(
            context,
            PromptTemplate.RebuildUnknown,
            PromptValues.RebuildUnknown(game.Name, job.Language, context.CopyFolder, collected.Translations.Count),
            game.Name,
            cancellationToken,
            new AgentWorkspace(files.AgentFolder)).ConfigureAwait(false);
        if (run.Stop is { } stop)
        {
            return stop;
        }

        IReadOnlyList<string> changed;
        try
        {
            if (Changed(files.AgentOutputFolder, context.CopyFolder, out var problem) is not { } found)
            {
                return BuildStage.Fail(context, GenerationFailureKind.Agent, problem!);
            }

            changed = found;
            DeleteFolder(files.BuildFolder);
            foreach (var file in changed)
            {
                var target = Local(files.BuildFolder, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Local(files.AgentOutputFolder, file), target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Disk, $"The rebuilt files can't be read or copied: {e.Message}");
        }

        context.Log($"The agent's rebuild changed {changed.Count} game files: {string.Join(", ", changed.Take(10))}{(changed.Count > 10 ? ", …" : "")}; {collected.Coverage.Translated} of {collected.Coverage.Total} strings translated.");
        context.Log("Fonts: the engine's fonts aren't checked.");
        var testStep = new BuildProgress(BuildStep.TestRun, changed.Count);
        BuildStage.Step(context, testStep);
        TestRunResult testRun;
        try
        {
            testRun = await services.TestRunner.RunAsync(new TestRunRequest(game, context.CopyFolder, files.BuildFolder, changed, context.Folder, context.Log), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Disk, $"The test run couldn't lay the rebuilt files over the working copy: {e.Message}");
        }

        context.Log($"Test run {testRun.Outcome.ToString().ToLowerInvariant()}" + (testRun.Skip is { } skip ? $" ({skip})" : "") + (testRun.Detail is { } detail ? $": {detail}" : "."));
        if (testRun.Outcome == TestRunOutcome.Crashed)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.TestRun, $"Test run failed: {testRun.Detail}");
        }

        BuildStage.Step(context, testStep with { Step = BuildStep.Packing });
        return Save(context, source, collected, changed, testRun, cancellationToken);
    }

    private GenerationStepResult Save(
        GenerationStepContext context, SourceStrings source, BuildTranslations collected, IReadOnlyList<string> changed, TestRunResult testRun, CancellationToken cancellationToken)
    {
        var job = context.Job;
        var store = services.LocalPacks;
        var earlier = store.FindByJob(game.Id, job.Id);
        var id = earlier is { } e && e.Manifest.Language == job.Language
            ? e.Manifest.Id
            : store.FreeId(game.Id, job.Language, LocalPackBuilder.PreferredId(agent.Id));
        var temp = Path.Combine(context.Folder, "patch.zip");
        PatchArchive archive;
        try
        {
            archive = PatchArchiveWriter.Write(temp, context.CopyFolder, context.Files.BuildFolder, changed, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Disk, $"The patch archive can't be made: {ex.Message}");
        }

        context.Log($"Patch archive: {changed.Count} files, {archive.Size} bytes.");
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
        var patch = new PackPatch(PackPatch.VcdiffFormat, LocalPackStore.PatchUrl(game.Id, job.Language, id, 1), archive.Sha256, archive.Size, archive.Patched);
        var manifest = LocalPackBuilder.Manifest(new LocalPackInput(
            id, game, job.Language, agent.Id, agent.DisplayName, progress.Agent, job.Options.Model, source.Tool, source.ToolVersion, archive.Originals, [], collected.Coverage, now,
            Patch: patch, Quality: job.Options.Quality));
        var report = new LocalPackReport(job.Id, now, testRun, LocalPackFonts.NotChecked, collected.Warnings, collected.Untranslated, collected.KeptAsIs);
        LocalPack pack;
        try
        {
            pack = store.Save(manifest, LocalPackBuilder.Table(source, collected, job.Language), report, glossary, temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Disk, $"The local pack can't be saved: {ex.Message}");
        }
        finally
        {
            File.Delete(temp);
        }

        var validation = PackValidator.Validate(pack.Folder);
        if (!validation.IsValid)
        {
            return BuildStage.Fail(context, GenerationFailureKind.Unexpected, $"The local pack {pack.Folder} doesn't pass the registry's checks: {string.Join("; ", validation.Problems)}");
        }

        context.Log($"Local patch pack {manifest.Id} rev {manifest.Revision} saved in {pack.Folder} ({pack.Report.SizeBytes} bytes): {collected.Coverage.Translated} of {collected.Coverage.Total} strings, {collected.Untranslated.Count} stay untranslated.");
        return new GenerationStepResult.StageDone(job.Counters with { Translated = collected.Translations.Count });
    }

    /// <summary>
    /// The files in <paramref name="output"/> that differ from the copy's (ordinal order), or <c>null</c> with the
    /// problem: a link, a file the game doesn't have, nothing written or nothing changed.
    /// </summary>
    /// <exception cref="IOException">A file can't be read.</exception>
    internal static IReadOnlyList<string>? Changed(string output, string copy, out string? problem)
    {
        problem = null;
        if (!Directory.Exists(output))
        {
            problem = "The agent's rebuild wrote nothing into out/.";
            return null;
        }

        var changed = new List<string>();
        var root = Path.GetFullPath(output);
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var relative = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (entry.LinkTarget is not null)
            {
                problem = $"The agent's rebuild wrote a link, out/{relative}; a patch holds only regular files.";
                return null;
            }

            if (entry is DirectoryInfo)
            {
                // A folder inside a link is never walked: the link was refused above.
                continue;
            }

            var original = Local(copy, relative);
            if (!File.Exists(original) || new FileInfo(original).LinkTarget is not null)
            {
                problem = $"The agent's rebuild wrote out/{relative}, which isn't a file of the game: a binary patch can only change the game's own files.";
                return null;
            }

            if (!SameContent(entry.FullName, original))
            {
                changed.Add(relative);
            }
        }

        if (changed.Count == 0)
        {
            problem = "The agent's rebuild changed no game file.";
            return null;
        }

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    private static bool SameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        using var streamA = infoA.OpenRead();
        using var streamB = infoB.OpenRead();
        var bufferA = new byte[1 << 16];
        var bufferB = new byte[1 << 16];
        int read;
        while ((read = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false)) > 0)
        {
            streamB.ReadExactly(bufferB, 0, read);
            if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
            {
                return false;
            }
        }

        return true;
    }

    private static string Local(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
