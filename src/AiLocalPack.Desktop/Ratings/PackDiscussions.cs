using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Secrets;

namespace AiLocalPack.Desktop.Ratings;

/// <summary>
/// Likes and reports in a pack's GitHub Discussion with the signed-in player's token (task 63). Behind an interface so
/// view models are tested without GitHub; never throws but for cancellation.
/// </summary>
public interface IPackDiscussions
{
    Task<RatingResult<DiscussionState>> ReadAsync(DiscussionRef discussion, CancellationToken cancellationToken = default);

    Task<RatingResult<LikeState>> SetLikeAsync(DiscussionRef discussion, bool like, CancellationToken cancellationToken = default);

    Task<RatingResult<SentReport>> ReportAsync(DiscussionRef discussion, PackReport report, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IPackDiscussions"/> over <see cref="PackDiscussionClient"/>, the token read from the keychain for each call.</summary>
internal sealed class PackDiscussions(PackDiscussionClient client, GitHubSession session) : IPackDiscussions
{
    public Task<RatingResult<DiscussionState>> ReadAsync(DiscussionRef discussion, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.ReadAsync(discussion, token, cancellationToken), cancellationToken);

    public Task<RatingResult<LikeState>> SetLikeAsync(DiscussionRef discussion, bool like, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.SetLikeAsync(discussion, like, token, cancellationToken), cancellationToken);

    public Task<RatingResult<SentReport>> ReportAsync(DiscussionRef discussion, PackReport report, CancellationToken cancellationToken = default) =>
        WithTokenAsync(token => client.ReportAsync(discussion, report, token, cancellationToken), cancellationToken);

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
