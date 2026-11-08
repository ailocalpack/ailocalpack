namespace AiLocalPack.Core.Generation;

/// <summary>How far a job is, for the progress card, the library badge and the Activity row.</summary>
/// <param name="Percent">Overall, 0–100 (100 only when completed).</param>
/// <param name="StageDone">The stage's counter (chunks of Translate), else <c>null</c>.</param>
/// <param name="StageTotal">Its total.</param>
/// <param name="Remaining">Time left while it runs; <c>null</c> when nothing tells yet.</param>
public sealed record GenerationProgress(int Percent, int? StageDone, int? StageTotal, TimeSpan? Remaining)
{
    /// <summary>
    /// Share of the whole job per stage. Translate is by far the longest (agent calls per chunk); the others are rough.
    /// </summary>
    public static readonly IReadOnlyDictionary<GenerationStage, double> Weights = new Dictionary<GenerationStage, double>
    {
        [GenerationStage.Setup] = 0.05,
        [GenerationStage.Extract] = 0.05,
        [GenerationStage.Glossary] = 0.10,
        [GenerationStage.Translate] = 0.70,
        [GenerationStage.Build] = 0.10,
    };

    /// <summary>What Build is reckoned to take in the ETA once chunks are measured (a rebuild and a short test run).</summary>
    public static readonly TimeSpan BuildAllowance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Percent = the weights of the stages done + the current stage's weight × its share done (chunks for Translate,
    /// nothing otherwise); kept under 100 until completed. ETA: once chunks are done, the mean time per finished chunk ×
    /// the chunks left (+ <see cref="BuildAllowance"/> before Build); before that, the size estimate's time × the share
    /// left; none when the job has no estimate. Pure.
    /// </summary>
    public static GenerationProgress Of(GenerationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Stage == GenerationStage.Done)
        {
            return new GenerationProgress(100, null, null, TimeSpan.Zero);
        }

        var counters = job.Counters;
        var stageShare = job.Stage == GenerationStage.Translate && counters.Chunks > 0
            ? Math.Clamp((double)counters.ChunksDone / counters.Chunks, 0, 1)
            : 0;
        var fraction = Weights.Where(w => w.Key < job.Stage).Sum(w => w.Value) + Weights[job.Stage] * stageShare;
        var percent = Math.Min(99, (int)Math.Floor(fraction * 100 + 1e-9));
        var (done, total) = job.Stage == GenerationStage.Translate && counters.Chunks > 0
            ? (counters.ChunksDone, counters.Chunks)
            : ((int?)null, (int?)null);
        return new GenerationProgress(percent, done, total, TimeLeft(job, fraction));
    }

    private static TimeSpan? TimeLeft(GenerationJob job, double fraction)
    {
        var counters = job.Counters;
        if (counters.ChunksDone > 0 && job.ChunkTime > TimeSpan.Zero && job.Stage <= GenerationStage.Translate)
        {
            var perChunk = job.ChunkTime / counters.ChunksDone;
            return perChunk * Math.Max(0, counters.Chunks - counters.ChunksDone) + BuildAllowance;
        }

        if (job.Stage == GenerationStage.Build)
        {
            return BuildAllowance;
        }

        return job.EstimatedTime is { } estimate ? estimate * (1 - fraction) : null;
    }
}
