using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Publishing;

/// <summary>
/// The one-click publish (task 61, design §7) from the result strip or Packs → Mine. <see cref="Publish"/> shows the
/// dialogs only when needed: the first-time dialog (GitHub sign-in + CC BY-SA 4.0 consent) while signed out or the
/// license was never accepted; the similar-pack dialog when the registry index has packs of the game + language
/// (<see cref="SimilarPacks.Find"/>; the most relevant one is shown). Then the pack is sent
/// (<see cref="IPackPublishing"/>): one publish at a time, its steps followed by the strip, the Mine row and an Activity
/// running item; the outcome is a toast and an Activity feed entry. The PR the client records in the pack's report makes
/// the pack <see cref="PublishPhase.Checking"/>, the index listing its revision <see cref="PublishPhase.Published"/>
/// (<see cref="PublishStatuses.Of"/>); a failure is kept in memory until the next try. Publishing is never offered
/// before the pack was tried in game. Task 62: the <see cref="PullRequestTracker"/> follows the PR (CI failed, merged,
/// closed); <see cref="Resubmit"/> (<c>Fix and resubmit</c>) sends the pack again to the same PR, the branch rebuilt
/// on the registry's head. <see cref="Changed"/> tells the pages (any thread).
/// </summary>
public sealed class PublishFlow
{
    private readonly IPackPublishing _publisher;
    private readonly PullRequestTracker _tracker;
    private readonly IGitHubHost _github;
    private readonly SettingsStore _settings;
    private readonly PackIndex _packIndex;
    private readonly LocalPacks _localPacks;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ActivityStore _activity;
    private readonly ISystemShell _shell;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger<PublishFlow> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PublishResult.Failed> _failures = [];
    private PublishRun? _running;
    private ActivityTask? _task;

    /// <summary>A pack went to the registry (its PR is open); any thread. Translation requests follow it (task 74).</summary>
    public event EventHandler<Core.Packs.PackManifest>? Submitted;

    public PublishFlow(
        IPackPublishing publisher,
        PullRequestTracker tracker,
        IGitHubHost github,
        SettingsStore settings,
        PackIndex packIndex,
        LocalPacks localPacks,
        IDialogService dialogs,
        ToastService toasts,
        ActivityStore activity,
        ISystemShell shell,
        Localizer localizer,
        IUiDispatcher ui,
        TimeProvider time,
        ILogger<PublishFlow> logger)
    {
        _publisher = publisher;
        _tracker = tracker;
        _github = github;
        _settings = settings;
        _packIndex = packIndex;
        _localPacks = localPacks;
        _dialogs = dialogs;
        _toasts = toasts;
        _activity = activity;
        _shell = shell;
        _localizer = localizer;
        _ui = ui;
        _time = time;
        _logger = logger;
        _tracker.Changed += (_, game) => Changed?.Invoke(this, game);
    }

    /// <summary>A publish started, moved on a step or ended, for the game's pages; any thread.</summary>
    public event EventHandler<GameId>? Changed;

    public RegistryRepo Registry => _publisher.Registry;

    /// <summary>A publish runs (one at a time).</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>The pack's state for the strip and Mine (reads the pack's report as given and the current index).</summary>
    public PublishStatus StatusOf(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        lock (_lock)
        {
            var tracked = pack.Report.Published is { } publication ? _tracker.Find(publication) : null;
            return PublishStatuses.Of(pack, _packIndex.Current?.Index, _running, _failures.GetValueOrDefault(PublishStatuses.KeyOf(pack)), tracked);
        }
    }

    /// <summary>
    /// <c>Publish</c> (UI thread): the dialogs that are needed, then the send. Nothing happens for a pack not tried in game,
    /// one already sent, or while another publish runs. After a failure that a new sign-in fixes (signed out, revoked,
    /// refused access) the app signs out first, so the dialog asks GitHub again.
    /// </summary>
    public void Publish(Game game, LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        var status = StatusOf(pack);
        if (IsBusy || !PublishStatuses.CanPublish(pack, status))
        {
            return;
        }

        if (status.Failure is { } failure && PublishTexts.NeedsSignIn(failure.Problem) && _github.Account is not null)
        {
            _ = SignOutThenAskAsync(game, pack);
            return;
        }

        if (PublishRules.NeedsSignInDialog(_github.Account is not null, _settings.Current.PublishLicenseAccepted))
        {
            _dialogs.Show<PublishSignInDialogViewModel>(new PublishSignInRequest(() => ChooseTarget(game, pack)));
            return;
        }

        ChooseTarget(game, pack);
    }

    /// <summary>
    /// <c>Fix and resubmit</c> (UI thread, task 62): the pack checked again with the registry's own checks and pushed to
    /// the rejected PR's branch, rebuilt as one commit on the registry's head (only the pack's folder, a fresh patch
    /// upload), so CI runs again on the same PR. Signed out (or the license never accepted), the first-time dialog comes
    /// first. Nothing happens unless CI rejected the PR, or while another publish runs.
    /// </summary>
    public void Resubmit(Game game, LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        var status = StatusOf(pack);
        if (IsBusy || !PublishStatuses.CanResubmit(status))
        {
            return;
        }

        var target = PublishStatuses.TargetOf(status.Publication!);
        if (PublishRules.NeedsSignInDialog(_github.Account is not null, _settings.Current.PublishLicenseAccepted))
        {
            _dialogs.Show<PublishSignInDialogViewModel>(new PublishSignInRequest(() => _ = SendAsync(game, Fresh(pack), target, fresh: true)));
            return;
        }

        _ = SendAsync(game, Fresh(pack), target, fresh: true);
    }

    /// <summary>Opens the PR or the Discussion in the browser.</summary>
    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _shell.OpenUrl(url);
    }

    /// <summary><c>Copy link</c>: the pack's link (its Discussion, else the PR) to the clipboard, with a toast.</summary>
    public async Task CopyLinkAsync(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (await _shell.CopyTextAsync(url.AbsoluteUri))
        {
            _ui.Post(() => _toasts.Show(new ToastViewModel(ToastTone.Ok, _localizer[nameof(Strings.Publish_Toast_LinkCopied)], [])));
        }
    }

    /// <summary>
    /// Sends the pack to <paramref name="target"/> (after the dialogs). Returns once the publish ended; <c>null</c> when
    /// another one runs.
    /// </summary>
    /// <param name="fresh">A resubmit: an open PR's branch is rebuilt on the registry's head (<see cref="PublishRequest.Fresh"/>).</param>
    public async Task<PublishResult?> SendAsync(Game game, LocalPack pack, PublishTarget target, bool fresh = false)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(target);
        var key = PublishStatuses.KeyOf(pack);
        var index = _packIndex.Current?.Index;
        var existing = target is PublishTarget.RevisionOf revisionOf
            ? index?.PacksFor(pack.Manifest.Game, pack.Manifest.Language).FirstOrDefault(p => p.Id == revisionOf.PackId)
            : null;
        var run = new PublishRun(key, null, existing?.Title ?? pack.Manifest.Title, existing is null ? null : existing.Revision + 1);
        lock (_lock)
        {
            if (_running is not null)
            {
                return null;
            }

            _running = run;
            _failures.Remove(key);
        }

        var started = _time.GetUtcNow();
        Moved(game, run, started);
        // Steps arrive on the publisher's thread; Progress<T> hands them to the caller's (UI) context, a late one is dropped.
        // With a UI context (the app) the steps go to it, like Progress<T> does; without one (view-model tests) they run
        // on the publisher's thread before it goes on, never on the thread pool beside the caller.
        Action<PublishStep> onStep = step =>
        {
            PublishRun moved;
            lock (_lock)
            {
                // Steps only move forward.
                if (_running is null || _running.Key != key || (_running.Step is { } current && step <= current))
                {
                    return;
                }

                moved = _running = _running with { Step = step };
            }

            Moved(game, moved, started);
        };
        IProgress<PublishStep> progress = SynchronizationContext.Current is null
            ? new InlineProgress<PublishStep>(onStep)
            : new Progress<PublishStep>(onStep);

        PublishResult result;
        try
        {
            _logger.LogInformation("Publishing {Pack} of {Game} to {Registry} as {Target}", key, game.Id, _publisher.Registry, target);
            result = await Task.Run(() => _publisher.PublishAsync(new PublishRequest(pack, target, _publisher.Registry, index, game.Name, fresh), progress));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The client never throws for GitHub or disk state; anything else is a bug, still shown as a failure.
            _logger.LogError(e, "Publishing {Pack} failed", key);
            result = new PublishResult.Failed(null, PublishProblem.Unexpected, e.Message);
        }

        lock (_lock)
        {
            _running = null;
            if (result is PublishResult.Failed failed)
            {
                _failures[key] = failed;
            }
        }

        EndTask();
        Report(game, pack, run, result);
        if (result is PublishResult.Published)
        {
            // CI starts over on the new head: the tracker drops the old result and reads the PR again soon.
            _tracker.Sent(Fresh(pack));
        }

        // The client wrote the PR into the pack's report: Mine and the result read it again.
        _localPacks.Refresh(game.Id);
        Changed?.Invoke(this, game.Id);
        return result;
    }

    private LocalPack Fresh(LocalPack pack) => _localPacks.Find(pack.Manifest.Game, pack.Manifest.Language, pack.Manifest.Id) ?? pack;

    private async Task SignOutThenAskAsync(Game game, LocalPack pack)
    {
        await _github.SignOutAsync();
        _ui.Post(() => _dialogs.Show<PublishSignInDialogViewModel>(new PublishSignInRequest(() => ChooseTarget(game, pack))));
    }

    /// <summary>
    /// The similar-pack dialog when the index has packs of the game + language, else the send right away. A pack whose PR
    /// is still open (a try to push to it failed) goes to that PR again, without asking.
    /// </summary>
    private void ChooseTarget(Game game, LocalPack pack)
    {
        var fresh = Fresh(pack);
        if (fresh.Report.Published is { State: Core.Publishing.PullRequestState.Open } open)
        {
            _ = SendAsync(game, fresh, PublishStatuses.TargetOf(open), fresh: true);
            return;
        }

        var similar = _packIndex.Current?.Index is { } index ? SimilarPacks.Find(index, fresh) : [];
        if (similar.Count == 0)
        {
            _ = SendAsync(game, fresh, new PublishTarget.NewPack());
            return;
        }

        _dialogs.Show<SimilarPackDialogViewModel>(new SimilarPackRequest(fresh, similar[0], target => _ = SendAsync(game, fresh, target)));
    }

    /// <summary>The step moved: the pages and the Activity running item follow.</summary>
    private void Moved(Game game, PublishRun run, DateTimeOffset started)
    {
        var task = new ActivityTask(
            $"publish:{run.Key}",
            ActivityTaskKind.Publish,
            game.Id,
            game.Name,
            _localizer.Format(nameof(Strings.Publish_Sending_Title), PublishTexts.Target(run.TargetTitle, run.TargetRevision, _localizer)),
            run.Step is { } step ? PublishTexts.Step(step, _localizer) : _localizer[nameof(Strings.Publish_Step_Account)],
            run.Step switch
            {
                PublishStep.Fork => 0.1,
                PublishStep.Commit => 0.4,
                PublishStep.PullRequest => 0.8,
                _ => 0.0,
            },
            started);
        lock (_lock)
        {
            _task = task;
        }

        _activity.SetTask(task);
        Changed?.Invoke(this, game.Id);
    }

    private void EndTask()
    {
        ActivityTask? task;
        lock (_lock)
        {
            task = _task;
            _task = null;
        }

        if (task is not null)
        {
            _activity.EndTask(task.Id);
        }
    }

    /// <summary>The outcome as a toast and an Activity feed entry.</summary>
    private void Report(Game game, LocalPack pack, PublishRun run, PublishResult result)
    {
        var what = PublishTexts.Target(run.TargetTitle, run.TargetRevision, _localizer);
        switch (result)
        {
            case PublishResult.Published published:
                Submitted?.Invoke(this, published.Manifest);
                var detail = _localizer.Format(published.Resubmitted ? nameof(Strings.Publish_Resubmitted_Detail) : nameof(Strings.Publish_Checking_Title), published.PullRequest);
                _activity.Add(ActivityEvent.Create(ActivityEventKind.PackSubmitted, _time.GetUtcNow()) with
                {
                    Game = game.Id,
                    GameName = game.Name,
                    PackId = published.Manifest.Id,
                    PackTitle = published.Manifest.Title,
                    Revision = published.Manifest.Revision,
                    Detail = detail,
                });
                _ui.Post(() => _toasts.Show(new ToastViewModel(
                    ToastTone.Ok,
                    _localizer.Format(published.Resubmitted ? nameof(Strings.Publish_Toast_Resubmitted) : nameof(Strings.Publish_Toast_Sent), what, published.PullRequest),
                    [new ToastAction(_localizer[nameof(Strings.Publish_OpenPr)], () => Open(published.PullRequestUrl), "IconExternal")],
                    key: $"publish:{run.Key}")));
                break;
            case PublishResult.Failed failed:
                var reason = PublishTexts.Failure(failed, _publisher.Registry, _localizer);
                _activity.Add(ActivityEvent.Create(ActivityEventKind.PublishFailed, _time.GetUtcNow()) with
                {
                    Game = game.Id,
                    GameName = game.Name,
                    PackId = pack.Manifest.Id,
                    PackTitle = run.TargetTitle,
                    Revision = run.TargetRevision ?? pack.Manifest.Revision,
                    Detail = reason,
                    CanRetry = true,
                });
                _ui.Post(() => _toasts.Show(new ToastViewModel(
                    ToastTone.Bad,
                    _localizer.Format(nameof(Strings.Publish_Toast_Failed), what) + " " + reason,
                    [],
                    key: $"publish:{run.Key}")));
                break;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
