using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;

namespace AiLocalPack.Core.Ratings;

/// <summary>A GitHub Discussion by its link: <c>https://&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;/discussions/&lt;number&gt;</c>.</summary>
public sealed record DiscussionRef(string Owner, string Repo, int Number)
{
    /// <summary>The pack's Discussion from the index's link; <c>false</c> for anything that isn't one.</summary>
    public static bool TryParse(Uri? url, [NotNullWhen(true)] out DiscussionRef? discussion)
    {
        discussion = null;
        if (url is not { IsAbsoluteUri: true } || url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var parts = url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 4
            || parts[2] != "discussions"
            || !RegistryRepo.TryParse($"{parts[0]}/{parts[1]}", out _)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        discussion = new DiscussionRef(parts[0], parts[1], number);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Owner}/{Repo}#{Number}");
}

/// <summary>A report comment in a Discussion.</summary>
/// <param name="Id">The comment's GraphQL node ID (to edit it).</param>
/// <param name="ByViewer">The signed-in player wrote it.</param>
public sealed record DiscussionReport(string Id, Uri? Url, PackReport Report, bool ByViewer);

/// <summary>A pack's Discussion as the signed-in player sees it.</summary>
/// <param name="Id">The Discussion's GraphQL node ID (reactions and comments go to it).</param>
/// <param name="Likes">👍 reactions on the Discussion.</param>
/// <param name="ViewerLiked">The player gave one of them.</param>
/// <param name="Reports">The report comments among the latest <see cref="PackDiscussionQueries.CommentsRead"/>.</param>
public sealed record DiscussionState(string Id, Uri? Url, bool Locked, int Likes, bool ViewerLiked, string? ViewerLogin, IReadOnlyList<DiscussionReport> Reports)
{
    /// <summary>The player's report of this pack revision, the latest one when there are several.</summary>
    public DiscussionReport? ViewerReport(PackReport about)
    {
        ArgumentNullException.ThrowIfNull(about);
        return Reports.LastOrDefault(r => r.ByViewer && r.Report.IsAbout(about.Game, about.Language, about.PackId, about.Revision));
    }
}

/// <summary>Why a rating call failed.</summary>
public enum RatingProblem
{
    /// <summary>No GitHub token: the player has to sign in.</summary>
    NotSignedIn,

    /// <summary>The keychain couldn't give the token.</summary>
    Keychain,

    /// <summary>GitHub no longer accepts the token (revoked).</summary>
    Unauthorized,

    /// <summary>The Discussion (or its repository) isn't there.</summary>
    NotFound,

    /// <summary>The maintainers locked the Discussion: no new comments or reactions.</summary>
    Locked,

    /// <summary>The token may not do this, or the repository refuses it.</summary>
    Forbidden,

    RateLimited,

    /// <summary>GitHub couldn't be reached, didn't answer in time, or answered 5xx.</summary>
    Network,

    /// <summary>GitHub refused the request, or answered something the app can't use.</summary>
    Unexpected,
}

/// <summary>A failed rating call: the kind, GitHub's own message, and when to retry (rate limit).</summary>
public sealed record RatingError(RatingProblem Problem, string Message, DateTimeOffset? RetryAt = null)
{
    public static RatingError From(GitHubApiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var problem = error.Problem switch
        {
            PublishProblem.NotSignedIn => RatingProblem.NotSignedIn,
            PublishProblem.Keychain => RatingProblem.Keychain,
            PublishProblem.Unauthorized => RatingProblem.Unauthorized,
            PublishProblem.NotFound => RatingProblem.NotFound,
            PublishProblem.Forbidden => RatingProblem.Forbidden,
            PublishProblem.RateLimited => RatingProblem.RateLimited,
            PublishProblem.Network => RatingProblem.Network,
            _ => RatingProblem.Unexpected,
        };
        return new RatingError(problem, error.Message, error.RetryAt);
    }
}

/// <summary>A rating call's answer: the value, or why it failed.</summary>
public sealed record RatingResult<T>(T? Value, RatingError? Error)
{
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsOk => Error is null;

    public static RatingResult<T> Ok(T value) => new(value, null);

    public static RatingResult<T> Failed(RatingError error) => new(default, error);
}

/// <summary>The player's 👍 after <see cref="PackDiscussionClient.SetLikeAsync"/>.</summary>
public sealed record LikeState(bool Liked, int Likes);

/// <summary>Where a report went: the comment, and whether an earlier report of the same revision was replaced.</summary>
public sealed record SentReport(string CommentId, Uri? Url, bool Replaced);

/// <summary>
/// The GraphQL requests of ratings (task 63) and the parsing of their answers; pure, so both are tested without
/// GitHub. Every request is <c>{query, variables}</c> for <c>POST &lt;api&gt;/graphql</c>.
/// </summary>
public static class PackDiscussionQueries
{
    /// <summary>How many of the latest comments are read to find the player's earlier report (an older one isn't edited, a new one is added).</summary>
    public const int CommentsRead = 100;

    public const string StateQuery = """
        query($owner: String!, $name: String!, $number: Int!) {
          viewer { login }
          repository(owner: $owner, name: $name) {
            discussion(number: $number) {
              id
              url
              locked
              reactionGroups { content viewerHasReacted reactors { totalCount } }
              comments(last: 100) { nodes { id url body viewerDidAuthor } }
            }
          }
        }
        """;

    public const string AddReactionMutation = """
        mutation($id: ID!) {
          addReaction(input: {subjectId: $id, content: THUMBS_UP}) { reaction { content } }
        }
        """;

    public const string RemoveReactionMutation = """
        mutation($id: ID!) {
          removeReaction(input: {subjectId: $id, content: THUMBS_UP}) { reaction { content } }
        }
        """;

    public const string AddCommentMutation = """
        mutation($id: ID!, $body: String!) {
          addDiscussionComment(input: {discussionId: $id, body: $body}) { comment { id url } }
        }
        """;

    public const string UpdateCommentMutation = """
        mutation($id: ID!, $body: String!) {
          updateDiscussionComment(input: {commentId: $id, body: $body}) { comment { id url } }
        }
        """;

    public static JsonObject State(DiscussionRef discussion)
    {
        ArgumentNullException.ThrowIfNull(discussion);
        return Request(StateQuery, new JsonObject
        {
            ["owner"] = discussion.Owner,
            ["name"] = discussion.Repo,
            ["number"] = discussion.Number,
        });
    }

    public static JsonObject Like(string discussionId, bool like) =>
        Request(like ? AddReactionMutation : RemoveReactionMutation, new JsonObject { ["id"] = discussionId });

    public static JsonObject AddComment(string discussionId, string body) =>
        Request(AddCommentMutation, new JsonObject { ["id"] = discussionId, ["body"] = body });

    public static JsonObject UpdateComment(string commentId, string body) =>
        Request(UpdateCommentMutation, new JsonObject { ["id"] = commentId, ["body"] = body });

    /// <summary>
    /// GraphQL's own errors (HTTP 200 with <c>errors</c>): <c>NOT_FOUND</c> → <see cref="RatingProblem.NotFound"/>,
    /// <c>FORBIDDEN</c> / <c>INSUFFICIENT_SCOPES</c> → <see cref="RatingProblem.Forbidden"/> (a message about a lock →
    /// <see cref="RatingProblem.Locked"/>), <c>RATE_LIMITED</c> → <see cref="RatingProblem.RateLimited"/>, anything else
    /// → <see cref="RatingProblem.Unexpected"/> with GitHub's message; <c>null</c> when there are none.
    /// </summary>
    public static RatingError? Errors(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
        {
            return answer.ValueKind == JsonValueKind.Object ? null : new RatingError(RatingProblem.Unexpected, "GitHub's GraphQL answer isn't an object.");
        }

        var first = errors[0];
        var type = Text(first, "type");
        var message = Text(first, "message") ?? "GitHub refused the request.";
        var problem = type switch
        {
            "NOT_FOUND" => RatingProblem.NotFound,
            "RATE_LIMITED" => RatingProblem.RateLimited,
            _ when message.Contains("locked", StringComparison.OrdinalIgnoreCase) => RatingProblem.Locked,
            "FORBIDDEN" or "INSUFFICIENT_SCOPES" => RatingProblem.Forbidden,
            _ => RatingProblem.Unexpected,
        };
        return new RatingError(problem, message);
    }

    /// <summary>The Discussion from <see cref="State"/>'s answer, or the error (a missing Discussion is <see cref="RatingProblem.NotFound"/>).</summary>
    public static RatingResult<DiscussionState> ParseState(JsonElement answer)
    {
        if (Errors(answer) is { } error)
        {
            return RatingResult<DiscussionState>.Failed(error);
        }

        if (!answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return RatingResult<DiscussionState>.Failed(new RatingError(RatingProblem.Unexpected, "GitHub's answer has no data."));
        }

        var viewer = data.TryGetProperty("viewer", out var v) && v.ValueKind == JsonValueKind.Object ? Text(v, "login") : null;
        if (!data.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("discussion", out var discussion) || discussion.ValueKind != JsonValueKind.Object)
        {
            return RatingResult<DiscussionState>.Failed(new RatingError(RatingProblem.NotFound, "The Discussion isn't there."));
        }

        if (Text(discussion, "id") is not { Length: > 0 } id)
        {
            return RatingResult<DiscussionState>.Failed(new RatingError(RatingProblem.Unexpected, "GitHub's answer has no Discussion ID."));
        }

        var (likes, liked) = ThumbsUp(discussion);
        var reports = new List<DiscussionReport>();
        if (discussion.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Object
            && comments.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Object
                    && Text(node, "id") is { Length: > 0 } commentId
                    && PackReportComment.TryParse(Text(node, "body"), out var report))
                {
                    var byViewer = node.TryGetProperty("viewerDidAuthor", out var mine) && mine.ValueKind == JsonValueKind.True;
                    reports.Add(new DiscussionReport(commentId, Url(Text(node, "url")), report, byViewer));
                }
            }
        }

        var locked = discussion.TryGetProperty("locked", out var l) && l.ValueKind == JsonValueKind.True;
        return RatingResult<DiscussionState>.Ok(new DiscussionState(id, Url(Text(discussion, "url")), locked, likes, liked, viewer, reports));
    }

    /// <summary>The comment an add / update mutation answered with.</summary>
    public static RatingResult<SentReport> ParseComment(JsonElement answer, string mutation, bool replaced)
    {
        if (Errors(answer) is { } error)
        {
            return RatingResult<SentReport>.Failed(error);
        }

        if (answer.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(mutation, out var payload) && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("comment", out var comment) && comment.ValueKind == JsonValueKind.Object
            && Text(comment, "id") is { Length: > 0 } id)
        {
            return RatingResult<SentReport>.Ok(new SentReport(id, Url(Text(comment, "url")), replaced));
        }

        return RatingResult<SentReport>.Failed(new RatingError(RatingProblem.Unexpected, $"GitHub's answer to {mutation} has no comment."));
    }

    private static (int Likes, bool Liked) ThumbsUp(JsonElement discussion)
    {
        if (!discussion.TryGetProperty("reactionGroups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return (0, false);
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind == JsonValueKind.Object && Text(group, "content") == "THUMBS_UP")
            {
                var count = group.TryGetProperty("reactors", out var reactors) && reactors.ValueKind == JsonValueKind.Object
                    && reactors.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var n) ? Math.Max(n, 0) : 0;
                var liked = group.TryGetProperty("viewerHasReacted", out var mine) && mine.ValueKind == JsonValueKind.True;
                return (count, liked);
            }
        }

        return (0, false);
    }

    private static JsonObject Request(string query, JsonObject variables) => new() { ["query"] = query, ["variables"] = variables };

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? Url(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
}

/// <summary>
/// Likes and works / doesn't-work reports in a pack's GitHub Discussion (task 63), through GitHub's GraphQL API with
/// the player's token (GraphQL has no anonymous access). A like is a 👍 reaction on the Discussion (<c>addReaction</c> /
/// <c>removeReaction</c>); a report is a comment in <see cref="PackReportComment"/>'s format, and a later report of the
/// same pack revision edits the player's earlier one (<c>updateDiscussionComment</c>) instead of adding another, so
/// one player counts once. HTTP errors are the publish client's (<see cref="GitHubRestClient"/>), GraphQL's own
/// errors <see cref="PackDiscussionQueries.Errors"/>. Never throws except for cancellation.
/// </summary>
public sealed class PackDiscussionClient(HttpClient http, GitHubOAuthApp app, TimeProvider? time = null)
{
    public Task<RatingResult<DiscussionState>> ReadAsync(DiscussionRef discussion, string token, CancellationToken cancellationToken = default) =>
        CallAsync(token, PackDiscussionQueries.State(discussion), PackDiscussionQueries.ParseState, cancellationToken);

    /// <summary>Gives or takes back the player's 👍 (nothing sent when it already is so).</summary>
    public async Task<RatingResult<LikeState>> SetLikeAsync(DiscussionRef discussion, bool like, string token, CancellationToken cancellationToken = default)
    {
        var read = await ReadAsync(discussion, token, cancellationToken).ConfigureAwait(false);
        if (!read.IsOk)
        {
            return RatingResult<LikeState>.Failed(read.Error);
        }

        var state = read.Value;
        if (state.ViewerLiked == like)
        {
            return RatingResult<LikeState>.Ok(new LikeState(like, state.Likes));
        }

        if (state.Locked)
        {
            return RatingResult<LikeState>.Failed(Locked());
        }

        var sent = await CallAsync(token, PackDiscussionQueries.Like(state.Id, like), answer => PackDiscussionQueries.Errors(answer) is { } error
            ? RatingResult<bool>.Failed(error)
            : RatingResult<bool>.Ok(true), cancellationToken).ConfigureAwait(false);
        return sent.IsOk
            ? RatingResult<LikeState>.Ok(new LikeState(like, Math.Max(0, state.Likes + (like ? 1 : -1))))
            : RatingResult<LikeState>.Failed(sent.Error);
    }

    /// <summary>Posts the report, or edits the player's earlier report of the same pack revision.</summary>
    public async Task<RatingResult<SentReport>> ReportAsync(DiscussionRef discussion, PackReport report, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var read = await ReadAsync(discussion, token, cancellationToken).ConfigureAwait(false);
        if (!read.IsOk)
        {
            return RatingResult<SentReport>.Failed(read.Error);
        }

        if (read.Value.Locked)
        {
            return RatingResult<SentReport>.Failed(Locked());
        }

        var body = PackReportComment.Format(report);
        if (read.Value.ViewerReport(report) is { } earlier)
        {
            return await CallAsync(token, PackDiscussionQueries.UpdateComment(earlier.Id, body),
                answer => PackDiscussionQueries.ParseComment(answer, "updateDiscussionComment", replaced: true), cancellationToken).ConfigureAwait(false);
        }

        return await CallAsync(token, PackDiscussionQueries.AddComment(read.Value.Id, body),
            answer => PackDiscussionQueries.ParseComment(answer, "addDiscussionComment", replaced: false), cancellationToken).ConfigureAwait(false);
    }

    private static RatingError Locked() => new(RatingProblem.Locked, "The Discussion is locked.");

    private async Task<RatingResult<T>> CallAsync<T>(string token, JsonObject request, Func<JsonElement, RatingResult<T>> parse, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        try
        {
            var answer = await new GitHubRestClient(http, app, token, time).PostAsync("graphql", request, cancellationToken).ConfigureAwait(false);
            return parse(answer);
        }
        catch (GitHubApiException e)
        {
            return RatingResult<T>.Failed(RatingError.From(e.Error));
        }
    }
}
