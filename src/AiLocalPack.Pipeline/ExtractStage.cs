using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Pipeline;

/// <summary>
/// Stage Extract (task 49): the toolkit's recipe tool reads the working copy's strings into <c>source.json</c>
/// (<see cref="SourceStringsFile"/>), cuts them into the Translate stage's chunks (<c>chunks.json</c>,
/// <see cref="ChunkPlanner"/>) and sets the job's counters (strings, chunks, translations already stored). No agent
/// runs for an engine the toolkit reads: its extraction is complete by construction, and the Glossary stage's agent
/// reads every string anyway.
/// An unknown engine, one switched off on this OS or without a tool fails the stage before any agent time is spent,
/// until the agent can write its own extractor (task 57).
/// </summary>
public sealed class ExtractStage(RecipeTools tools, ChunkBudget? budget = null) : IGenerationStage
{
    public GenerationStage Stage => GenerationStage.Extract;

    public Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(Run(context, cancellationToken));
    }

    private GenerationStepResult Run(GenerationStepContext context, CancellationToken cancellationToken)
    {
        var kind = new EngineDetector().Detect(context.CopyFolder).Kind;
        if (Problem(kind) is { } problem)
        {
            context.Log(problem);
            return new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, problem);
        }

        var tool = tools.For(kind)!;
        RecipeExtraction extraction;
        try
        {
            extraction = tool.Extract(context.CopyFolder, cancellationToken);
        }
        catch (RecipeException e)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, e.Message);
        }

        if (extraction.Strings.Count == 0)
        {
            return new GenerationStepResult.Failed(GenerationFailureKind.Toolkit, $"The toolkit found no text to translate in this {kind.DisplayName} game.");
        }

        var source = SourceStrings.Of(kind, extraction);
        var chunks = ChunkPlanner.Plan(tool.Id, source.Strings, budget);
        int translated;
        try
        {
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
            return new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"source.json can't be written: {e.Message}");
        }

        context.Log($"Extracted {source.Strings.Count} strings from {source.Files.Count} files with {tool.Id} {tool.Version.ToString(3)} ({kind.DisplayName}): {chunks.Count} chunks"
            + (translated > 0 ? $", {translated} already translated." : "."));
        return new GenerationStepResult.StageDone(new GenerationCounters(source.Strings.Count, translated, chunks.Count, 0));
    }

    /// <summary>Why the toolkit can't extract games of <paramref name="kind"/>, or <c>null</c>.</summary>
    private string? Problem(EngineKind kind) =>
        kind.Engine == GameEngine.Unknown
            ? "The game's engine wasn't recognized, so the toolkit can't read its strings; translating such games (the agent writing its own extractor) comes in a later version."
            : tools.IsDisabled(kind)
            ? $"The toolkit doesn't handle {kind.DisplayName} games on this system for now; translating them (the agent writing its own extractor) comes in a later version."
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
