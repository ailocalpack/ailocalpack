using System.Globalization;
using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;

namespace AiLocalPack.Desktop.Activity;

/// <summary>
/// Feeds the <see cref="ActivityStore"/> (task 38): the running install, re-apply or uninstall as a task, the outcome of
/// each operation the player started (<see cref="Record"/>, called by <see cref="InstallFlow"/>), and what the re-check
/// after a game update found (<see cref="InstalledPackMonitor.Rechecked"/>): re-applied by itself → feed; re-apply
/// failed, pack incompatible, pack not applied → decisions, each added once per installed record. Any successful
/// operation on a game, or a re-check finding its pack intact, settles that game's pack decisions. Later tasks
/// (generation, publishing) add their own producers. Never throws.
/// </summary>
public sealed class ActivityRecorder
{
    private readonly ActivityStore _store;
    private readonly PackInstallService _service;
    private readonly InstalledPackStore _installed;
    private readonly GameLibrary _library;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private (string Id, DateTimeOffset Started)? _task;

    public ActivityRecorder(
        ActivityStore store,
        PackInstallService service,
        InstalledPackMonitor monitor,
        InstalledPackStore installed,
        GameLibrary library,
        Localizer localizer,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _store = store;
        _service = service;
        _installed = installed;
        _library = library;
        _localizer = localizer;
        _time = time;
        _service.Changed += (_, _) => SyncTask();
        _localizer.CultureChanged += (_, _) => SyncTask();
        monitor.Rechecked += (_, result) => OnRechecked(result);
    }

    public ActivityStore Store => _store;

    /// <summary>The outcome of an operation the player started (a refusal because another one runs isn't recorded).</summary>
    public void Record(InstallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Record(outcome, automatic: false, key: null);
    }

    private void Record(InstallOutcome outcome, bool automatic, string? key)
    {
        if (outcome.Error is { Kind: InstallErrorKind.Busy })
        {
            return;
        }

        if (outcome.Succeeded)
        {
            _store.ResolveGame(outcome.Game.Id, e => ActivityEvents.IsPackProblem(e.Kind));
        }

        var kind = (outcome.Kind, outcome.Succeeded, automatic) switch
        {
            (InstallOperationKind.Install, true, _) => ActivityEventKind.PackInstalled,
            (InstallOperationKind.Install, false, _) => ActivityEventKind.InstallFailed,
            (InstallOperationKind.Uninstall, true, _) => ActivityEventKind.PackUninstalled,
            (InstallOperationKind.Uninstall, false, _) => ActivityEventKind.UninstallFailed,
            (_, true, true) => ActivityEventKind.PackAutoReapplied,
            (_, true, false) => ActivityEventKind.PackReapplied,
            (_, false, true) => ActivityEventKind.AutoReapplyFailed,
            _ => ActivityEventKind.ReapplyFailed,
        };
        var activity = ActivityEvent.Create(kind, _time.GetUtcNow()) with
        {
            Game = outcome.Game.Id,
            GameName = outcome.Game.Name,
            PackId = outcome.PackId,
            PackTitle = outcome.Title,
            Revision = outcome.Installed?.Revision,
            Key = key,
        };
        if (outcome.Error is { } error)
        {
            activity = activity with
            {
                Detail = InstallTexts.Reason(error, _localizer),
                Log = outcome.Log,
                FilesRestored = error.FilesRestored,
                CanRetry = error.CanRetry,
            };
        }

        _store.Add(activity);
    }

    private void OnRechecked(PackRecheckResult result)
    {
        var record = _installed.Find(result.Game.Id);
        if (result.Reapply is { } outcome)
        {
            Record(outcome, automatic: true, outcome.Succeeded ? null : Key(record, InstalledPackProblem.ReapplyFailed));
            return;
        }

        switch (result.Problem)
        {
            case InstalledPackProblem.None when result.Decision == PackRecheckDecision.Intact:
                _store.ResolveGame(result.Game.Id, e => ActivityEvents.IsPackProblem(e.Kind));
                break;
            case InstalledPackProblem.Incompatible:
                AddProblem(ActivityEventKind.PackIncompatible, InstalledPackProblem.Incompatible);
                break;
            case InstalledPackProblem.Wiped:
                AddProblem(ActivityEventKind.PackNotApplied, InstalledPackProblem.Wiped);
                break;
        }

        void AddProblem(ActivityEventKind kind, InstalledPackProblem problem)
        {
            if (record is null)
            {
                return;
            }

            _store.Add(ActivityEvent.Create(kind, _time.GetUtcNow()) with
            {
                Game = result.Game.Id,
                GameName = result.Game.Name,
                PackId = record.PackId,
                PackTitle = result.Title,
                Revision = record.Revision,
                Key = Key(record, problem),
            });
        }
    }

    /// <summary>One finding per installed record: a later install or re-apply makes a new record (new install time).</summary>
    private static string? Key(InstalledPack? record, InstalledPackProblem problem) => record is null ? null
        : string.Create(CultureInfo.InvariantCulture, $"pack-{problem}:{record.Game}:{record.PackId}:{record.Revision}:{record.InstalledAt.UtcTicks}");

    /// <summary>The service's operation as the one running task (the service runs one at a time).</summary>
    private void SyncTask()
    {
        lock (_lock)
        {
            var operation = _service.Current;
            if (operation is null)
            {
                if (_task is { } ended)
                {
                    _task = null;
                    _store.EndTask(ended.Id);
                }

                return;
            }

            var id = $"install:{operation.Game}:{operation.Kind}:{operation.PackId}";
            if (_task is not { } current || current.Id != id)
            {
                if (_task is { } previous)
                {
                    _store.EndTask(previous.Id);
                }

                _task = (id, _time.GetUtcNow());
            }

            _store.SetTask(new ActivityTask(
                id,
                operation.Kind switch
                {
                    InstallOperationKind.Uninstall => ActivityTaskKind.Uninstall,
                    InstallOperationKind.Reapply => ActivityTaskKind.Reapply,
                    _ => ActivityTaskKind.Install,
                },
                operation.Game,
                _library.Catalog.Find(operation.Game)?.Name,
                InstallTexts.Title(operation, _localizer),
                InstallTexts.Step(operation.Step, _localizer),
                operation.Percent / 100.0,
                _task.Value.Started));
        }
    }
}
