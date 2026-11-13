using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;

namespace AiLocalPack.Core.Requests;

/// <summary>A comment of a request's Discussion as the signed-in player reads it.</summary>
/// <param name="Author"><c>null</c> for a deleted account.</param>
/// <param name="ByViewer">The signed-in player wrote it.</param>
public sealed record RequestComment(
    string Id,
    Uri? Url,
    string? Author,
    bool AuthorIsBot,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastEditedAt,
    bool ByViewer,
    string Body)
{
    /// <summary>When it said what it says now (a claim's last news).</summary>
    public DateTimeOffset SaidAt => LastEditedAt ?? CreatedAt;
}

/// <summary>A claim comment found in a request's Discussion.</summary>
public sealed record RequestClaimEntry(RequestComment Comment, RequestClaim Claim);

/// <summary>A request's Discussion as the signed-in player sees it (task 74).</summary>
/// <param name="Id">The Discussion's GraphQL node ID (reactions and comments go to it).</param>
/// <param name="Key">The request its marker names; <c>null</c> for a Discussion that isn't one.</param>
/// <param name="Votes">👍 reactions on the Discussion.</param>
/// <param name="Voted">The player gave one of them.</param>
/// <param name="Comments">The latest <see cref="RequestQueries.CommentsRead"/> comments, oldest first.</param>
public sealed record RequestState(
    string Id,
    Uri Url,
    RequestKey? Key,
    bool Closed,
    bool Locked,
    int Votes,
    bool Voted,
    string? ViewerLogin,
    IReadOnlyList<RequestComment> Comments)
{
    /// <summary>Every claim comment about this request.</summary>
    public IEnumerable<RequestClaimEntry> Claims => Comments
        .Select(c => RequestClaimComment.TryParse(c.Body, out var claim) && claim.Key == Key && c.Author is not null && !c.AuthorIsBot
            ? new RequestClaimEntry(c, claim)
            : null)
        .OfType<RequestClaimEntry>();

    /// <summary>The newest claim that still holds the request at <paramref name="now"/>, or <c>null</c>.</summary>
    public RequestClaimEntry? LiveClaim(DateTimeOffset now) =>
        Claims.Where(c => RequestRules.IsLive(c.Claim.State, c.Comment.SaidAt, now)).MaxBy(c => c.Comment.SaidAt);

    /// <summary>The player's own claim comment (the newest), to edit; <c>null</c> when they have none.</summary>
    public RequestClaimEntry? ViewerClaim => Claims.Where(c => c.Comment.ByViewer).MaxBy(c => c.Comment.SaidAt);

    /// <summary>Players' notes: comments that aren't claims or app markers, not from bots, oldest first.</summary>
    public IReadOnlyList<RequestComment> Notes => [.. Comments.Where(c =>
        c.Author is not null && !c.AuthorIsBot && c.Body.Trim().Length > 0 && !c.Body.TrimStart().StartsWith("<!-- ailocalpack-", StringComparison.Ordinal))];
}

/// <summary>
/// The GraphQL requests of translation requests (task 74) and the parsing of their answers; pure, so both are tested
/// without GitHub. Every request is <c>{query, variables}</c> for <c>POST &lt;api&gt;/graphql</c>.
/// </summary>
public static class RequestQueries
{
    /// <summary>The Discussion category of requests in the registry (open format, made by hand).</summary>
    public const string Category = "Requests";

    /// <summary>How many of the latest comments the app reads (notes, claims).</summary>
    public const int CommentsRead = 50;

    public const string StateQuery = """
        query($owner: String!, $name: String!, $number: Int!) {
          viewer { login }
          repository(owner: $owner, name: $name) {
            discussion(number: $number) {
              id
              url
              body
              closed
              locked
              reactionGroups { content viewerHasReacted reactors { totalCount } }
              comments(last: 50) { nodes { id url body createdAt lastEditedAt viewerDidAuthor author { __typename login } } }
            }
          }
        }
        """;

    public const string SearchQuery = """
        query($q: String!) {
          search(query: $q, type: DISCUSSION, first: 20) {
            nodes { ... on Discussion { number url body closed } }
          }
        }
        """;

    public const string AddCommentMutation = """
        mutation($id: ID!, $body: String!) {
          addDiscussionComment(input: {discussionId: $id, body: $body}) { comment { id url } }
        }
        """;

    public const string CloseMutation = """
        mutation($id: ID!, $reason: DiscussionCloseReason!) {
          closeDiscussion(input: {discussionId: $id, reason: $reason}) { discussion { id } }
        }
        """;

    public static JsonObject State(DiscussionRef discussion)
    {
        ArgumentNullException.ThrowIfNull(discussion);
        return Request(StateQuery, new JsonObject { ["owner"] = discussion.Owner, ["name"] = discussion.Repo, ["number"] = discussion.Number });
    }

    /// <summary>Discussions of the registry whose title names the key (the title ends with it): candidates, checked by their marker.</summary>
    public static JsonObject Search(RegistryRepo repo, RequestKey key)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(key);
        return Request(SearchQuery, new JsonObject { ["q"] = $"repo:{repo.FullName} in:title \"{key}\"" });
    }

    public static JsonObject Close(string discussionId, string reason) =>
        Request(CloseMutation, new JsonObject { ["id"] = discussionId, ["reason"] = reason });

    public static JsonObject AddComment(string discussionId, string body) =>
        Request(AddCommentMutation, new JsonObject { ["id"] = discussionId, ["body"] = body });

    public static RatingResult<RequestState> ParseState(JsonElement answer)
    {
        if (PackDiscussionQueries.Errors(answer) is { } error)
        {
            return RatingResult<RequestState>.Failed(error);
        }

        if (!Object(answer, "data", out var data))
        {
            return RatingResult<RequestState>.Failed(new RatingError(RatingProblem.Unexpected, "GitHub's answer has no data."));
        }

        var viewer = Object(data, "viewer", out var v) ? Text(v, "login") : null;
        if (!Object(data, "repository", out var repository) || !Object(repository, "discussion", out var discussion))
        {
            return RatingResult<RequestState>.Failed(new RatingError(RatingProblem.NotFound, "The Discussion isn't there."));
        }

        if (Text(discussion, "id") is not { Length: > 0 } id || Url(Text(discussion, "url")) is not { } url)
        {
            return RatingResult<RequestState>.Failed(new RatingError(RatingProblem.Unexpected, "GitHub's answer has no Discussion ID."));
        }

        var (votes, voted) = (0, false);
        if (discussion.TryGetProperty("reactionGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray().Where(g => Text(g, "content") == "THUMBS_UP"))
            {
                votes = Object(group, "reactors", out var reactors) && reactors.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var n) ? Math.Max(n, 0) : 0;
                voted = group.TryGetProperty("viewerHasReacted", out var mine) && mine.ValueKind == JsonValueKind.True;
            }
        }

        var comments = new List<RequestComment>();
        foreach (var node in Object(discussion, "comments", out var c) ? Nodes(c) : [])
        {
            if (Text(node, "id") is not { Length: > 0 } commentId || Date(Text(node, "createdAt")) is not { } created)
            {
                continue;
            }

            var author = Object(node, "author", out var a) ? a : default;
            comments.Add(new RequestComment(
                commentId,
                Url(Text(node, "url")),
                author.ValueKind == JsonValueKind.Object ? Text(author, "login") : null,
                author.ValueKind == JsonValueKind.Object && Text(author, "__typename") == "Bot",
                created,
                Date(Text(node, "lastEditedAt")),
                node.TryGetProperty("viewerDidAuthor", out var mine) && mine.ValueKind == JsonValueKind.True,
                Text(node, "body") ?? ""));
        }

        RequestDiscussionMarker.TryParse(Text(discussion, "body"), out var key);
        return RatingResult<RequestState>.Ok(new RequestState(
            id, url, key, Flag(discussion, "closed"), Flag(discussion, "locked"), votes, voted, viewer, comments));
    }

    /// <summary>The open Discussion among the search results whose marker names <paramref name="key"/> (the oldest), or <c>null</c>.</summary>
    public static RatingResult<DiscussionRef?> ParseSearch(JsonElement answer, RequestKey key)
    {
        if (PackDiscussionQueries.Errors(answer) is { } error)
        {
            return RatingResult<DiscussionRef?>.Failed(error);
        }

        if (!Object(answer, "data", out var data) || !Object(data, "search", out var search))
        {
            return RatingResult<DiscussionRef?>.Failed(new RatingError(RatingProblem.Unexpected, "GitHub's answer has no search results."));
        }

        var found = Nodes(search)
            .Where(n => !Flag(n, "closed") && RequestDiscussionMarker.TryParse(Text(n, "body"), out var k) && k == key)
            .Select(n => DiscussionRef.TryParse(Url(Text(n, "url")), out var d) ? d : null)
            .OfType<DiscussionRef>()
            .MinBy(d => d.Number);
        return RatingResult<DiscussionRef?>.Ok(found);
    }

    /// <summary>A mutation's answer: only its errors matter.</summary>
    public static RatingResult<bool> ParseDone(JsonElement answer) =>
        PackDiscussionQueries.Errors(answer) is { } error ? RatingResult<bool>.Failed(error) : RatingResult<bool>.Ok(true);

    private static bool Flag(JsonElement json, string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Nodes(JsonElement connection) =>
        connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object)
            : [];

    private static bool Object(JsonElement json, string name, out JsonElement value)
    {
        value = default;
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    private static JsonObject Request(string query, JsonObject variables) => new() { ["query"] = query, ["variables"] = variables };

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? Url(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
}

/// <summary>
/// Translation requests in the registry's Discussions (task 74) with the player's token (GraphQL has no anonymous
/// access). Asking finds the request's open Discussion (the index's link, else a title search checked by marker) or
/// opens one in the <see cref="RequestQueries.Category"/> category, gives it the player's 👍 and posts their note;
/// withdrawing takes the 👍 back; a claim is one comment of the translator's, edited as the job goes on. Never throws
/// except for cancellation.
/// </summary>
public sealed class TranslationRequestClient(HttpClient http, GitHubOAuthApp app, TimeProvider? time = null)
{
    public Task<RatingResult<RequestState>> ReadAsync(DiscussionRef discussion, string token, CancellationToken cancellationToken = default) =>
        CallAsync(token, RequestQueries.State(discussion), RequestQueries.ParseState, cancellationToken);

    /// <summary>The request's open Discussion by title search, or <c>null</c> when there is none yet.</summary>
    public Task<RatingResult<DiscussionRef?>> FindAsync(RegistryRepo repo, RequestKey key, string token, CancellationToken cancellationToken = default) =>
        CallAsync(token, RequestQueries.Search(repo, key), a => RequestQueries.ParseSearch(a, key), cancellationToken);

    /// <summary>
    /// The player asks for <paramref name="key"/> (or adds their voice): the Discussion of <paramref name="known"/> or
    /// found by search (a closed one or one of another request doesn't count), else a new one; then their 👍 (nothing sent
    /// when they gave it already) and their note as a comment. Answers the Discussion as it is now.
    /// </summary>
    public async Task<RatingResult<RequestState>> AskAsync(
        RegistryRepo repo,
        RequestKey key,
        string gameName,
        Uri? known,
        string? note,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(key);
        RequestState? state = null;
        if (DiscussionRef.TryParse(known, out var link))
        {
            var read = await ReadAsync(link, token, cancellationToken).ConfigureAwait(false);
            if (read.IsOk && read.Value is { Closed: false } open && open.Key == key)
            {
                state = open;
            }
            else if (!read.IsOk && read.Error.Problem != RatingProblem.NotFound)
            {
                return read;
            }
        }

        if (state is null)
        {
            var found = await FindAsync(repo, key, token, cancellationToken).ConfigureAwait(false);
            if (!found.IsOk)
            {
                return RatingResult<RequestState>.Failed(found.Error);
            }

            if (found.Value is { } other)
            {
                var read = await ReadAsync(other, token, cancellationToken).ConfigureAwait(false);
                if (!read.IsOk)
                {
                    return read;
                }

                state = read.Value;
            }
        }

        if (state is null)
        {
            var created = await CreateAsync(repo, key, gameName, token, cancellationToken).ConfigureAwait(false);
            if (!created.IsOk)
            {
                return created;
            }

            state = created.Value;
        }

        if (state.Locked)
        {
            return RatingResult<RequestState>.Failed(new RatingError(RatingProblem.Locked, "The Discussion is locked."));
        }

        var changed = false;
        if (!state.Voted)
        {
            var voted = await CallAsync(token, PackDiscussionQueries.Like(state.Id, like: true), RequestQueries.ParseDone, cancellationToken).ConfigureAwait(false);
            if (!voted.IsOk)
            {
                return RatingResult<RequestState>.Failed(voted.Error);
            }

            changed = true;
        }

        if (RequestRules.CleanNote(note) is { } text)
        {
            var noted = await CallAsync(token, RequestQueries.AddComment(state.Id, text), RequestQueries.ParseDone, cancellationToken).ConfigureAwait(false);
            if (!noted.IsOk)
            {
                return RatingResult<RequestState>.Failed(noted.Error);
            }

            changed = true;
        }

        return changed && DiscussionRef.TryParse(state.Url, out var again)
            ? await ReadAsync(again, token, cancellationToken).ConfigureAwait(false)
            : RatingResult<RequestState>.Ok(state);
    }

    /// <summary>Takes the player's 👍 back (nothing sent when they have none); their notes stay.</summary>
    public async Task<RatingResult<RequestState>> WithdrawAsync(DiscussionRef discussion, string token, CancellationToken cancellationToken = default)
    {
        var read = await ReadAsync(discussion, token, cancellationToken).ConfigureAwait(false);
        if (!read.IsOk || !read.Value.Voted)
        {
            return read;
        }

        var sent = await CallAsync(token, PackDiscussionQueries.Like(read.Value.Id, like: false), RequestQueries.ParseDone, cancellationToken).ConfigureAwait(false);
        return sent.IsOk
            ? RatingResult<RequestState>.Ok(read.Value with { Voted = false, Votes = Math.Max(0, read.Value.Votes - 1) })
            : RatingResult<RequestState>.Failed(sent.Error);
    }

    /// <summary>
    /// Writes the player's claim: edits <paramref name="commentId"/> (their earlier claim comment) or, when it is gone or
    /// not given, their newest claim comment of the request, else adds one. Answers the comment's ID.
    /// </summary>
    public async Task<RatingResult<string>> ClaimAsync(DiscussionRef discussion, RequestClaim claim, string? commentId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var body = RequestClaimComment.Format(claim);
        if (commentId is { Length: > 0 })
        {
            var edited = await CallAsync(token, PackDiscussionQueries.UpdateComment(commentId, body),
                a => PackDiscussionQueries.ParseComment(a, "updateDiscussionComment", replaced: true), cancellationToken).ConfigureAwait(false);
            if (edited.IsOk)
            {
                return RatingResult<string>.Ok(edited.Value.CommentId);
            }

            if (edited.Error.Problem != RatingProblem.NotFound)
            {
                return RatingResult<string>.Failed(edited.Error);
            }
        }

        var read = await ReadAsync(discussion, token, cancellationToken).ConfigureAwait(false);
        if (!read.IsOk)
        {
            return RatingResult<string>.Failed(read.Error);
        }

        if (read.Value.Locked)
        {
            return RatingResult<string>.Failed(new RatingError(RatingProblem.Locked, "The Discussion is locked."));
        }

        var sent = read.Value.ViewerClaim is { } mine
            ? await CallAsync(token, PackDiscussionQueries.UpdateComment(mine.Comment.Id, body),
                a => PackDiscussionQueries.ParseComment(a, "updateDiscussionComment", replaced: true), cancellationToken).ConfigureAwait(false)
            : await CallAsync(token, PackDiscussionQueries.AddComment(read.Value.Id, body),
                a => PackDiscussionQueries.ParseComment(a, "addDiscussionComment", replaced: false), cancellationToken).ConfigureAwait(false);
        return sent.IsOk ? RatingResult<string>.Ok(sent.Value.CommentId) : RatingResult<string>.Failed(sent.Error);
    }

    private async Task<RatingResult<RequestState>> CreateAsync(RegistryRepo repo, RequestKey key, string gameName, string token, CancellationToken cancellationToken)
    {
        var category = await CallAsync(token, RegistryDiscussionQueries.Category(repo), a => RegistryDiscussionQueries.ParseCategory(a, RequestQueries.Category), cancellationToken).ConfigureAwait(false);
        if (!category.IsOk)
        {
            return RatingResult<RequestState>.Failed(category.Error);
        }

        if (category.Value.CategoryId is not { } categoryId)
        {
            return RatingResult<RequestState>.Failed(new RatingError(
                RatingProblem.NotFound, $"{repo.FullName} has no Discussion category \"{RequestQueries.Category}\"."));
        }

        var created = await CallAsync(
            token,
            RegistryDiscussionQueries.Create(category.Value.RepositoryId, categoryId, RequestDiscussionText.Title(key, gameName), RequestDiscussionText.Body(key, gameName)),
            RegistryDiscussionQueries.ParseCreated,
            cancellationToken).ConfigureAwait(false);
        if (!created.IsOk)
        {
            return RatingResult<RequestState>.Failed(created.Error);
        }

        return DiscussionRef.TryParse(created.Value.Url, out var made)
            ? await ReadAsync(made, token, cancellationToken).ConfigureAwait(false)
            : RatingResult<RequestState>.Failed(new RatingError(RatingProblem.Unexpected, $"GitHub opened {created.Value.Url}, which isn't a Discussion link."));
    }

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
