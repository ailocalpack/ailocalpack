using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Ratings;

/// <summary>The registry repository and its Discussion category for packs.</summary>
/// <param name="CategoryId"><c>null</c> when the repository has no category of that name.</param>
public sealed record DiscussionCategory(string RepositoryId, string? CategoryId);

/// <summary>One page of something paged by GraphQL: the items and the cursor of the next page (<c>null</c> = last page).</summary>
public sealed record GraphQlPage<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>A Discussion of a listing, with the 👍 givers and comments of its first page and the cursors of the rest.</summary>
public sealed record ListedDiscussion(CollectedDiscussion Discussion, string? MoreLikers, string? MoreComments);

/// <summary>A Discussion the collector opened.</summary>
public sealed record CreatedDiscussion(string Id, int Number, Uri Url);

/// <summary>
/// The GraphQL requests of <c>ratings collect</c> (task 64) and the parsing of their answers; pure, so both are
/// tested without GitHub. Every request is <c>{query, variables}</c> for <c>POST &lt;api&gt;/graphql</c>.
/// </summary>
public static class RegistryDiscussionQueries
{
    /// <summary>Discussions per page; each brings up to <see cref="PageSize"/> likers and comments (GraphQL's node limit is far off).</summary>
    public const int DiscussionsPerPage = 25;

    public const int PageSize = 100;

    public const string CategoryQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            id
            discussionCategories(first: 100) { nodes { id name } }
          }
        }
        """;

    public const string DiscussionsQuery = """
        query($owner: String!, $name: String!, $category: ID!, $after: String) {
          repository(owner: $owner, name: $name) {
            discussions(first: 25, after: $after, categoryId: $category, orderBy: {field: CREATED_AT, direction: ASC}) {
              pageInfo { hasNextPage endCursor }
              nodes {
                id
                number
                url
                body
                closed
                createdAt
                reactions(content: THUMBS_UP, first: 100) {
                  pageInfo { hasNextPage endCursor }
                  nodes { user { __typename login createdAt } }
                }
                comments(first: 100) {
                  pageInfo { hasNextPage endCursor }
                  nodes { id body createdAt lastEditedAt author { __typename login ... on User { createdAt } } }
                }
              }
            }
          }
        }
        """;

    public const string MoreCommentsQuery = """
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on Discussion {
              comments(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { id body createdAt lastEditedAt author { __typename login ... on User { createdAt } } }
              }
            }
          }
        }
        """;

    public const string MoreLikersQuery = """
        query($id: ID!, $after: String) {
          node(id: $id) {
            ... on Discussion {
              reactions(content: THUMBS_UP, first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { user { __typename login createdAt } }
              }
            }
          }
        }
        """;

    public const string CreateMutation = """
        mutation($repository: ID!, $category: ID!, $title: String!, $body: String!) {
          createDiscussion(input: {repositoryId: $repository, categoryId: $category, title: $title, body: $body}) {
            discussion { id number url }
          }
        }
        """;

    public static JsonObject Category(RegistryRepo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return Request(CategoryQuery, new JsonObject { ["owner"] = repo.Owner, ["name"] = repo.Name });
    }

    public static JsonObject Discussions(RegistryRepo repo, string categoryId, string? after)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return Request(DiscussionsQuery, new JsonObject { ["owner"] = repo.Owner, ["name"] = repo.Name, ["category"] = categoryId, ["after"] = after });
    }

    public static JsonObject MoreComments(string discussionId, string after) =>
        Request(MoreCommentsQuery, new JsonObject { ["id"] = discussionId, ["after"] = after });

    public static JsonObject MoreLikers(string discussionId, string after) =>
        Request(MoreLikersQuery, new JsonObject { ["id"] = discussionId, ["after"] = after });

    public static JsonObject Create(string repositoryId, string categoryId, string title, string body) =>
        Request(CreateMutation, new JsonObject { ["repository"] = repositoryId, ["category"] = categoryId, ["title"] = title, ["body"] = body });

    /// <summary>The repository's ID and the ID of the category named <paramref name="category"/> (case ignored).</summary>
    public static RatingResult<DiscussionCategory> ParseCategory(JsonElement answer, string category)
    {
        if (Data(answer, out var data) is { } error)
        {
            return RatingResult<DiscussionCategory>.Failed(error);
        }

        if (!Object(data, "repository", out var repository) || Text(repository, "id") is not { Length: > 0 } id)
        {
            return RatingResult<DiscussionCategory>.Failed(new RatingError(RatingProblem.NotFound, "The registry repository isn't there."));
        }

        string? categoryId = null;
        if (Object(repository, "discussionCategories", out var categories))
        {
            categoryId = Nodes(categories)
                .Where(n => string.Equals(Text(n, "name"), category, StringComparison.OrdinalIgnoreCase))
                .Select(n => Text(n, "id"))
                .FirstOrDefault(i => i is { Length: > 0 });
        }

        return RatingResult<DiscussionCategory>.Ok(new DiscussionCategory(id, categoryId));
    }

    public static RatingResult<GraphQlPage<ListedDiscussion>> ParseDiscussions(JsonElement answer)
    {
        if (Data(answer, out var data) is { } error)
        {
            return RatingResult<GraphQlPage<ListedDiscussion>>.Failed(error);
        }

        if (!Object(data, "repository", out var repository) || !Object(repository, "discussions", out var discussions))
        {
            return RatingResult<GraphQlPage<ListedDiscussion>>.Failed(new RatingError(RatingProblem.NotFound, "The registry repository isn't there."));
        }

        var items = new List<ListedDiscussion>();
        foreach (var node in Nodes(discussions))
        {
            if (Text(node, "id") is not { Length: > 0 } id
                || !node.TryGetProperty("number", out var n) || !n.TryGetInt32(out var number)
                || !Uri.TryCreate(Text(node, "url"), UriKind.Absolute, out var url))
            {
                continue;
            }

            var likers = Object(node, "reactions", out var reactions) ? Likers(reactions) : new GraphQlPage<DiscussionUser>([], null);
            var comments = Object(node, "comments", out var c) ? Comments(c) : new GraphQlPage<DiscussionComment>([], null);
            var pack = PackDiscussionMarker.TryParse(Text(node, "body"), out var key) ? key : null;
            var collected = new CollectedDiscussion(id, number, url, pack, likers.Items, comments.Items)
            {
                Body = Text(node, "body") ?? "",
                Closed = node.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.True,
                CreatedAt = Date(Text(node, "createdAt")),
            };
            items.Add(new ListedDiscussion(collected, likers.Next, comments.Next));
        }

        return RatingResult<GraphQlPage<ListedDiscussion>>.Ok(new GraphQlPage<ListedDiscussion>(items, NextPage(discussions)));
    }

    public static RatingResult<GraphQlPage<DiscussionComment>> ParseMoreComments(JsonElement answer) =>
        ParseNode(answer, "comments", Comments);

    public static RatingResult<GraphQlPage<DiscussionUser>> ParseMoreLikers(JsonElement answer) =>
        ParseNode(answer, "reactions", Likers);

    public static RatingResult<CreatedDiscussion> ParseCreated(JsonElement answer)
    {
        if (Data(answer, out var data) is { } error)
        {
            return RatingResult<CreatedDiscussion>.Failed(error);
        }

        if (Object(data, "createDiscussion", out var payload) && Object(payload, "discussion", out var discussion)
            && Text(discussion, "id") is { Length: > 0 } id
            && discussion.TryGetProperty("number", out var n) && n.TryGetInt32(out var number)
            && Uri.TryCreate(Text(discussion, "url"), UriKind.Absolute, out var url))
        {
            return RatingResult<CreatedDiscussion>.Ok(new CreatedDiscussion(id, number, url));
        }

        return RatingResult<CreatedDiscussion>.Failed(new RatingError(RatingProblem.Unexpected, "GitHub's answer to createDiscussion has no Discussion."));
    }

    private static RatingResult<GraphQlPage<T>> ParseNode<T>(JsonElement answer, string connection, Func<JsonElement, GraphQlPage<T>> read)
    {
        if (Data(answer, out var data) is { } error)
        {
            return RatingResult<GraphQlPage<T>>.Failed(error);
        }

        return Object(data, "node", out var node) && Object(node, connection, out var items)
            ? RatingResult<GraphQlPage<T>>.Ok(read(items))
            : RatingResult<GraphQlPage<T>>.Failed(new RatingError(RatingProblem.NotFound, "The Discussion isn't there any more."));
    }

    private static GraphQlPage<DiscussionUser> Likers(JsonElement reactions)
    {
        var users = new List<DiscussionUser>();
        foreach (var node in Nodes(reactions))
        {
            if (Object(node, "user", out var user) && Text(user, "login") is { Length: > 0 } login)
            {
                users.Add(new DiscussionUser(login, Text(user, "__typename") == "Bot", Date(Text(user, "createdAt"))));
            }
        }

        return new GraphQlPage<DiscussionUser>(users, NextPage(reactions));
    }

    private static GraphQlPage<DiscussionComment> Comments(JsonElement comments)
    {
        var items = new List<DiscussionComment>();
        foreach (var node in Nodes(comments))
        {
            if (Text(node, "id") is not { Length: > 0 } id || Date(Text(node, "createdAt")) is not { } created)
            {
                continue;
            }

            DiscussionUser? author = null;
            if (Object(node, "author", out var a) && Text(a, "login") is { Length: > 0 } login)
            {
                author = new DiscussionUser(login, Text(a, "__typename") == "Bot", Date(Text(a, "createdAt")));
            }

            items.Add(new DiscussionComment(id, author, created, Date(Text(node, "lastEditedAt")), Text(node, "body") ?? ""));
        }

        return new GraphQlPage<DiscussionComment>(items, NextPage(comments));
    }

    private static string? NextPage(JsonElement connection) =>
        Object(connection, "pageInfo", out var info) && info.TryGetProperty("hasNextPage", out var more) && more.ValueKind == JsonValueKind.True
            ? Text(info, "endCursor")
            : null;

    private static RatingError? Data(JsonElement answer, out JsonElement data)
    {
        data = default;
        if (PackDiscussionQueries.Errors(answer) is { } error)
        {
            return error;
        }

        return Object(answer, "data", out data) ? null : new RatingError(RatingProblem.Unexpected, "GitHub's answer has no data.");
    }

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
}

/// <summary>
/// The registry's pack Discussions through GitHub's GraphQL API with a token that may read them (and, to open missing
/// ones, write: the Action's <c>GITHUB_TOKEN</c> with <c>discussions: write</c>). Never throws except for cancellation.
/// </summary>
public sealed class RegistryDiscussionClient(HttpClient http, GitHubOAuthApp app, string token, TimeProvider? time = null)
{
    public Task<RatingResult<DiscussionCategory>> CategoryAsync(RegistryRepo repo, string category, CancellationToken cancellationToken = default) =>
        CallAsync(RegistryDiscussionQueries.Category(repo), a => RegistryDiscussionQueries.ParseCategory(a, category), cancellationToken);

    /// <summary>Every Discussion of the category with all its 👍 givers and top-level comments.</summary>
    public async Task<RatingResult<IReadOnlyList<CollectedDiscussion>>> ListAsync(RegistryRepo repo, string categoryId, CancellationToken cancellationToken = default)
    {
        var result = new List<CollectedDiscussion>();
        string? after = null;
        do
        {
            var page = await CallAsync(RegistryDiscussionQueries.Discussions(repo, categoryId, after), RegistryDiscussionQueries.ParseDiscussions, cancellationToken).ConfigureAwait(false);
            if (!page.IsOk)
            {
                return RatingResult<IReadOnlyList<CollectedDiscussion>>.Failed(page.Error);
            }

            foreach (var listed in page.Value.Items)
            {
                var discussion = listed.Discussion;
                var likers = await RestAsync(discussion.Id, listed.MoreLikers, RegistryDiscussionQueries.MoreLikers, RegistryDiscussionQueries.ParseMoreLikers, cancellationToken).ConfigureAwait(false);
                var comments = await RestAsync(discussion.Id, listed.MoreComments, RegistryDiscussionQueries.MoreComments, RegistryDiscussionQueries.ParseMoreComments, cancellationToken).ConfigureAwait(false);
                if (!likers.IsOk || !comments.IsOk)
                {
                    return RatingResult<IReadOnlyList<CollectedDiscussion>>.Failed(likers.Error ?? comments.Error!);
                }

                result.Add(discussion with { Likers = [.. discussion.Likers, .. likers.Value], Comments = [.. discussion.Comments, .. comments.Value] });
            }

            after = page.Value.Next;
        }
        while (after is not null);

        return RatingResult<IReadOnlyList<CollectedDiscussion>>.Ok(result);
    }

    public Task<RatingResult<CreatedDiscussion>> CreateAsync(DiscussionCategory category, string title, string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        return CallAsync(RegistryDiscussionQueries.Create(category.RepositoryId, category.CategoryId!, title, body), RegistryDiscussionQueries.ParseCreated, cancellationToken);
    }

    /// <summary>Adds a comment to a Discussion (the request collector's "done" / "merged" notes, task 74).</summary>
    public Task<RatingResult<bool>> CommentAsync(string discussionId, string body, CancellationToken cancellationToken = default) =>
        CallAsync(Requests.RequestQueries.AddComment(discussionId, body), Requests.RequestQueries.ParseDone, cancellationToken);

    /// <summary>Closes a Discussion: <c>RESOLVED</c> (a done request) or <c>DUPLICATE</c>.</summary>
    public Task<RatingResult<bool>> CloseAsync(string discussionId, string reason, CancellationToken cancellationToken = default) =>
        CallAsync(Requests.RequestQueries.Close(discussionId, reason), Requests.RequestQueries.ParseDone, cancellationToken);

    /// <summary>The pages after the first one of a Discussion's likers or comments.</summary>
    private async Task<RatingResult<IReadOnlyList<T>>> RestAsync<T>(
        string id,
        string? after,
        Func<string, string, JsonObject> request,
        Func<JsonElement, RatingResult<GraphQlPage<T>>> parse,
        CancellationToken cancellationToken)
    {
        var items = new List<T>();
        while (after is not null)
        {
            var page = await CallAsync(request(id, after), parse, cancellationToken).ConfigureAwait(false);
            if (!page.IsOk)
            {
                return RatingResult<IReadOnlyList<T>>.Failed(page.Error);
            }

            items.AddRange(page.Value.Items);
            after = page.Value.Next;
        }

        return RatingResult<IReadOnlyList<T>>.Ok(items);
    }

    private async Task<RatingResult<T>> CallAsync<T>(JsonObject request, Func<JsonElement, RatingResult<T>> parse, CancellationToken cancellationToken)
    {
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
