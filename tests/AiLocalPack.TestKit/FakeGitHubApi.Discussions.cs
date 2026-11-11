using System.Net;
using System.Text.Json.Nodes;

namespace AiLocalPack.TestKit;

/// <summary>A Discussion of the fake GitHub: its 👍 givers and comments (task 63's GraphQL calls).</summary>
public sealed class FakeDiscussion(string id, string repo, int number)
{
    public string Id => id;

    public Uri Url => new($"https://github.com/{repo}/discussions/{number}");

    public bool Locked { get; set; }

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
}

public sealed partial class FakeGitHubApi
{
    private readonly Dictionary<(string Repo, int Number), FakeDiscussion> _discussions = [];

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
