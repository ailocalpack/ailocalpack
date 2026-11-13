using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Requests;
using AiLocalPack.Desktop.Requests;

namespace AiLocalPack.Desktop.Tests.Requests;

/// <summary>
/// The registry's Requests Discussions as the test sets them, so no test reaches GitHub: records every ask, withdraw and
/// claim; an ask answers <see cref="State"/> with the player's vote. <see cref="Error"/> fails the next calls.
/// </summary>
internal sealed class FakeRequestDiscussions : IRequestDiscussions
{
    public static readonly Uri Url = new("https://github.com/ailocalpack/registry/discussions/77");

    public List<(RequestKey Key, string GameName, Uri? Known, string? Note)> Asks { get; } = [];

    public List<DiscussionRef> Withdrawals { get; } = [];

    public List<(DiscussionRef Discussion, RequestClaim Claim, string? Comment)> Claims { get; } = [];

    public int Reads { get; private set; }

    /// <summary>What a read answers.</summary>
    public RequestState State { get; set; } = new("D_77", Url, null, false, false, 13, false, "octocat", []);

    public RatingError? Error { get; set; }

    public Task<RatingResult<RequestState>> ReadAsync(DiscussionRef discussion, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Error is { } error ? RatingResult<RequestState>.Failed(error) : RatingResult<RequestState>.Ok(State));
    }

    public Task<RatingResult<RequestState>> AskAsync(RequestKey key, string gameName, Uri? known, string? note, CancellationToken cancellationToken = default)
    {
        Asks.Add((key, gameName, known, note));
        if (Error is { } error)
        {
            return Task.FromResult(RatingResult<RequestState>.Failed(error));
        }

        State = State with { Key = key, Voted = true, Votes = State.Voted ? State.Votes : State.Votes + 1 };
        return Task.FromResult(RatingResult<RequestState>.Ok(State));
    }

    public Task<RatingResult<RequestState>> WithdrawAsync(DiscussionRef discussion, CancellationToken cancellationToken = default)
    {
        Withdrawals.Add(discussion);
        if (Error is { } error)
        {
            return Task.FromResult(RatingResult<RequestState>.Failed(error));
        }

        State = State with { Voted = false, Votes = Math.Max(0, State.Votes - 1) };
        return Task.FromResult(RatingResult<RequestState>.Ok(State));
    }

    public Task<RatingResult<string>> ClaimAsync(DiscussionRef discussion, RequestClaim claim, string? commentId, CancellationToken cancellationToken = default)
    {
        Claims.Add((discussion, claim, commentId));
        return Task.FromResult(Error is { } error ? RatingResult<string>.Failed(error) : RatingResult<string>.Ok("DC_claim"));
    }
}
