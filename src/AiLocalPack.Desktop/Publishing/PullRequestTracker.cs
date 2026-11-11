using AiLocalPack.Core.Activity;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Publishing;

/// <summary>Reads a pack PR's state and CI from GitHub (task 62). Behind an interface so view models are tested without GitHub; never throws but for cancellation.</summary>
public interface IPullRequestStatus
{
    Task<PullRequestCheckResult> CheckAsync(RegistryRepo registry, int number, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IPullRequestStatus"/> over Core's <see cref="PullRequestStatusClient"/>: with the player's token while signed
/// in (the remembered account), else anonymous (public data, a smaller rate limit).
/// </summary>
internal sealed class PullRequestStatusSource(PullRequestStatusClient client, GitHubSession session) : IPullRequestStatus
{
    public async Task<PullRequestCheckResult> CheckAsync(RegistryRepo registry, int number, CancellationToken cancellationToken = default)
    {
        string? token = null;
        if (session.Account is not null && await session.GetTokenAsync(cancellationToken).ConfigureAwait(false) is { Status: SecretStatus.Ok } secret)
        {
            token = secret.Value;
        }

        return await client.CheckAsync(registry, number, token, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>What the app last read about a pack's PR.</summary>
/// <param name="Status">The last successful read; <c>null</c> before the first.</param>
/// <param name="Failure">Why CI rejected it, when <see cref="PullRequestStatus.Ci"/> is <see cref="CiState.Failed"/>.</param>
public sealed record TrackedPullRequest(PullRequestStatus? Status, CiFailure? Failure);

/// <summary>
/// Follows the player's open pack PRs (task 62): every local pack whose report records a PR still open is read
/// (<see cref="IPullRequestStatus"/>) at start-up, then when due (<see cref="PullRequestPolling.Next"/>: often while CI
/// runs, less later, backing off after errors and waiting out a rate limit), checked every <see cref="TickInterval"/>
/// while the app runs (window or tray). <b>CI failed</b> → the reason (<see cref="CiFailures.Explain"/>) and an Activity
/// decision <see cref="ActivityEventKind.PullRequestFailed"/> (once per head commit), settled when CI passes again, the
/// PR is merged or closed, or the player resubmits. <b>Merged</b> / <b>closed</b> → recorded in the pack's report (no
/// longer read) and a feed event; after a merge the registry index is fetched again every few minutes for an hour until
/// it lists the revision (<i>Published</i>). An index that lists a published pack as <c>verified</c> adds
/// <see cref="ActivityEventKind.PackVerified"/> once. <see cref="Changed"/> tells the pages (any thread). Never throws.
/// </summary>
public sealed class PullRequestTracker : IDisposable
{
    /// <summary>How often the tracker looks for a PR that is due.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    /// <summary>How often the index is fetched again while a merged revision isn't in it yet.</summary>
    public static readonly TimeSpan IndexWait = TimeSpan.FromMinutes(3);

    /// <summary>How long after a merge the index is waited for (then the usual refreshes take over).</summary>
    public static readonly TimeSpan IndexWaitLimit = TimeSpan.FromHours(1);

    private readonly IPullRequestStatus _status;
    private readonly LocalPacks _localPacks;
    private readonly PackIndex _packIndex;
    private readonly ActivityStore _activity;
    private readonly GameLibrary _library;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private readonly ILogger<PullRequestTracker> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _indexAsked = DateTimeOffset.MinValue;
    private bool _started;

    public PullRequestTracker(
        IPullRequestStatus status,
        LocalPacks localPacks,
        PackIndex packIndex,
        ActivityStore activity,
        GameLibrary library,
        Localizer localizer,
        TimeProvider time,
        ILogger<PullRequestTracker> logger)
    {
        _status = status;
        _localPacks = localPacks;
        _packIndex = packIndex;
        _activity = activity;
        _library = library;
        _localizer = localizer;
        _time = time;
        _logger = logger;
        _packIndex.Changed += (_, snapshot) => OnIndexChanged(snapshot.Index);
    }

    /// <summary>A PR's state changed (the game's pages read it again); any thread.</summary>
    public event EventHandler<GameId>? Changed;

    /// <summary>What was last read about the publication's PR, or <c>null</c> (not read yet, or no longer followed).</summary>
    public TrackedPullRequest? Find(LocalPackPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        lock (_lock)
        {
            return _entries.TryGetValue(KeyOf(publication), out var entry) && entry.Status is not null ? new TrackedPullRequest(entry.Status, entry.Failure) : null;
        }
    }

    /// <summary>Reads the open PRs now, then follows them on the clock. Once; later calls do nothing.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _ = LoopAsync(_stop.Token);
    }

    /// <summary>
    /// The pack was sent again (<c>Fix and resubmit</c>, <c>Publish again</c>): its PR's old CI result is dropped, its
    /// decision settled, and it is read again within <see cref="TickInterval"/> (CI starts over on the new head).
    /// </summary>
    public void Sent(LocalPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Report.Published is not { } publication)
        {
            return;
        }

        lock (_lock)
        {
            _entries[KeyOf(publication)] = new Entry(pack, publication) { Since = _time.GetUtcNow(), Next = _time.GetUtcNow() };
        }

        SettleDecisions(pack.Manifest.Game, publication);
        Changed?.Invoke(this, pack.Manifest.Game);
    }

    /// <summary>Reads every PR that is due (all of them with <paramref name="all"/>); one run at a time, a second call waits.</summary>
    public async Task CheckAsync(bool all = false, CancellationToken cancellationToken = default)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var packs = _localPacks.ListAll();
            List<Entry> due;
            lock (_lock)
            {
                Sync(packs, now);
                due = [.. _entries.Values.Where(e => all || e.Next is not { } next || next <= now)];
            }

            foreach (var entry in due)
            {
                await ReadAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            WaitForIndex(packs, now);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The client never throws for GitHub or disk state; anything else is a bug, logged and tried again later.
            _logger.LogError(e, "Following the pack pull requests failed");
        }
        finally
        {
            _checking.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _checking.Dispose();
    }

    private static string KeyOf(LocalPackPublication publication) => $"{publication.Registry}#{publication.PullRequest}";

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(all: true, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TickInterval, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckAsync(all: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Follows exactly the packs whose report has an open PR (a new one added, a merged / deleted / sent-again one dropped).</summary>
    private void Sync(IReadOnlyList<LocalPack> packs, DateTimeOffset now)
    {
        var open = packs.Where(p => p.Report.Published is { State: PullRequestState.Open }).ToDictionary(p => KeyOf(p.Report.Published!), StringComparer.Ordinal);
        foreach (var key in _entries.Keys.Where(k => !open.ContainsKey(k)).ToList())
        {
            _entries.Remove(key);
        }

        foreach (var (key, pack) in open)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Pack = pack;
            }
            else
            {
                _entries[key] = new Entry(pack, pack.Report.Published!) { Since = pack.Report.Published!.At > now ? now : pack.Report.Published.At };
            }
        }
    }

    private async Task ReadAsync(Entry entry, CancellationToken cancellationToken)
    {
        var publication = entry.Publication;
        if (!RegistryRepo.TryParse(publication.Registry, out var registry))
        {
            lock (_lock)
            {
                _entries.Remove(KeyOf(publication));
            }

            return;
        }

        var result = await _status.CheckAsync(registry, publication.PullRequest, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var game = entry.Pack.Manifest.Game;
        switch (result)
        {
            case PullRequestCheckResult.Failed failed:
                lock (_lock)
                {
                    entry.Errors++;
                    entry.RetryAt = failed.Error.RetryAt;
                    entry.Next = PullRequestPolling.Next(Poll(entry), now);
                }

                _logger.LogInformation("PR {Registry}#{Number} not read ({Problem} {Status}: {Message}); next at {Next}", registry, publication.PullRequest, failed.Error.Problem, failed.Error.Status, failed.Error.Message, entry.Next);
                return;
            case PullRequestCheckResult.Checked { Status: var status }:
                PullRequestStatus? before;
                CiFailure? failure = status.Ci == CiState.Failed ? CiFailures.Explain(status.Annotations, PackFolder(entry.Pack.Manifest.Game, entry.Pack.Manifest.Language, publication.PackId)) : null;
                lock (_lock)
                {
                    before = entry.Status;
                    entry.Errors = 0;
                    entry.RetryAt = null;
                    entry.Status = status;
                    entry.Failure = failure;
                    if (before is null || before.State != status.State || before.Ci != status.Ci || before.HeadSha != status.HeadSha)
                    {
                        entry.Since = now;
                    }

                    entry.Next = PullRequestPolling.Next(Poll(entry), now);
                }

                Record(entry, before, status, failure);
                if (before is null || before.State != status.State || before.Ci != status.Ci || before.HeadSha != status.HeadSha)
                {
                    _logger.LogInformation("PR {Registry}#{Number}: {State}, CI {Ci} at {Head}{Reason}", registry, publication.PullRequest, status.State, status.Ci, status.HeadSha, failure is null ? "" : $" ({failure.Kind}: {failure.FirstProblem})");
                    Changed?.Invoke(this, game);
                }

                return;
        }
    }

    private static PullRequestPoll Poll(Entry entry) => new(entry.Status?.State, entry.Status?.Ci, entry.Since, entry.Errors, entry.RetryAt);

    private static string PackFolder(GameId game, string language, string id) => $"{RegistryLayout.GamesFolder}/{RegistryLayout.GameFolder(game)}/{language}/{id}";

    /// <summary>The Activity side of a read: a rejected head → a decision; passing again, merged or closed → settled; merged / closed → recorded + feed.</summary>
    private void Record(Entry entry, PullRequestStatus? before, PullRequestStatus status, CiFailure? failure)
    {
        var pack = entry.Pack;
        var publication = entry.Publication;
        var game = pack.Manifest.Game;
        if (status.State != PullRequestState.Open)
        {
            SettleDecisions(game, publication);
            _localPacks.RecordPullRequest(pack, publication.PullRequest, status.State, status.ClosedAt ?? _time.GetUtcNow());
            lock (_lock)
            {
                _entries.Remove(KeyOf(publication));
            }

            _activity.Add(Event(status.State == PullRequestState.Merged ? ActivityEventKind.PackMerged : ActivityEventKind.PullRequestClosed, pack, publication) with
            {
                Key = $"pr-{status.State.ToString().ToLowerInvariant()}:{KeyOf(publication)}",
            });
            if (status.State == PullRequestState.Merged)
            {
                _indexAsked = DateTimeOffset.MinValue;
            }

            return;
        }

        if (failure is not null)
        {
            if (before is not { Ci: CiState.Failed } || before.HeadSha != status.HeadSha)
            {
                _activity.Add(Event(ActivityEventKind.PullRequestFailed, pack, publication) with
                {
                    Key = $"pr-failed:{KeyOf(publication)}@{status.HeadSha}",
                    Detail = PublishTexts.CiFailure(failure, _localizer),
                });
            }

            return;
        }

        if (before is { Ci: CiState.Failed })
        {
            SettleDecisions(game, publication);
        }
    }

    private ActivityEvent Event(ActivityEventKind kind, LocalPack pack, LocalPackPublication publication) =>
        ActivityEvent.Create(kind, _time.GetUtcNow()) with
        {
            Game = pack.Manifest.Game,
            GameName = _library.Catalog.Find(pack.Manifest.Game)?.Name,
            PackId = publication.PackId,
            PackTitle = pack.Manifest.Title,
            Revision = publication.Revision,
            PullRequest = publication.PullRequest,
            Link = publication.PullRequestUrl,
        };

    private void SettleDecisions(GameId game, LocalPackPublication publication) =>
        _activity.ResolveGame(game, e => e.Kind == ActivityEventKind.PullRequestFailed && e.PullRequest == publication.PullRequest && e.Link == publication.PullRequestUrl);

    /// <summary>A merged revision the index doesn't list yet (merged within the hour): the index is fetched again every few minutes.</summary>
    private void WaitForIndex(IReadOnlyList<LocalPack> packs, DateTimeOffset now)
    {
        var index = _packIndex.Current?.Index;
        var waiting = packs.Any(p => p.Report.Published is { State: PullRequestState.Merged } publication
            && now - (publication.StateAt ?? publication.At) < IndexWaitLimit
            && !Lists(index, p.Manifest, publication));
        if (!waiting || now - _indexAsked < IndexWait)
        {
            return;
        }

        _indexAsked = now;
        _logger.LogInformation("A merged pack isn't in the registry index yet; fetching it again");
        _ = _packIndex.RefreshAsync();
    }

    private static bool Lists(RegistryIndex? index, PackManifest local, LocalPackPublication publication) =>
        index?.PacksFor(local.Game, local.Language).Any(p => p.Id == publication.PackId && p.Revision >= publication.Revision) == true;

    /// <summary>A published pack the index now lists as verified: one feed event.</summary>
    private void OnIndexChanged(RegistryIndex? index)
    {
        if (index is null)
        {
            return;
        }

        try
        {
            foreach (var pack in _localPacks.ListAll())
            {
                if (pack.Report.Published is not { } publication)
                {
                    continue;
                }

                var live = index.PacksFor(pack.Manifest.Game, pack.Manifest.Language).FirstOrDefault(p => p.Id == publication.PackId && p.Revision >= publication.Revision);
                if (live is { Status: PackStatus.Verified })
                {
                    _activity.Add(Event(ActivityEventKind.PackVerified, pack, publication) with
                    {
                        Key = $"verified:{publication.Registry}/{RegistryLayout.GameFolder(pack.Manifest.Game)}/{pack.Manifest.Language}/{publication.PackId}",
                        PackTitle = live.Title,
                        Revision = live.Revision,
                        Link = live.Discussion ?? publication.PullRequestUrl,
                    });
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Local packs not read after the index changed");
        }
    }

    private sealed class Entry(LocalPack pack, LocalPackPublication publication)
    {
        public LocalPack Pack { get; set; } = pack;

        public LocalPackPublication Publication { get; } = publication;

        public PullRequestStatus? Status { get; set; }

        public CiFailure? Failure { get; set; }

        public int Errors { get; set; }

        public DateTimeOffset? RetryAt { get; set; }

        public DateTimeOffset Since { get; set; }

        public DateTimeOffset? Next { get; set; }
    }
}
