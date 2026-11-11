using System.Net;
using System.Text.Json.Nodes;

namespace AiLocalPack.TestKit;

/// <summary>A Discussion of the fake GitHub: its 👍 givers and comments (task 63's GraphQL calls).</summary>
public sealed class FakeDiscussion(string id, string repo, int number)
{
    public string Id => id;

    public Uri Url => new($"https://github.com/{repo}/discussions/{number}");

    public bool Locked { get; set; }

    /// <summary>The Discussion category's name (the collector reads <c>Packs</c>).</summary>
    public string Category { get; set; } = "Packs";

    public string Title { get; set; } = "";

    /// <summary>The first post (a pack's Discussion starts with its marker).</summary>
    public string Body { get; set; } = "";

    /// <summary>Logins that gave a 👍.</summary>
    public HashSet<string> ThumbsUp { get; } = new(StringComparer.Ordinal);

    public List<FakeDiscussionComment> Comments { get; } = [];
}

/// <summary>A comment in a <see cref="FakeDiscussion"/>.</summary>
public sealed class FakeDiscussionComment(string id, string author, string body)
{
    public string Id => id;

    public string Author => author;

    public string Body { get; set; } = body;

    public DateTimeOffset CreatedAt { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset? LastEditedAt { get; set; }

    /// <summary>When the author's account was made (old enough to count by default).</summary>
    public DateTimeOffset AuthorCreatedAt { get; set; } = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public bool AuthorIsBot { get; set; }

    /// <summary>The account was deleted (GitHub answers <c>author: null</c>).</summary>
    public bool AuthorDeleted { get; set; }
}

public sealed partial class FakeGitHubApi
{
    private readonly Dictionary<(string Repo, int Number), FakeDiscussion> _discussions = [];

    /// <summary>Discussion categories every repository has (the registry's <c>Packs</c>).</summary>
    public List<string> DiscussionCategories { get; } = ["General", "Packs"];

    /// <summary>The Discussions of <paramref name="repo"/> by number.</summary>
    public IReadOnlyList<FakeDiscussion> DiscussionsOf(string repo)
    {
        lock (_lock)
        {
            return [.. _discussions.Where(d => d.Key.Repo == repo.ToLowerInvariant()).OrderBy(d => d.Key.Number).Select(d => d.Value)];
        }
    }

    /// <summary>Adds Discussion #<paramref name="number"/> to <paramref name="repo"/> (no repository needed).</summary>
    public FakeDiscussion AddDiscussion(string repo, int number)
    {
        lock (_lock)
        {
            var discussion = new FakeDiscussion($"D_{_nextId++}", repo, number);
            _discussions[(repo.ToLowerInvariant(), number)] = discussion;
            return discussion;
        }
    }

    /// <summary>
    /// The GraphQL calls of ratings, told apart by the operation they name: the Discussion's state (viewer, 👍, latest
    /// comments), <c>addReaction</c> / <c>removeReaction</c>, <c>addDiscussionComment</c> / <c>updateDiscussionComment</c>.
    /// </summary>
    private HttpResponseMessage GraphQl(JsonNode request)
    {
        var query = request["query"]?.GetValue<string>() ?? string.Empty;
        var variables = request["variables"] as JsonObject ?? [];
        if (query.Contains("addReaction", StringComparison.Ordinal) || query.Contains("removeReaction", StringComparison.Ordinal))
        {
            if (ById(variables["id"]?.GetValue<string>()) is not { } discussion)
            {
                return GraphQlError("NOT_FOUND", "Could not resolve to a node.");
            }

            if (query.Contains("addReaction", StringComparison.Ordinal))
            {
                discussion.ThumbsUp.Add(Login);
            }
            else
            {
                discussion.ThumbsUp.Remove(Login);
            }

            return Ok(new JsonObject { ["data"] = new JsonObject { ["x"] = new JsonObject { ["reaction"] = new JsonObject { ["content"] = "THUMBS_UP" } } } });
        }

        if (query.Contains("addDiscussionComment", StringComparison.Ordinal))
        {
            if (ById(variables["id"]?.GetValue<string>()) is not { } discussion)
            {
                return GraphQlError("NOT_FOUND", "Could not resolve to a node.");
            }

            var comment = new FakeDiscussionComment($"DC_{_nextId++}", Login, variables["body"]!.GetValue<string>());
            discussion.Comments.Add(comment);
            return Ok(CommentAnswer("addDiscussionComment", discussion, comment));
        }

        if (query.Contains("updateDiscussionComment", StringComparison.Ordinal))
        {
            var id = variables["id"]?.GetValue<string>();
            foreach (var discussion in _discussions.Values)
            {
                if (discussion.Comments.FirstOrDefault(c => c.Id == id) is { } comment)
                {
                    if (comment.Author != Login)
                    {
                        return GraphQlError("FORBIDDEN", "Viewer can't update this comment.");
                    }

                    comment.Body = variables["body"]!.GetValue<string>();
                    return Ok(CommentAnswer("updateDiscussionComment", discussion, comment));
                }
            }

            return GraphQlError("NOT_FOUND", "Could not resolve to a node.");
        }

        if (CollectorCall(query, variables) is { } answer)
        {
            return answer;
        }

        var key = ((variables["owner"]?.GetValue<string>() + "/" + variables["name"]?.GetValue<string>()).ToLowerInvariant(), variables["number"]?.GetValue<int>() ?? 0);
        JsonNode? found = null;
        if (_discussions.TryGetValue(key, out var d))
        {
            var comments = new JsonArray();
            foreach (var comment in d.Comments.TakeLast(100))
            {
                comments.Add(new JsonObject
                {
                    ["id"] = comment.Id,
                    ["url"] = $"{d.Url.AbsoluteUri}#discussioncomment-{comment.Id}",
                    ["body"] = comment.Body,
                    ["viewerDidAuthor"] = comment.Author == Login,
                });
            }

            found = new JsonObject
            {
                ["id"] = d.Id,
                ["url"] = d.Url.AbsoluteUri,
                ["locked"] = d.Locked,
                ["reactionGroups"] = new JsonArray(new JsonObject
                {
                    ["content"] = "THUMBS_UP",
                    ["viewerHasReacted"] = d.ThumbsUp.Contains(Login),
                    ["reactors"] = new JsonObject { ["totalCount"] = d.ThumbsUp.Count },
                }),
                ["comments"] = new JsonObject { ["nodes"] = comments },
            };
        }

        return Ok(new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["viewer"] = new JsonObject { ["login"] = Login },
                ["repository"] = new JsonObject { ["discussion"] = found },
            },
        });
    }

    /// <summary>
    /// The calls of <c>ratings collect</c> (task 64): the repository's categories, a category's Discussions (25 a page,
    /// 100 comments and 👍 givers each), the further pages of one Discussion's comments or 👍 givers (cursor = the index
    /// of the next item), <c>createDiscussion</c>. <c>null</c> for any other call.
    /// </summary>
    private HttpResponseMessage? CollectorCall(string query, JsonObject variables)
    {
        if (query.Contains("discussionCategories", StringComparison.Ordinal))
        {
            var repo = $"{variables["owner"]?.GetValue<string>()}/{variables["name"]?.GetValue<string>()}".ToLowerInvariant();
            var categories = new JsonArray([.. DiscussionCategories.Select(c => (JsonNode)new JsonObject { ["id"] = CategoryId(c), ["name"] = c })]);
            return Ok(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["repository"] = new JsonObject { ["id"] = $"R_{repo}", ["discussionCategories"] = new JsonObject { ["nodes"] = categories } },
                },
            });
        }

        if (query.Contains("createDiscussion", StringComparison.Ordinal))
        {
            var repo = (variables["repository"]?.GetValue<string>() ?? "")["R_".Length..];
            var category = DiscussionCategories.FirstOrDefault(c => CategoryId(c) == variables["category"]?.GetValue<string>());
            if (category is null)
            {
                return GraphQlError("NOT_FOUND", "Could not resolve to a DiscussionCategory.");
            }

            var number = _discussions.Keys.Where(k => k.Repo == repo).Select(k => k.Number).DefaultIfEmpty(0).Max() + 1;
            var created = new FakeDiscussion($"D_{_nextId++}", repo, number)
            {
                Category = category,
                Title = variables["title"]!.GetValue<string>(),
                Body = variables["body"]!.GetValue<string>(),
            };
            _discussions[(repo, number)] = created;
            return Ok(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["createDiscussion"] = new JsonObject
                    {
                        ["discussion"] = new JsonObject { ["id"] = created.Id, ["number"] = number, ["url"] = created.Url.AbsoluteUri },
                    },
                },
            });
        }

        if (query.Contains("discussions(first", StringComparison.Ordinal))
        {
            var repo = $"{variables["owner"]?.GetValue<string>()}/{variables["name"]?.GetValue<string>()}".ToLowerInvariant();
            var all = _discussions.Where(d => d.Key.Repo == repo && CategoryId(d.Value.Category) == variables["category"]?.GetValue<string>())
                .OrderBy(d => d.Key.Number).Select(d => d.Value).ToList();
            var start = Cursor(variables["after"]);
            var page = new JsonArray();
            foreach (var d in all.Skip(start).Take(25))
            {
                page.Add(new JsonObject
                {
                    ["id"] = d.Id,
                    ["number"] = int.Parse(d.Url.Segments[^1], System.Globalization.CultureInfo.InvariantCulture),
                    ["url"] = d.Url.AbsoluteUri,
                    ["body"] = d.Body,
                    ["reactions"] = Likers(d, 0),
                    ["comments"] = CommentPage(d, 0),
                });
            }

            return Ok(new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["repository"] = new JsonObject { ["discussions"] = new JsonObject { ["pageInfo"] = PageInfo(start + 25, all.Count), ["nodes"] = page } },
                },
            });
        }

        if (query.Contains("node(id", StringComparison.Ordinal))
        {
            if (ById(variables["id"]?.GetValue<string>()) is not { } d)
            {
                return Ok(new JsonObject { ["data"] = new JsonObject { ["node"] = null } });
            }

            var start = Cursor(variables["after"]);
            var node = query.Contains("comments(", StringComparison.Ordinal)
                ? new JsonObject { ["comments"] = CommentPage(d, start) }
                : new JsonObject { ["reactions"] = Likers(d, start) };
            return Ok(new JsonObject { ["data"] = new JsonObject { ["node"] = node } });
        }

        return null;
    }

    private static string CategoryId(string name) => $"DIC_{name}";

    private static int Cursor(JsonNode? after) =>
        after?.GetValue<string>() is { } text ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static JsonObject PageInfo(int next, int count) => new()
    {
        ["hasNextPage"] = next < count,
        ["endCursor"] = next < count ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
    };

    private static JsonObject Likers(FakeDiscussion d, int start) => new()
    {
        ["pageInfo"] = PageInfo(start + 100, d.ThumbsUp.Count),
        ["nodes"] = new JsonArray([.. d.ThumbsUp.Order(StringComparer.Ordinal).Skip(start).Take(100)
            .Select(login => (JsonNode)new JsonObject { ["user"] = new JsonObject { ["login"] = login, ["createdAt"] = "2020-01-01T00:00:00Z" } })]),
    };

    private static JsonObject CommentPage(FakeDiscussion d, int start) => new()
    {
        ["pageInfo"] = PageInfo(start + 100, d.Comments.Count),
        ["nodes"] = new JsonArray([.. d.Comments.Skip(start).Take(100).Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["body"] = c.Body,
            ["createdAt"] = c.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["lastEditedAt"] = c.LastEditedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["author"] = c.AuthorDeleted ? null : new JsonObject
            {
                ["__typename"] = c.AuthorIsBot ? "Bot" : "User",
                ["login"] = c.Author,
                ["createdAt"] = c.AuthorIsBot ? null : c.AuthorCreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            },
        })]),
    };

    private FakeDiscussion? ById(string? id) => _discussions.Values.FirstOrDefault(d => d.Id == id);

    private static JsonObject CommentAnswer(string mutation, FakeDiscussion discussion, FakeDiscussionComment comment) => new()
    {
        ["data"] = new JsonObject
        {
            [mutation] = new JsonObject
            {
                ["comment"] = new JsonObject { ["id"] = comment.Id, ["url"] = $"{discussion.Url.AbsoluteUri}#discussioncomment-{comment.Id}" },
            },
        },
    };

    private static HttpResponseMessage GraphQlError(string type, string message) =>
        Ok(new JsonObject { ["data"] = null, ["errors"] = new JsonArray(new JsonObject { ["type"] = type, ["message"] = message }) });
}
