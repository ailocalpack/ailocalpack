using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Agent;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Extract (task 49): the toolkit's recipe tool reads the working copy's strings into <c>source.json</c>
/// (<see cref="SourceStringsFile"/>), cuts them into the Translate stage's chunks (<c>chunks.json</c>,
/// <see cref="ChunkPlanner"/>) and sets the job's counters (strings, chunks, translations already stored). No agent
/// runs for an engine the toolkit reads: its extraction is complete by construction, and the Glossary stage's agent
/// reads every string anyway.
/// An unknown engine, one switched off on this OS or without a tool (task 57), or one whose tool fails to read the game
/// (an encrypted archive, a format change): the agent explores the copy and writes
/// its own extractor and rebuilder in its workspace (<c>agent/</c>, <see cref="AgentWorkspace"/>, prompt
/// <see cref="PromptTemplate.ExtractUnknown"/>), runs the extractor into <c>agent/strings.json</c> and checks the round
/// trip; the app reads that file (<see cref="AgentScriptTool"/>: refused strings fail the stage, Agent kind, to retry)
/// and goes on as for a toolkit extraction, with <c>source.json</c>'s tool <c>agent-script</c>. Without an agent
/// (tests that give none) such a game fails the stage.
/// An update job (task 56) compares the strings with its base pack (<see cref="UpdateDiff"/>): the base's translations
/// of unchanged strings go into the string store, and the chunks hold only the added and changed strings.
/// </summary>
/// <param name="agent">Writes the extractor of a game the toolkit can't read; <c>null</c> = such a game fails the stage.</param>
/// <param name="gameName">The game's name, for the agent's prompt.</param>
public sealed class ExtractStage(RecipeTools tools, ChunkBudget? budget = null, PipelineAgent? agent = null, string gameName = "") : IGenerationStage
{
    public GenerationStage Stage => GenerationStage.Extract;

    public async Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var kind = new EngineDetector().Detect(context.CopyFolder).Kind;
        RecipeExtraction extraction;
        var problem = Problem(kind);
        string? toolkitError = null;
        if (problem is null)
        {
            var tool = tools.For(kind)!;
            try
            {
                extraction = tool.Extract(context.CopyFolder, cancellationToken);
                return extraction.Strings.Count == 0
                    ? new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, $"The toolkit found no text to translate in this {kind.DisplayName} game.")
                    : Store(context, kind, tool, extraction);
            }
            catch (RecipeException e)
            {
                // The toolkit's reader failed on this game: the agent may still read it its own way.
                toolkitError = e.Message;
                problem = $"The toolkit couldn't read this {kind.DisplayName} game: {e.Message}";
            }
        }

        if (agent is null)
        {
            context.Log(problem);
            return new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, problem);
        }

        context.Log(problem + " The agent writes its own extractor.");
        var files = context.Files;
        var run = await agent.RunAsync(
            context,
            PromptTemplate.ExtractUnknown,
            PromptValues.ExtractUnknown(gameName, context.Job.Language, kind, context.CopyFolder, CountFiles(context.CopyFolder), toolkitError),
            gameName,
            cancellationToken,
            new AgentWorkspace(files.AgentFolder)).ConfigureAwait(false);
        if (run.Stop is { } stop)
        {
            return stop;
        }

        var agentTool = new AgentScriptTool(files.AgentStringsPath);
        try
        {
            extraction = agentTool.Extract(context.CopyFolder, cancellationToken);
        }
        catch (RecipeException e)
        {
            return Fail(context, GenerationFailureKind.Agent, $"The agent's extractor didn't give strings the app can read: {e.Message}");
        }

        return extraction.Strings.Count == 0
            ? Fail(context, GenerationFailureKind.Agent, "The agent's extractor found no text to translate.")
            : Store(context, kind, agentTool, extraction);
    }

    private GenerationStepResult Store(GenerationStepContext context, EngineKind kind, IRecipeTool tool, RecipeExtraction extraction)
    {

        var source = SourceStrings.Of(kind, extraction);
        IReadOnlyList<TranslationChunk> chunks;
        int translated;
        UpdateDiff? diff = null;
        try
        {
            var toTranslate = source.Strings;
            if (context.Job.Options.Base is not null)
            {
                if (context.Files.ReadBase() is not { } updateBase)
                {
                    return new GenerationStepResult.Failed(GenerationFailureKind.Disk, "The update's base pack is missing from the job folder (base/).");
                }

                if (updateBase.Manifest.Language != context.Job.Language)
                {
                    return new GenerationStepResult.Failed(GenerationFailureKind.Unexpected, $"The base pack is in '{updateBase.Manifest.Language}', the job in '{context.Job.Language}'.");
                }

                if (updateBase.Manifest.Recipe is { } recipe && recipe.Tool != tool.Id)
                {
                    context.Log($"The base pack was made with {recipe.Tool}, the game is read with {tool.Id} now.");
                }

                diff = UpdateDiff.Of(source.Strings.Select(s => KeyValuePair.Create(s.Key, s.Original)), updateBase.Strings);
                context.Files.WriteStrings(diff.Seed(context.Files.ReadStrings(), updateBase.Strings, context.Job.Language));
                toTranslate = [.. source.Strings.Where(s => diff.ToTranslate.Contains(s.Key))];
            }

            chunks = ChunkPlanner.Plan(tool.Id, toTranslate, budget);
            translated = Translated(context.Files, source);
            SourceStringsFile.Write(context.Files, source);
            ChunkPlanFile.Write(context.Files, chunks);
        }
        catch (GenerationFileException e)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Disk, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"The job's files (source.json, strings.json, chunks.json) can't be written: {e.Message}");
        }

        if (diff is not null)
        {
            context.Log($"Update of {context.Job.Options.Base!.PackId} rev {context.Job.Options.Base.Revision}: {diff.Added.Count} new strings, {diff.Changed.Count} changed, {diff.Removed.Count} removed, {diff.Unchanged.Count} unchanged (their translations kept).");
        }

        context.Log($"Extracted {source.Strings.Count} strings from {source.Files.Count} files with {tool.Id} {tool.Version.ToString(3)} ({(kind.Engine == GameEngine.Unknown ? "engine unknown" : kind.DisplayName)}): {chunks.Count} chunks"
            + (translated > 0 ? $", {translated} already translated." : "."));
        return new GenerationStepResult.StageDone(new GenerationCounters(source.Strings.Count, translated, chunks.Count, 0));
    }

    private static GenerationStepResult.Failed Fail(GenerationStepContext context, GenerationFailureKind kind, string message)
    {
        context.Log(message);
        return new GenerationStepResult.Failed(kind, message);
    }

    /// <summary>The working copy's files, for the agent's prompt (best effort).</summary>
    private static int CountFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Why the toolkit can't extract games of <paramref name="kind"/>, or <c>null</c>.</summary>
    private string? Problem(EngineKind kind) =>
        kind.Engine == GameEngine.Unknown
            ? "The game's engine wasn't recognized, so the toolkit can't read its strings."
            : tools.IsDisabled(kind)
            ? $"The toolkit doesn't handle {kind.DisplayName} games on this system for now."
            : tools.For(kind) is null
            ? $"The toolkit can't read {kind.DisplayName} games yet."
            : null;

    /// <summary>Stored translations of keys the game still has (a stage run again keeps what an earlier run stored).</summary>
    private static int Translated(GenerationFiles files, SourceStrings source)
    {
        if (files.ReadStrings() is not { } stored)
        {
            return 0;
        }

        var keys = source.Strings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        return stored.Strings.Count(p => keys.Contains(p.Key) && !string.IsNullOrEmpty(p.Value));
    }
}
