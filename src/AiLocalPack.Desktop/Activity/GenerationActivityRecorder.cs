using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.GamePage;

namespace AiLocalPack.Desktop.Activity;

/// <summary>
/// Feeds the Activity panel from the app's generation jobs (task 53, design §5): a running job is a running task
/// ("Generating · Русский", the stage with its counter and ETA, the bar, ⏸ that pauses it; no ✕: cancelling asks on the
/// Generation tab), ended when it stops; a job paused by the agent's limit is a decision (<c>Resume now</c>; it resumes by
/// itself at <see cref="ActivityEvent.Until"/>), added once per pause and settled when the job runs again or ends. Never
/// throws.
/// </summary>
public sealed class GenerationActivityRecorder
{
    private readonly ActivityStore _store;
    private readonly GenerationService _jobs;
    private readonly Localizer _localizer;
    private readonly Lock _lock = new();
    private readonly Dictionary<GameId, GenerationJob> _running = [];

    public GenerationActivityRecorder(ActivityStore store, GenerationService jobs, Localizer localizer)
    {
        _store = store;
        _jobs = jobs;
        _localizer = localizer;
        _jobs.Changed += (_, job) => OnChanged(job);
        _localizer.CultureChanged += (_, _) => Reword();
        SettleStale();
    }

    public static string TaskId(GenerationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return GenerationActivity.TaskId(job);
    }

    /// <summary>The key of one limit pause: the job and its reset time, so a later pause of the same job is a new decision.</summary>
    public static string PauseKey(GenerationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return $"generation-paused:{job.Id}:{job.ResumeAt?.ToUnixTimeSeconds()}";
    }

    private void OnChanged(GenerationJob job)
    {
        if (job.Status == GenerationJobStatus.Running)
        {
            lock (_lock)
            {
                _running[job.Game] = job;
            }

            Publish(job);
        }
        else
        {
            lock (_lock)
            {
                _running.Remove(job.Game);
            }

            _store.EndTask(TaskId(job));
        }

        if (job is { Status: GenerationJobStatus.Paused, PauseReason: GenerationPauseReason.Limit })
        {
            _store.Add(ActivityEvent.Create(ActivityEventKind.GenerationPaused, job.Updated) with
            {
                Game = job.Game,
                GameName = job.GameName,
                Until = job.ResumeAt,
                Key = PauseKey(job),
            });
        }
        else
        {
            _store.ResolveGame(job.Game, e => e.Kind == ActivityEventKind.GenerationPaused);
        }
    }

    private void Publish(GenerationJob job)
    {
        var progress = GenerationProgress.Of(job);
        var title = _localizer.Format(nameof(Strings.Activity_Task_Generation), LanguageNames.Native(job.Language));
        var step = GenerationTexts.StageName(job.Stage, _localizer);
        if (job.Stage == GenerationStage.Translate && progress is { StageDone: { } done, StageTotal: { } total })
        {
            step += " · " + _localizer.Format(nameof(Strings.Gen_Progress_Counter_Chunks), Math.Min(done + 1, total), total);
        }

        if (progress.Remaining is { } left && left > TimeSpan.Zero)
        {
            step = _localizer.Format(nameof(Strings.Gen_Progress_Eta), step, GenerationTexts.Duration(left, _localizer));
        }

        var game = job.Game;
        _store.SetTask(new ActivityTask(TaskId(job), ActivityTaskKind.Generation, game, job.GameName, title, step, progress.Percent / 100.0, job.RunningSince ?? job.Updated)
        {
            Pause = () => _jobs.Pause(game),
        });
    }

    private void Reword()
    {
        List<GenerationJob> running;
        lock (_lock)
        {
            running = [.. _running.Values];
        }

        foreach (var job in running)
        {
            Publish(job);
        }
    }

    /// <summary>A limit decision whose job isn't paused by the limit any more (resumed or cancelled by the CLI meanwhile) is settled.</summary>
    private void SettleStale()
    {
        foreach (var decision in _store.Decisions.Where(e => e.Kind == ActivityEventKind.GenerationPaused && e.Game is not null))
        {
            if (_jobs.FindOpen(decision.Game!) is not { Status: GenerationJobStatus.Paused, PauseReason: GenerationPauseReason.Limit })
            {
                _store.Resolve(decision.Id);
            }
        }
    }
}
