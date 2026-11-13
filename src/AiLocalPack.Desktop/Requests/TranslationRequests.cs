using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Ratings;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Requests;

/// <summary>
/// Translation requests in the registry's Discussions with the signed-in player's token (task 74). Behind an interface so
/// view models are tested without GitHub; never throws but for cancellation.
/// </summary>
public interface IRequestDiscussions
{
    Task<RatingResult<RequestState>> ReadAsync(DiscussionRef discussion, CancellationToken cancellationToken = default);

    Task<RatingResult<RequestState>> AskAsync(RequestKey key, string gameName, Uri? known, string? note, CancellationToken cancellationToken = default);

    Task<RatingResult<RequestState>> WithdrawAsync(DiscussionRef discussion, CancellationToken cancellationToken = default);

    Task<RatingResult<string>> ClaimAsync(DiscussionRef discussion, RequestClaim claim, string? commentId, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IRequestDiscussions"/> over <see cref="TranslationRequestClient"/>, the token read from the keychain for each call.</summary>
internal sealed class RequestDiscussions(TranslationRequestClient client, GitHubSession session, RegistryRepo repo) : IRequestDiscussions
{
    public Task<RatingResult<RequestState>> ReadAsync(DiscussionRef discussion, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.ReadAsync(discussion, token, cancellationToken), cancellationToken);

    public Task<RatingResult<RequestState>> AskAsync(RequestKey key, string gameName, Uri? known, string? note, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.AskAsync(repo, key, gameName, known, note, token, cancellationToken), cancellationToken);

    public Task<RatingResult<RequestState>> WithdrawAsync(DiscussionRef discussion, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.WithdrawAsync(discussion, token, cancellationToken), cancellationToken);

    public Task<RatingResult<string>> ClaimAsync(DiscussionRef discussion, RequestClaim claim, string? commentId, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.ClaimAsync(discussion, claim, commentId, token, cancellationToken), cancellationToken);

    private async Task<RatingResult<T>> WithTokenAsync<T>(Func<string, Task<RatingResult<T>>> call, CancellationToken cancellationToken)
    {
        var token = await session.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return token switch
        {
            { IsOk: true, Value: { Length: > 0 } value } => await call(value).ConfigureAwait(false),
            { Status: SecretStatus.NotFound } => RatingResult<T>.Failed(new RatingError(RatingProblem.NotSignedIn, "Not signed in to GitHub.")),
            _ => RatingResult<T>.Failed(new RatingError(RatingProblem.Keychain, token.ToString())),
        };
    }
}

/// <summary>Who is translating a request, as the page shows it.</summary>
/// <param name="ByMe">The signed-in player's own claim.</param>
public sealed record RequestClaimView(string By, RequestClaimState State, GenerationStage? Stage, int Percent, DateTimeOffset Updated, bool ByMe);

/// <summary>
/// A request as the game page and the library show it: the index's numbers, the player's own record and, when read,
/// the Discussion itself (fresher than the hourly index).
/// </summary>
/// <param name="Votes">Players asking (the player included when they asked).</param>
/// <param name="Mine">The player asked (their record, or their 👍 on the Discussion).</param>
/// <param name="Notes">Players' notes from the Discussion; empty until it was read.</param>
public sealed record RequestView(
    RequestKey Key,
    int Votes,
    bool Mine,
    DateTimeOffset? Since,
    Uri? Discussion,
    RequestClaimView? Claim,
    IReadOnlyList<RequestComment> Notes)
{
    /// <summary>Someone asked for it (or the player did).</summary>
    public bool Exists => Votes > 0 || Mine;

    /// <summary>The other players asking.</summary>
    public int Others => Math.Max(0, Votes - (Mine ? 1 : 0));
}

/// <summary>An ask or withdraw on its way to GitHub (<see cref="Error"/> <c>null</c>), or the last one that failed.</summary>
public sealed record RequestSend(bool Withdrawing, RatingError? Error = null)
{
    public bool IsRunning => Error is null;
}

/// <summary>
/// The player's side of translation requests (task 74). Asking (or adding a vote) and withdrawing go to the request's
/// Discussion (<see cref="IRequestDiscussions"/>), after the GitHub sign-in when needed; the player's own requests are
/// kept (<see cref="TranslationRequestStore"/>) so a new pack list tells them once when one is taken and once when it is
/// done (Activity feed + OS notification). A job started with "tell them I'm translating" keeps a claim comment in the
/// Discussion, edited when its stage changes, every 25 % and when it completes, is cancelled or its pack is published.
/// One send per request at a time; its state (running, failed with the reason) is in memory (<see cref="SendOf"/>).
/// <see cref="Changed"/> tells the pages (any thread). Never throws.
/// </summary>
public sealed class TranslationRequests
{
    /// <summary>A claim's percent is sent again when it crosses one of these steps.</summary>
    public const int PercentStep = 25;

    private readonly TranslationRequestStore _store;
    private readonly IRequestDiscussions _discussions;
    private readonly PackIndex _packIndex;
    private readonly GameCatalog _catalog;
    private readonly IGitHubHost _github;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ActivityStore _activity;
    private readonly ISystemShell _shell;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger<TranslationRequests> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<RequestKey, RequestSend> _sends = [];
    private readonly Dictionary<RequestKey, RequestState> _live = [];
    private readonly HashSet<RequestKey> _synced = [];
    private readonly Dictionary<string, RequestClaim> _claimQueue = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claiming = new(StringComparer.Ordinal);

    public TranslationRequests(
        TranslationRequestStore store,
        IRequestDiscussions discussions,
        PackIndex packIndex,
        GameCatalog catalog,
        GenerationService jobs,
        PublishFlow publish,
        IGitHubHost github,
        IDialogService dialogs,
        ToastService toasts,
        ActivityStore activity,
        ISystemShell shell,
        Localizer localizer,
        IUiDispatcher ui,
        TimeProvider time,
        ILogger<TranslationRequests> logger)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(publish);
        _store = store;
        _discussions = discussions;
        _packIndex = packIndex;
        _catalog = catalog;
        _github = github;
        _dialogs = dialogs;
        _toasts = toasts;
        _activity = activity;
        _shell = shell;
        _localizer = localizer;
        _ui = ui;
        _time = time;
        _logger = logger;
        _store.Changed += (_, game) => Changed?.Invoke(this, game);
        _packIndex.Changed += (_, snapshot) => OnIndexChanged(snapshot.Index);
        jobs.Changed += (_, job) => OnJobChanged(job);
        publish.Submitted += (_, manifest) => OnSubmitted(manifest);
        OnIndexChanged(_packIndex.Current?.Index);
    }

    /// <summary>A request of the game changed (record, live read, send); any thread.</summary>
    public event EventHandler<GameId>? Changed;

    /// <summary>The signed-in player's login, or <c>null</c>.</summary>
    public string? Login => _github.Account?.Login;

    /// <summary>The request of <paramref name="game"/> into <paramref name="language"/> as the page shows it.</summary>
    public RequestView ViewOf(GameId game, string language)
    {
        var key = new RequestKey(game, language);
        var indexed = _packIndex.Current?.Index?.RequestFor(game, language);
        var asked = _store.Find(key);
        RequestState? live;
        lock (_lock)
        {
            live = _live.GetValueOrDefault(key);
        }

        var now = _time.GetUtcNow();
        var login = Login;
        var mine = asked is not null || live?.Voted == true;
        var votes = live?.Votes ?? indexed?.Votes ?? 0;
        if (mine && votes == 0)
        {
            votes = 1;
        }

        RequestClaimView? claim = null;
        if (live?.LiveClaim(now) is { } fresh)
        {
            claim = new RequestClaimView(fresh.Comment.Author!, fresh.Claim.State, fresh.Claim.Stage, fresh.Claim.Percent, fresh.Comment.SaidAt, fresh.Comment.ByViewer);
        }
        else if (live is null && indexed?.Claim is { } c && RequestRules.IsLive(c.State, c.Updated, now))
        {
            claim = new RequestClaimView(c.By, c.State, c.Stage, c.Percent, c.Updated, string.Equals(c.By, login, StringComparison.OrdinalIgnoreCase));
        }

        return new RequestView(
            key,
            votes,
            mine,
            asked?.AskedAt ?? indexed?.Opened,
            live?.Url ?? indexed?.Discussion ?? asked?.Discussion,
            claim,
            live?.Notes ?? []);
    }

    /// <summary>The player's record of the request (asked, taken, done), or <c>null</c>.</summary>
    public AskedRequest? Mine(GameId game, string language) => _store.Find(new RequestKey(game, language));

    /// <summary>The ask or withdraw running for the request, or the last one that failed; <c>null</c> when none.</summary>
    public RequestSend? SendOf(GameId game, string language)
    {
        lock (_lock)
        {
            return _sends.GetValueOrDefault(new RequestKey(game, language));
        }
    }

    /// <summary>
    /// <c>Request translation</c> / <c>Me too</c> after the dialog (UI thread): asks, after the sign-in when needed. The
    /// toast says it went out.
    /// </summary>
    public void Ask(Game game, string language, string? note)
    {
        ArgumentNullException.ThrowIfNull(game);
        var key = new RequestKey(game.Id, language);
        WithSignIn(key, () => AskAsync(key, game.Name, note));
    }

    /// <summary><c>Request translation</c> / <c>Me too</c> on the Text tab (UI thread): the request dialog.</summary>
    public void ShowAskDialog(Game game, string language) => _dialogs.Show<RequestDialogViewModel>(new RequestDialogRequest(game, language));

    /// <summary><c>Withdraw my request</c> (UI thread): takes the player's 👍 back and forgets the record.</summary>
    public void Withdraw(GameId game, string language)
    {
        var key = new RequestKey(game, language);
        WithSignIn(key, () => WithdrawAsync(key));
    }

    /// <summary>The ✕ on the Text tab's "your request is done" strip.</summary>
    public void DismissDone(GameId game, string language) => _store.SeenDone(new RequestKey(game, language));

    /// <summary>
    /// Reads the request's Discussion once per request while the app runs, only when signed in and the request has one
    /// (the player's vote and the notes show at once, fresher than the hourly index). Failures are only logged.
    /// </summary>
    public async Task SyncAsync(GameId game, string language)
    {
        var key = new RequestKey(game, language);
        if (_github.Account is null || !DiscussionRef.TryParse(ViewOf(game, language).Discussion, out var discussion))
        {
            return;
        }

        lock (_lock)
        {
            if (!_synced.Add(key) || _sends.ContainsKey(key))
            {
                return;
            }
        }

        var read = await _discussions.ReadAsync(discussion);
        if (!read.IsOk)
        {
            _logger.LogInformation("Reading the request {Discussion} of {Key} failed: {Problem} {Message}", discussion, key, read.Error.Problem, read.Error.Message);
            return;
        }

        Remember(key, read.Value);
    }

    /// <summary>
    /// The start form's "tell them I'm translating" (UI thread): the job keeps a claim on the request, posted now (after
    /// the sign-in when needed) and edited as the job goes on.
    /// </summary>
    public void Take(GenerationJob job, Uri discussion)
    {
        ArgumentNullException.ThrowIfNull(job);
        var key = new RequestKey(job.Game, job.Language);
        _store.SetClaim(new RequestClaimRecord(job.Id, key, discussion));
        _logger.LogInformation("Job {Job} takes the request {Key} ({Discussion})", job.Id, key, discussion);
        var claim = ClaimOf(job);
        if (_github.Account is null)
        {
            _dialogs.Show<RatingSignInDialogViewModel>(new RatingSignInRequest(() => Queue(job.Id, claim), RatingSignInPurpose.Request));
            return;
        }

        Queue(job.Id, claim);
    }

    /// <summary>What a job's claim says: working (stage, %), built when completed, stopped when cancelled.</summary>
    public static RequestClaim ClaimOf(GenerationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var key = new RequestKey(job.Game, job.Language);
        return job.Status switch
        {
            GenerationJobStatus.Cancelled => new RequestClaim(key, RequestClaimState.Stopped),
            GenerationJobStatus.Completed => new RequestClaim(key, RequestClaimState.Built, null, 100, job.Agent, job.Options.Quality),
            _ => new RequestClaim(key, RequestClaimState.Working, job.Stage, GenerationProgress.Of(job).Percent, job.Agent, job.Options.Quality),
        };
    }

    /// <summary>The claim is worth sending again: another state or stage, or the percent crossed a <see cref="PercentStep"/>.</summary>
    public static bool IsNews(RequestClaim? sent, RequestClaim next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return sent is null
            || sent.State != next.State
            || sent.Stage != next.Stage
            || sent.Percent / PercentStep != next.Percent / PercentStep;
    }

    private void WithSignIn(RequestKey key, Func<Task> run)
    {
        if (IsRunning(key))
        {
            return;
        }

        if (_github.Account is null)
        {
            _dialogs.Show<RatingSignInDialogViewModel>(new RatingSignInRequest(() => _ = run(), RatingSignInPurpose.Request));
            return;
        }

        _ = run();
    }

    private async Task AskAsync(RequestKey key, string gameName, string? note)
    {
        if (!Begin(key, new RequestSend(false)))
        {
            return;
        }

        var result = await _discussions.AskAsync(key, gameName, ViewOf(key.Game, key.Language).Discussion, note);
        if (!result.IsOk)
        {
            await FailAsync(key, new RequestSend(false), result.Error, () => AskAsync(key, gameName, note));
            return;
        }

        _logger.LogInformation("Asked for {Key} in {Discussion} ({Votes} votes)", key, result.Value.Url, result.Value.Votes);
        Remember(key, result.Value);
        _store.Ask(key, result.Value.Url, _time.GetUtcNow());
        End(key, null);
        var url = result.Value.Url;
        _ui.Post(() => _toasts.Show(new ToastViewModel(
            ToastTone.Ok,
            _localizer[nameof(Strings.Request_Toast_Sent)],
            [new ToastAction(_localizer[nameof(Strings.Request_OpenOnGitHub)], () => _shell.OpenUrl(url), "IconExternal")],
            $"request:{key}")));
    }

    private async Task WithdrawAsync(RequestKey key)
    {
        if (!Begin(key, new RequestSend(true)))
        {
            return;
        }

        if (!DiscussionRef.TryParse(ViewOf(key.Game, key.Language).Discussion, out var discussion))
        {
            _store.Withdraw(key);
            End(key, null);
            return;
        }

        var result = await _discussions.WithdrawAsync(discussion);
        if (!result.IsOk && result.Error.Problem != RatingProblem.NotFound)
        {
            await FailAsync(key, new RequestSend(true), result.Error, () => WithdrawAsync(key));
            return;
        }

        _logger.LogInformation("Withdrew the request {Key}", key);
        if (result.IsOk)
        {
            Remember(key, result.Value);
        }

        _store.Withdraw(key);
        End(key, null);
    }

    /// <summary>
    /// A failed send: a revoked token or a keychain without one signs out and asks again (the send follows the sign-in);
    /// anything else is kept as the request's failed send with the reason.
    /// </summary>
    private async Task FailAsync(RequestKey key, RequestSend send, RatingError error, Func<Task> again)
    {
        _logger.LogWarning("Sending the request {Key} failed: {Problem} {Message}", key, error.Problem, error.Message);
        if (error.Problem is RatingProblem.Unauthorized or RatingProblem.NotSignedIn)
        {
            End(key, null);
            if (_github.Account is not null)
            {
                await _github.SignOutAsync();
            }

            _ui.Post(() => _dialogs.Show<RatingSignInDialogViewModel>(new RatingSignInRequest(() => _ = again(), RatingSignInPurpose.Request)));
            return;
        }

        End(key, send with { Error = error });
    }

    private void Remember(RequestKey key, RequestState state)
    {
        lock (_lock)
        {
            _live[key] = state;
        }

        Changed?.Invoke(this, key.Game);
    }

    private bool IsRunning(RequestKey key)
    {
        lock (_lock)
        {
            return _sends.TryGetValue(key, out var send) && send.IsRunning;
        }
    }

    private bool Begin(RequestKey key, RequestSend send)
    {
        lock (_lock)
        {
            if (_sends.TryGetValue(key, out var running) && running.IsRunning)
            {
                return false;
            }

            _sends[key] = send;
        }

        Changed?.Invoke(this, key.Game);
        return true;
    }

    private void End(RequestKey key, RequestSend? failed)
    {
        lock (_lock)
        {
            if (failed is null)
            {
                _sends.Remove(key);
            }
            else
            {
                _sends[key] = failed;
            }
        }

        Changed?.Invoke(this, key.Game);
    }

    // ---------- The player's requests: taken, done ----------

    /// <summary>A new pack list: the player's requests with a pack now are done; one with a new translator is taken. Told once each.</summary>
    private void OnIndexChanged(RegistryIndex? index)
    {
        if (index is null)
        {
            return;
        }

        foreach (var asked in _store.Asked())
        {
            var key = asked.Key;
            var name = _catalog.Find(key.Game)?.Name ?? key.Game.ToString();
            if (index.PacksFor(key.Game, key.Language) is { Count: > 0 } packs)
            {
                if (asked.DoneAt is null)
                {
                    var pack = packs.OrderByDescending(p => p.Status == PackStatus.Verified).ThenByDescending(p => p.Rating.Likes).First();
                    _store.Done(key, _time.GetUtcNow());
                    _logger.LogInformation("The request {Key} is done: {Pack}", key, pack.Id);
                    _activity.Add(ActivityEvent.Create(ActivityEventKind.RequestDone, _time.GetUtcNow()) with
                    {
                        Game = key.Game,
                        GameName = name,
                        PackId = pack.Id,
                        PackTitle = pack.Title,
                        Detail = pack.Author.GitHub,
                        Link = pack.Discussion ?? asked.Discussion,
                        Key = $"request-done:{key}",
                    });
                }

                continue;
            }

            if (index.RequestFor(key.Game, key.Language)?.Claim is { } claim
                && RequestRules.IsLive(claim.State, claim.Updated, _time.GetUtcNow())
                && !string.Equals(claim.By, Login, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(claim.By, asked.TakenBy, StringComparison.OrdinalIgnoreCase))
            {
                _store.Taken(key, claim.By);
                _logger.LogInformation("The request {Key} was taken by {By}", key, claim.By);
                _activity.Add(ActivityEvent.Create(ActivityEventKind.RequestTaken, _time.GetUtcNow()) with
                {
                    Game = key.Game,
                    GameName = name,
                    Detail = claim.By,
                    Link = index.RequestFor(key.Game, key.Language)?.Discussion ?? asked.Discussion,
                    Key = $"request-taken:{key}:{claim.By.ToLowerInvariant()}",
                });
            }
        }
    }

    // ---------- Claims of the player's jobs ----------

    private void OnJobChanged(GenerationJob job)
    {
        if (_store.FindClaim(job.Id) is not { } record)
        {
            return;
        }

        var next = ClaimOf(job);
        if (IsNews(record.Sent, next))
        {
            Queue(job.Id, next);
        }
    }

    /// <summary>The player's pack was sent to the registry: claims on its request say so.</summary>
    private void OnSubmitted(PackManifest manifest)
    {
        foreach (var record in _store.Claims().Where(c => c.Key.Game == manifest.Game && c.Key.Language == manifest.Language))
        {
            Queue(record.Job, new RequestClaim(record.Key, RequestClaimState.Published, null, 100, record.Sent?.Agent, record.Sent?.Quality));
        }
    }

    /// <summary>Sends the job's newest claim; one send per job at a time, a claim that came meanwhile goes next.</summary>
    private void Queue(string job, RequestClaim claim)
    {
        lock (_lock)
        {
            _claimQueue[job] = claim;
            if (!_claiming.Add(job))
            {
                return;
            }
        }

        _ = SendClaimsAsync(job);
    }

    private async Task SendClaimsAsync(string job)
    {
        while (true)
        {
            RequestClaim claim;
            lock (_lock)
            {
                if (!_claimQueue.Remove(job, out claim!))
                {
                    _claiming.Remove(job);
                    return;
                }
            }

            if (_store.FindClaim(job) is not { } record || !DiscussionRef.TryParse(record.Discussion, out var discussion) || _github.Account is null)
            {
                continue;
            }

            var sent = await _discussions.ClaimAsync(discussion, claim, record.Comment);
            if (!sent.IsOk)
            {
                // Tried again with the job's next news.
                _logger.LogWarning("Sending the claim of job {Job} on {Key} failed: {Problem} {Message}", job, record.Key, sent.Error.Problem, sent.Error.Message);
                continue;
            }

            _logger.LogInformation("Claim of job {Job} on {Key}: {State} {Stage} {Percent}%", job, record.Key, claim.State, claim.Stage, claim.Percent);
            if (claim.State is RequestClaimState.Stopped or RequestClaimState.Published)
            {
                _store.RemoveClaim(job);
            }
            else
            {
                _store.SetClaim(record with { Comment = sent.Value, Sent = claim });
            }

            lock (_lock)
            {
                _synced.Remove(record.Key);
            }

            Changed?.Invoke(this, record.Key.Game);
        }
    }
}
