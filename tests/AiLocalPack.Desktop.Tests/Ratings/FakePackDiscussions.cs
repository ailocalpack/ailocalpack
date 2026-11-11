using AiLocalPack.Core.Ratings;
using AiLocalPack.Desktop.Ratings;

namespace AiLocalPack.Desktop.Tests.Ratings;

/// <summary>
/// A pack's Discussion as the test sets it, so no test reaches GitHub: records every like and report; by default a
/// like is counted and a report is a new comment. <see cref="Error"/> fails the next calls; <see cref="Hold"/> keeps a
/// call waiting until <see cref="Release"/>.
/// </summary>
internal sealed class FakePackDiscussions : IPackDiscussions
{
    private TaskCompletionSource? _hold;

    public List<(DiscussionRef Discussion, bool Like)> Likes { get; } = [];

    public List<(DiscussionRef Discussion, PackReport Report)> Reports { get; } = [];

    public int Reads { get; private set; }

    /// <summary>What a read answers: the player's like and reports.</summary>
    public DiscussionState State { get; set; } = new("D_1", new Uri("https://github.com/ailocalpack/registry/discussions/42"), false, 5, false, "octocat", []);

    /// <summary>Every call fails with this until it is <c>null</c> again.</summary>
    public RatingError? Error { get; set; }

    public static Uri CommentUrl { get; } = new("https://github.com/ailocalpack/registry/discussions/42#discussioncomment-7");

    public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _hold?.TrySetResult();

    public async Task<RatingResult<DiscussionState>> ReadAsync(DiscussionRef discussion, CancellationToken cancellationToken = default)
    {
        Reads++;
        await WaitAsync(cancellationToken);
        return Error is { } error ? RatingResult<DiscussionState>.Failed(error) : RatingResult<DiscussionState>.Ok(State);
    }

    public async Task<RatingResult<LikeState>> SetLikeAsync(DiscussionRef discussion, bool like, CancellationToken cancellationToken = default)
    {
        Likes.Add((discussion, like));
        await WaitAsync(cancellationToken);
        return Error is { } error ? RatingResult<LikeState>.Failed(error) : RatingResult<LikeState>.Ok(new LikeState(like, like ? 6 : 4));
    }

    public async Task<RatingResult<SentReport>> ReportAsync(DiscussionRef discussion, PackReport report, CancellationToken cancellationToken = default)
    {
        Reports.Add((discussion, report));
        await WaitAsync(cancellationToken);
        return Error is { } error ? RatingResult<SentReport>.Failed(error) : RatingResult<SentReport>.Ok(new SentReport("DC_7", CommentUrl, Reports.Count > 1));
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (_hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }
    }
}
