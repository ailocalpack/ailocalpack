using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Activity;

/// <summary>
/// What happened (design §5). The text is worded by the app from the kind and the event's data, so it follows the UI
/// language. Later tasks add generation, publishing and PR kinds; a kind this app doesn't know is skipped when read.
/// </summary>
public enum ActivityEventKind
{
    /// <summary>A pack was installed.</summary>
    PackInstalled,

    /// <summary>A pack was uninstalled, the original files restored.</summary>
    PackUninstalled,

    /// <summary>The player re-applied the installed pack.</summary>
    PackReapplied,

    /// <summary>The installed pack was re-applied by itself after a game update or a file check.</summary>
    PackAutoReapplied,

    /// <summary>An install the player started failed (files restored, or not: <see cref="ActivityEvent.Detail"/>).</summary>
    InstallFailed,

    /// <summary>An uninstall failed.</summary>
    UninstallFailed,

    /// <summary>A re-apply the player started failed.</summary>
    ReapplyFailed,

    /// <summary>The automatic re-apply after a game update failed: needs a decision (<c>Retry</c>, <c>Log</c>).</summary>
    AutoReapplyFailed,

    /// <summary>A game update made the installed pack incompatible: needs a decision (newer revision, translate new strings).</summary>
    PackIncompatible,

    /// <summary>
    /// A game update put the original files back and the pack still fits, but re-applying by itself is off: needs a
    /// decision (<c>Re-apply</c>).
    /// </summary>
    PackNotApplied,

    /// <summary>
    /// A generation job was paused by the agent's subscription limit (task 53): a decision (<c>Resume now</c>) until it
    /// goes on by itself at <see cref="ActivityEvent.Until"/>, the player resumes or cancels it.
    /// </summary>
    GenerationPaused,

    /// <summary>
    /// A generation job's glossary waits for the player's review (task 54): a decision (<c>Review glossary</c>) until
    /// the player continues, has it drafted again or cancels the job.
    /// </summary>
    GlossaryReview,

    /// <summary>A local pack was sent to the registry (task 61): its PR is open, CI checks it (<see cref="ActivityEvent.Detail"/>).</summary>
    PackSubmitted,

    /// <summary>Publishing a local pack stopped before its PR (offline, rate limit, refused): the reason in <see cref="ActivityEvent.Detail"/>.</summary>
    PublishFailed,

    /// <summary>
    /// Registry CI rejected the player's PR (task 62): a decision (<c>Fix and resubmit</c>, <c>PR on GitHub</c>) until the
    /// player resubmits, the PR passes after a fix on GitHub, or it is merged or closed; the reason in
    /// <see cref="ActivityEvent.Detail"/>.
    /// </summary>
    PullRequestFailed,

    /// <summary>The player's PR was merged: the pack is in the registry as <c>alpha</c>.</summary>
    PackMerged,

    /// <summary>The player's PR was closed without merging.</summary>
    PullRequestClosed,

    /// <summary>The player's published pack became <c>verified</c> (the index says so).</summary>
    PackVerified,
}

/// <summary>An event's tone; always shown as icon + text, never color only.</summary>
public enum ActivityTone
{
    /// <summary>Neutral (uninstalled).</summary>
    Info,

    /// <summary>Done (installed, re-applied).</summary>
    Ok,

    /// <summary>Waits for the player (amber).</summary>
    Attention,

    /// <summary>Failed (red).</summary>
    Bad,

    /// <summary>A generation waits (teal: the agent's limit).</summary>
    Paused,
}

/// <summary>
/// One event of the Activity panel: a decision waiting for the player (<see cref="IsDecision"/>) or a feed entry. Persisted
/// in <c>activity.json</c> (<see cref="ActivityStore"/>).
/// </summary>
public sealed record ActivityEvent
{
    public required string Id { get; init; }

    public required ActivityEventKind Kind { get; init; }

    public required DateTimeOffset Time { get; init; }

    /// <summary>The game it is about; <c>null</c> for app-wide events (an app update, later).</summary>
    public GameId? Game { get; init; }

    /// <summary>The game's name when it happened (the game may leave the library later).</summary>
    public string? GameName { get; init; }

    public string? PackId { get; init; }

    /// <summary>The pack's title («…», or its ID when the registry didn't list it).</summary>
    public string? PackTitle { get; init; }

    public int? Revision { get; init; }

    /// <summary>The registry pull request it is about (task 62).</summary>
    public int? PullRequest { get; init; }

    /// <summary>A page on GitHub it is about (the pull request); https only.</summary>
    public Uri? Link { get; init; }

    /// <summary>When a paused generation goes on by itself (<see cref="ActivityEventKind.GenerationPaused"/>).</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>A failure's reason in the UI language of the moment it happened.</summary>
    public string? Detail { get; init; }

    /// <summary>A failure's log lines (the <c>Log</c> / <c>Details</c> action).</summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>A failure's files were restored (<c>false</c>: restoring failed, retried on the next start).</summary>
    public bool FilesRestored { get; init; } = true;

    /// <summary>Another try may help (offline, game running, file in use…): <c>Retry</c> is offered.</summary>
    public bool CanRetry { get; init; }

    /// <summary>
    /// What the event is about, so the same finding isn't added twice (a re-check at every start finds an incompatible
    /// pack again); <c>null</c> = always added.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>Waits in <i>Needs your decision</i> until the player acts on it or presses <i>Decide later</i>.</summary>
    public bool IsDecision { get; init; }

    public bool IsRead { get; init; }

    /// <summary>The player hid it (✕); kept only so its <see cref="Key"/> isn't added again.</summary>
    public bool IsHidden { get; init; }

    public ActivityTone Tone => ActivityEvents.ToneOf(Kind);

    /// <summary>Shown in the feed (not a decision, not hidden).</summary>
    public bool IsInFeed => !IsDecision && !IsHidden;

    /// <summary>A new event with a fresh ID; a decision kind starts as a decision.</summary>
    public static ActivityEvent Create(ActivityEventKind kind, DateTimeOffset time) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = kind,
        Time = time,
        IsDecision = ActivityEvents.IsDecisionKind(kind),
    };
}

/// <summary>What a running task is.</summary>
public enum ActivityTaskKind
{
    Install,
    Reapply,
    Uninstall,

    /// <summary>A generation job (task 47; can be paused and cancelled).</summary>
    Generation,

    /// <summary>A local pack being sent to the registry (task 61).</summary>
    Publish,
}

/// <summary>
/// A task running now (design §5 <i>Running</i>): kept in memory only, its producer updates it and ends it. The hooks are
/// <c>null</c> when the task can't be paused or cancelled (installs can't; generation, task 47, can).
/// </summary>
/// <param name="Title">The task in words ("Installing «…» · step 2 of 6").</param>
/// <param name="Step">The current step ("Backing up the original files"), or <c>null</c>.</param>
/// <param name="Fraction">How far it is, 0–1; <c>null</c> when it can't tell.</param>
public sealed record ActivityTask(
    string Id,
    ActivityTaskKind Kind,
    GameId? Game,
    string? GameName,
    string Title,
    string? Step,
    double? Fraction,
    DateTimeOffset Started)
{
    public Action? Pause { get; init; }

    public Action? Cancel { get; init; }
}

/// <summary>How the 🔔 badge looks.</summary>
public enum ActivityBadgeTone
{
    /// <summary>No badge.</summary>
    None,

    /// <summary>Running tasks plus unread events.</summary>
    Normal,

    /// <summary>Amber: the number of decisions waiting.</summary>
    Attention,
}

/// <summary>The 🔔 badge: amber with the number of decisions when there are any, otherwise running tasks plus unread events.</summary>
public sealed record ActivityBadge(int Count, ActivityBadgeTone Tone)
{
    public static ActivityBadge Of(int decisions, int running, int unread)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decisions);
        ArgumentOutOfRangeException.ThrowIfNegative(running);
        ArgumentOutOfRangeException.ThrowIfNegative(unread);
        if (decisions > 0)
        {
            return new ActivityBadge(decisions, ActivityBadgeTone.Attention);
        }

        var count = running + unread;
        return new ActivityBadge(count, count > 0 ? ActivityBadgeTone.Normal : ActivityBadgeTone.None);
    }
}

/// <summary>Rules per event kind.</summary>
public static class ActivityEvents
{
    public static ActivityTone ToneOf(ActivityEventKind kind) => kind switch
    {
        ActivityEventKind.PackInstalled or ActivityEventKind.PackReapplied or ActivityEventKind.PackAutoReapplied
            or ActivityEventKind.PackSubmitted or ActivityEventKind.PackMerged or ActivityEventKind.PackVerified => ActivityTone.Ok,
        ActivityEventKind.InstallFailed or ActivityEventKind.UninstallFailed or ActivityEventKind.ReapplyFailed
            or ActivityEventKind.AutoReapplyFailed or ActivityEventKind.PublishFailed or ActivityEventKind.PullRequestFailed => ActivityTone.Bad,
        ActivityEventKind.PackIncompatible or ActivityEventKind.PackNotApplied or ActivityEventKind.GlossaryReview => ActivityTone.Attention,
        ActivityEventKind.GenerationPaused => ActivityTone.Paused,
        _ => ActivityTone.Info,
    };

    /// <summary>
    /// Kinds that wait for the player: what happened while they weren't there and needs them. A failure of something the
    /// player started goes to the feed (they saw its toast).
    /// </summary>
    public static bool IsDecisionKind(ActivityEventKind kind) =>
        kind is ActivityEventKind.AutoReapplyFailed or ActivityEventKind.PackIncompatible or ActivityEventKind.PackNotApplied
            or ActivityEventKind.GenerationPaused or ActivityEventKind.GlossaryReview or ActivityEventKind.PullRequestFailed;

    /// <summary>Decisions about a game's installed pack: settled once the pack is installed, re-applied or uninstalled again.</summary>
    public static bool IsPackProblem(ActivityEventKind kind) =>
        kind is ActivityEventKind.AutoReapplyFailed or ActivityEventKind.PackIncompatible or ActivityEventKind.PackNotApplied;
}
