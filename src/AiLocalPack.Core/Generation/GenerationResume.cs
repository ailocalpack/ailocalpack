using AiLocalPack.Core.Activity;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// When jobs paused by the subscription limit go on by themselves (task 47): at their <see cref="GenerationJob.ResumeAt"/>
/// (the limit's reset time). Only jobs the limit paused: a job the player paused, an interrupted or failed one waits for
/// the player. Pure.
/// </summary>
public static class GenerationResumeSchedule
{
    /// <summary>Jobs to resume at <paramref name="now"/>, earliest reset first.</summary>
    public static IReadOnlyList<GenerationJobEntry> Due(IEnumerable<GenerationJobEntry> jobs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return [.. Waiting(jobs).Where(e => e.Job.ResumeAt <= now)];
    }

    /// <summary>The earliest reset time of the jobs waiting for one, or <c>null</c>.</summary>
    public static DateTimeOffset? Next(IEnumerable<GenerationJobEntry> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return Waiting(jobs).Select(e => e.Job.ResumeAt).FirstOrDefault();
    }

    private static IEnumerable<GenerationJobEntry> Waiting(IEnumerable<GenerationJobEntry> jobs) => jobs
        .Where(e => e.Job is { Status: GenerationJobStatus.Paused, PauseReason: GenerationPauseReason.Limit, ResumeAt: not null })
        .OrderBy(e => e.Job.ResumeAt);
}

/// <summary>
/// Resumes jobs paused by the limit once their reset time comes. Time comes from the <see cref="TimeProvider"/>; the
/// host calls <see cref="TickAsync"/> on a timer (the app every 30 s, <c>gen run --wait</c> until the reset) and tests
/// after moving a fake clock. A job another runner holds is skipped this tick.
/// </summary>
/// <param name="jobs">The jobs on disk (<see cref="GenerationJobStore.List"/>).</param>
/// <param name="resume">Resumes one (<see cref="GenerationJobs.Resume"/>); its exceptions are swallowed, the next tick tries again.</param>
public sealed class GenerationResumeScheduler(TimeProvider time, Func<IReadOnlyList<GenerationJobEntry>> jobs, Func<GenerationJobEntry, Task> resume)
{
    /// <summary>Resumes every due job; returns those it resumed. Never throws.</summary>
    public async Task<IReadOnlyList<GenerationJobEntry>> TickAsync()
    {
        IReadOnlyList<GenerationJobEntry> due;
        try
        {
            due = GenerationResumeSchedule.Due(jobs(), time.GetUtcNow());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var resumed = new List<GenerationJobEntry>();
        foreach (var entry in due)
        {
            try
            {
                await resume(entry).ConfigureAwait(false);
                resumed.Add(entry);
            }
            catch (Exception e) when (e is GenerationJobException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Busy, blocked or gone: the next tick looks again.
            }
        }

        return resumed;
    }
}

/// <summary>
/// Shows a running job as the Activity panel's running task (design §5), with Pause / Cancel, and ends the task when the
/// job stops running. The words come from the host (the app's UI language). Never throws.
/// </summary>
/// <param name="words">Title and step line of a running job, from its progress.</param>
public sealed class GenerationActivity(ActivityStore store, Func<GenerationJob, GenerationProgress, (string Title, string? Step)> words)
{
    public static string TaskId(GenerationJob job) => "generation:" + job.Id;

    /// <summary>Follows <paramref name="run"/> until it ends.</summary>
    public void Follow(GenerationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var started = run.Job.RunningSince ?? run.Job.Updated;
        Publish(run, run.Job, started);
        run.Changed += (_, job) => Publish(run, job, started);
    }

    private void Publish(GenerationRun run, GenerationJob job, DateTimeOffset started)
    {
        if (job.Status != GenerationJobStatus.Running)
        {
            store.EndTask(TaskId(job));
            return;
        }

        var progress = GenerationProgress.Of(job);
        var (title, step) = words(job, progress);
        store.SetTask(new ActivityTask(TaskId(job), ActivityTaskKind.Generation, job.Game, job.GameName, title, step, progress.Percent / 100.0, started)
        {
            Pause = run.Pause,
            Cancel = run.Cancel,
        });
    }
}

/// <summary>A job as the library card and the game page's header see it (<see cref="Library.GenerationJobState"/>).</summary>
public static class GenerationJobStates
{
    /// <summary>The open job's state; <c>null</c> for a cancelled or completed job (no badge).</summary>
    public static Library.GenerationJobState? Of(GenerationJob job, string? pauseReason = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.IsOpen)
        {
            return null;
        }

        var progress = GenerationProgress.Of(job);
        return new Library.GenerationJobState(
            progress.Percent,
            job.Status != GenerationJobStatus.Running,
            job.Status == GenerationJobStatus.Running ? progress.Remaining : null,
            pauseReason);
    }
}
