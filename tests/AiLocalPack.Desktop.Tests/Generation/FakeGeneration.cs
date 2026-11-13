using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>
/// The start form's estimate as the test sets it. By default every game is a Unity game the toolkit reads: 1,200 strings,
/// a 3 GB working copy with 120 GB free. Every call is counted.
/// </summary>
internal sealed class FakeEstimates : IGenerationEstimates
{
    public const long Gb = 1024L * 1024 * 1024;

    public Func<Game, CancellationToken, Task<GenerationSizeEstimate>> Estimate { get; set; } = (_, _) => Task.FromResult(Of());

    public int Calls { get; private set; }

    /// <summary>The update base table of the last call (task 56), <c>null</c> for a new translation.</summary>
    public StringTable? LastBase { get; private set; }

    /// <summary>An estimate: the text from the toolkit (or a guess), the copy and the disk.</summary>
    public static GenerationSizeEstimate Of(
        int strings = 1_200,
        long words = 9_000,
        TextSizeSource source = TextSizeSource.Toolkit,
        EngineKind? engine = null,
        long copyBytes = 3 * Gb,
        long? freeBytes = 120 * Gb,
        bool canClone = false,
        string? toolkitError = null)
    {
        var basis = source == TextSizeSource.Toolkit ? EstimateBasis.Toolkit : EstimateBasis.FileSizes;
        var work = GenerationEstimateModel.Estimate(new TextSize(strings, words, words * 6), basis);
        var plan = new WorkingCopyPlan("/games/x", WorkingCopyScope.WithoutMedia, [], [], copyBytes, 0, 0);
        var needed = (canClone ? 0 : copyBytes) + WorkingCopies.Margin;
        return new GenerationSizeEstimate(
            engine ?? new EngineKind(GameEngine.Unity, "mono"),
            source,
            null,
            toolkitError,
            work,
            plan,
            new WorkingCopySpace(needed, freeBytes, canClone),
            0);
    }

    public Task<GenerationSizeEstimate> EstimateAsync(Game game, StringTable? updateBase, CancellationToken cancellationToken)
    {
        Calls++;
        LastBase = updateBase;
        return Estimate(game, cancellationToken);
    }
}

/// <summary>
/// Stage handlers for tests, so no test runs the real pipeline: by default every stage holds its step until the job
/// is stopped (the job stays running at Setup); <see cref="Hold"/> = <c>false</c> lets every stage finish at once.
/// The jobs the factory made stages for are recorded.
/// </summary>
internal sealed class FakeGenerationStages : IGenerationStagesFactory
{
    public bool Hold { get; set; } = true;

    /// <summary>Thrown by <see cref="Create"/> instead of making stages (the app's own path unknown).</summary>
    public Exception? Refuse { get; set; }

    public List<GenerationJob> Jobs { get; } = [];

    /// <summary>What a stage's step ends with instead of finishing (a limit, a failure); <c>null</c> = it finishes.</summary>
    public Func<GenerationStage, GenerationStepResult?>? Results { get; set; }

    public GenerationStages Create(Game game, GenerationJobEntry entry)
    {
        if (Refuse is { } e)
        {
            throw e;
        }

        Jobs.Add(entry.Job);
        return new GenerationStages(GenerationSteps.StudyStages.Select(stage => (IGenerationStage)new HeldStage(stage, Hold, Results)));
    }

    private sealed class HeldStage(GenerationStage stage, bool hold, Func<GenerationStage, GenerationStepResult?>? results) : IGenerationStage
    {
        public GenerationStage Stage => stage;

        public async Task<GenerationStepResult> RunStepAsync(GenerationStepContext context, CancellationToken cancellationToken)
        {
            if (hold)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (results?.Invoke(stage) is { } result)
            {
                return result;
            }

            return stage switch
            {
                GenerationStage.Extract => new GenerationStepResult.StageDone(new GenerationCounters(10, 0, 1, 0)),
                GenerationStage.Translate => new GenerationStepResult.ChunkDone(10),
                _ => new GenerationStepResult.StageDone(),
            };
        }
    }
}

/// <summary>The update's base pack as the test sets it (task 56); by default no update is possible (nothing to build on).</summary>
internal sealed class FakeUpdateBases : IUpdateBases
{
    public Func<Game, string, Task<UpdateBaseLoad?>> Load { get; set; } = (_, _) => Task.FromResult<UpdateBaseLoad?>(null);

    public int Calls { get; private set; }

    public Task<UpdateBaseLoad?> LoadAsync(Game game, string language, CancellationToken cancellationToken)
    {
        Calls++;
        return Load(game, language);
    }
}
