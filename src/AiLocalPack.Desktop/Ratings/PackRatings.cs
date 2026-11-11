using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Ratings;

/// <summary>What is being sent for a pack right now, or why the last send failed.</summary>
public enum RatingSendKind
{
    Like,
    Report,
}

/// <summary>A like or report on its way to GitHub (<see cref="Error"/> <c>null</c>), or the last one that failed.</summary>
/// <param name="Verdict">The report's verdict (<see cref="RatingSendKind.Report"/>).</param>
public sealed record RatingSend(RatingSendKind Kind, PackVerdict? Verdict, RatingError? Error = null)
{
    public bool IsRunning => Error is null;
}

/// <summary>
/// Ratings of installed registry packs (task 63): follows <see cref="GameSessions"/> (a session that ended with a
/// registry pack installed before it started marks the pack played, so the rating block and prompt appear), keeps the
/// player's marks (<see cref="PackRatingStore"/>), and sends likes and reports to the pack's Discussion
/// (<see cref="IPackDiscussions"/>). Signed out, the sign-in dialog comes first and the send follows it; a token GitHub
/// no longer accepts signs out and asks again. One send per pack at a time; its state (running, failed with the reason)
/// is in memory (<see cref="SendOf"/>). <see cref="Changed"/> tells the game's pages (any thread).
/// </summary>
public sealed class PackRatings
{
    private readonly PackRatingStore _store;
    private readonly InstalledPackStore _installed;
    private readonly IPackDiscussions _discussions;
    private readonly IGitHubHost _github;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ISystemShell _shell;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly OsEnvironment _env;
    private readonly TimeProvider _time;
    private readonly ILogger<PackRatings> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RatingSend> _sends = new(StringComparer.Ordinal);
    private readonly HashSet<string> _synced = new(StringComparer.Ordinal);

    public PackRatings(
        PackRatingStore store,
        InstalledPackStore installed,
        GameSessions sessions,
        IPackDiscussions discussions,
        IGitHubHost github,
        IDialogService dialogs,
        ToastService toasts,
        ISystemShell shell,
        Localizer localizer,
        IUiDispatcher ui,
        OsEnvironment env,
        TimeProvider time,
        ILogger<PackRatings> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _store = store;
        _installed = installed;
        _discussions = discussions;
        _github = github;
        _dialogs = dialogs;
        _toasts = toasts;
        _shell = shell;
        _localizer = localizer;
        _ui = ui;
        _env = env;
        _time = time;
        _logger = logger;
        _store.Changed += (_, game) => Changed?.Invoke(this, game);
        sessions.Changed += OnSessionChanged;
    }

    /// <summary>A pack's marks or its send changed; any thread.</summary>
    public event EventHandler<GameId>? Changed;

    /// <summary>The signed-in player's login (author check), or <c>null</c>.</summary>
    public string? Login => _github.Account?.Login;

    public PackRating? Find(GameId game, string language, string packId) => _store.Find(game, language, packId);

    /// <summary>The send running for the pack, or the last one that failed; <c>null</c> when none.</summary>
    public RatingSend? SendOf(GameId game, string language, string packId)
    {
        lock (_lock)
        {
            return _sends.GetValueOrDefault(Key(game, language, packId));
        }
    }

    /// <summary>"Later" on the prompt: hidden until the next session ends.</summary>
    public void Later(GameId game, string language, string packId, int revision)
    {
        _logger.LogInformation("Rating prompt for {Pack} rev {Revision} of {Game} put off", packId, revision, game);
        _store.Later(game, language, packId, revision, _time.GetUtcNow());
    }

    /// <summary><c>Like</c> / <c>Liked</c> (UI thread): gives or takes back the player's 👍, after the sign-in when needed.</summary>
    public void Like(Game game, RegistryPack pack, int revision, bool like)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        WithSignIn(game, pack, new RatingSend(RatingSendKind.Like, null), () => LikeAsync(game, pack, revision, like));
    }

    /// <summary>
    /// <c>Works</c> / <c>Doesn't work</c> (UI thread): posts the report of the installed <paramref name="revision"/> with the
    /// game's build and the player's text, after the sign-in when needed; a later report of the same revision replaces the
    /// earlier one. The toast thanks the player; <paramref name="addDetails"/> adds its <c>Add details</c> button.
    /// </summary>
    public void Report(Game game, RegistryPack pack, int revision, PackVerdict verdict, string? text, Action? addDetails = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        WithSignIn(game, pack, new RatingSend(RatingSendKind.Report, verdict), () => ReportAsync(game, pack, revision, verdict, text, addDetails));
    }

    /// <summary>
    /// Reads the player's 👍 and report of the pack from its Discussion, once per pack while the app runs and only when
    /// signed in (another machine may have sent them). Failures are only logged.
    /// </summary>
    public async Task SyncAsync(GameId game, RegistryPack pack, int revision)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var key = Key(game, pack.Language, pack.Id);
        if (_github.Account is null || !DiscussionRef.TryParse(pack.Discussion, out var discussion))
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
            _logger.LogInformation("Reading the Discussion {Discussion} of {Pack} failed: {Problem} {Message}", discussion, pack.Id, read.Error.Problem, read.Error.Message);
            return;
        }

        var state = read.Value;
        _store.SetLiked(game, pack.Language, pack.Id, revision, state.ViewerLiked);
        var about = new PackReport(PackVerdict.Works, game, pack.Language, pack.Id, revision, null);
        if (state.ViewerReport(about) is { } mine && Find(game, pack.Language, pack.Id) is { Reported: null })
        {
            _store.Reported(game, pack.Language, pack.Id, revision, mine.Report.Verdict, _time.GetUtcNow(), mine.Url);
        }
    }

    /// <summary>"0.1.0 macos": the app's version and the OS, in the report's small print.</summary>
    internal string AppText => $"{typeof(PackRatings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} {_env.Os.ToString().ToLowerInvariant()}";

    private void WithSignIn(Game game, RegistryPack pack, RatingSend send, Func<Task> run)
    {
        if (!DiscussionRef.TryParse(pack.Discussion, out _) || IsRunning(game.Id, pack))
        {
            return;
        }

        if (_github.Account is null)
        {
            _dialogs.Show<RatingSignInDialogViewModel>(new RatingSignInRequest(() => _ = run()));
            return;
        }

        _ = run();
    }

    private bool IsRunning(GameId game, RegistryPack pack)
    {
        lock (_lock)
        {
            return _sends.TryGetValue(Key(game, pack.Language, pack.Id), out var send) && send.IsRunning;
        }
    }

    private async Task LikeAsync(Game game, RegistryPack pack, int revision, bool like)
    {
        var send = new RatingSend(RatingSendKind.Like, null);
        if (!Begin(game.Id, pack, send) || !DiscussionRef.TryParse(pack.Discussion, out var discussion))
        {
            return;
        }

        var result = await _discussions.SetLikeAsync(discussion, like);
        if (result.IsOk)
        {
            _logger.LogInformation("{Action} {Pack} of {Game} in {Discussion}", like ? "Liked" : "Took back the like of", pack.Id, game.Id, discussion);
            End(game.Id, pack, null);
            _store.SetLiked(game.Id, pack.Language, pack.Id, revision, result.Value.Liked);
            return;
        }

        await FailAsync(game, pack, send, result.Error, () => LikeAsync(game, pack, revision, like));
    }

    private async Task ReportAsync(Game game, RegistryPack pack, int revision, PackVerdict verdict, string? text, Action? addDetails)
    {
        var send = new RatingSend(RatingSendKind.Report, verdict);
        if (!Begin(game.Id, pack, send) || !DiscussionRef.TryParse(pack.Discussion, out var discussion))
        {
            return;
        }

        var report = new PackReport(verdict, game.Id, pack.Language, pack.Id, revision, PackReport.BuildOf(game.Version), text, AppText);
        var result = await _discussions.ReportAsync(discussion, report);
        if (result.IsOk)
        {
            _logger.LogInformation(
                "Reported {Pack} rev {Revision} of {Game} as {Verdict} in {Discussion} ({How})",
                pack.Id, revision, game.Id, verdict, discussion, result.Value.Replaced ? "replaced the earlier report" : "new comment");
            End(game.Id, pack, null);
            _store.Reported(game.Id, pack.Language, pack.Id, revision, verdict, _time.GetUtcNow(), result.Value.Url ?? pack.Discussion);
            _ui.Post(() => _toasts.Show(new ToastViewModel(ToastTone.Ok, _localizer[nameof(Strings.Rating_Toast_Sent)], Actions(result.Value.Url ?? pack.Discussion, addDetails), $"rating:{game.Id}")));
            return;
        }

        await FailAsync(game, pack, send, result.Error, () => ReportAsync(game, pack, revision, verdict, text, addDetails));
    }

    private List<ToastAction> Actions(Uri? url, Action? addDetails)
    {
        var actions = new List<ToastAction>();
        if (addDetails is not null)
        {
            actions.Add(new ToastAction(_localizer[nameof(Strings.Rating_Toast_AddDetails)], addDetails));
        }

        if (url is not null)
        {
            actions.Add(new ToastAction(_localizer[nameof(Strings.Rating_Toast_Open)], () => _shell.OpenUrl(url), "IconExternal"));
        }

        return actions;
    }

    /// <summary>
    /// A failed send: a revoked token or a keychain without one signs out and asks again (the send follows the sign-in);
    /// anything else is kept as the pack's failed send with the reason.
    /// </summary>
    private async Task FailAsync(Game game, RegistryPack pack, RatingSend send, RatingError error, Func<Task> again)
    {
        _logger.LogWarning("Sending the {Kind} of {Pack} ({Game}) failed: {Problem} {Message}", send.Kind, pack.Id, game.Id, error.Problem, error.Message);
        if (error.Problem is RatingProblem.Unauthorized or RatingProblem.NotSignedIn)
        {
            End(game.Id, pack, null);
            if (_github.Account is not null)
            {
                await _github.SignOutAsync();
            }

            _ui.Post(() => _dialogs.Show<RatingSignInDialogViewModel>(new RatingSignInRequest(() => _ = again())));
            return;
        }

        End(game.Id, pack, send with { Error = error });
    }

    private bool Begin(GameId game, RegistryPack pack, RatingSend send)
    {
        lock (_lock)
        {
            var key = Key(game, pack.Language, pack.Id);
            if (_sends.TryGetValue(key, out var running) && running.IsRunning)
            {
                return false;
            }

            _sends[key] = send;
        }

        Changed?.Invoke(this, game);
        return true;
    }

    private void End(GameId game, RegistryPack pack, RatingSend? failed)
    {
        lock (_lock)
        {
            var key = Key(game, pack.Language, pack.Id);
            if (failed is null)
            {
                _sends.Remove(key);
            }
            else
            {
                _sends[key] = failed;
            }
        }

        Changed?.Invoke(this, game);
    }

    private void OnSessionChanged(object? sender, GameSessionEvent change)
    {
        if (change is not GameSessionEnded ended || _installed.Find(ended.Game) is not { } record || !RatingRules.CountsAsPlayed(record, ended))
        {
            return;
        }

        _logger.LogInformation("Played {Game} with {Pack} rev {Revision} ({Duration})", record.Game, record.PackId, record.Revision, ended.Duration);
        _store.Played(record.Game, record.Language, record.PackId, record.Revision, ended.EndedAt);
    }

    private static string Key(GameId game, string language, string packId) => $"{game}/{language}/{packId}";
}
