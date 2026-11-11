using System.Text.Json;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Ratings;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary>The GraphQL requests of ratings and the parsing of GitHub's answers (task 63).</summary>
public sealed class PackDiscussionQueriesTests
{
    private static readonly DiscussionRef Discussion = new("ailocalpack", "registry", 42);

    internal static readonly PackReport Report = new(PackVerdict.Works, GameId.Steam(620), "ru", "full", 3, "9087113");

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    internal static string Comment(string id, string body, bool mine) =>
        JsonSerializer.Serialize(new { id, url = $"https://github.com/ailocalpack/registry/discussions/42#discussioncomment-{id}", body, viewerDidAuthor = mine });

    internal static string State(int likes = 5, bool liked = false, bool locked = false, params string[] comments) => """
        {"data":{"viewer":{"login":"bob"},"repository":{"discussion":{
          "id":"D_kwDO42","url":"https://github.com/ailocalpack/registry/discussions/42","locked":LOCKED,
          "reactionGroups":[{"content":"HEART","viewerHasReacted":true,"reactors":{"totalCount":9}},{"content":"THUMBS_UP","viewerHasReacted":LIKED,"reactors":{"totalCount":LIKES}}],
          "comments":{"nodes":[COMMENTS]}}}}}
        """
        .Replace("LOCKED", locked ? "true" : "false", StringComparison.Ordinal)
        .Replace("LIKED", liked ? "true" : "false", StringComparison.Ordinal)
        .Replace("LIKES", likes.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("COMMENTS", string.Join(",", comments), StringComparison.Ordinal);

    // ---------- Discussion links ----------

    [Fact]
    public void A_discussion_link_gives_owner_repository_and_number()
    {
        // Act
        var parsed = DiscussionRef.TryParse(new Uri("https://github.com/ailocalpack/registry/discussions/42"), out var discussion);

        // Assert
        parsed.Should().BeTrue();
        discussion.Should().Be(Discussion);
    }

    [Theory]
    [InlineData("https://github.com/ailocalpack/registry/pull/42")]
    [InlineData("https://github.com/ailocalpack/registry/discussions/x")]
    [InlineData("https://github.com/ailocalpack/registry/discussions/0")]
    [InlineData("http://github.com/ailocalpack/registry/discussions/42")]
    [InlineData("https://github.com/ailocalpack/discussions/42")]
    public void Anything_else_is_not_a_discussion(string url)
    {
        // Act
        var parsed = DiscussionRef.TryParse(new Uri(url), out _);

        // Assert
        parsed.Should().BeFalse();
    }

    // ---------- Requests ----------

    [Fact]
    public void The_state_request_asks_for_the_discussion_by_owner_name_and_number()
    {
        // Act
        var request = PackDiscussionQueries.State(Discussion);

        // Assert
        request["query"]!.GetValue<string>().Should().Be(PackDiscussionQueries.StateQuery);
        request["variables"]!.ToJsonString().Should().Be("""{"owner":"ailocalpack","name":"registry","number":42}""");
    }

    [Fact]
    public void The_state_query_reads_thumbs_up_and_the_latest_comments()
    {
        // Act
        var query = PackDiscussionQueries.StateQuery;

        // Assert
        query.Should().Contain("reactionGroups { content viewerHasReacted reactors { totalCount } }")
            .And.Contain($"comments(last: {PackDiscussionQueries.CommentsRead})")
            .And.Contain("viewerDidAuthor");
    }

    [Fact]
    public void A_like_adds_a_thumbs_up_reaction_to_the_discussion()
    {
        // Act
        var request = PackDiscussionQueries.Like("D_kwDO42", like: true);

        // Assert
        request["query"]!.GetValue<string>().Should().Contain("addReaction(input: {subjectId: $id, content: THUMBS_UP})");
        request["variables"]!.ToJsonString().Should().Be("""{"id":"D_kwDO42"}""");
    }

    [Fact]
    public void Taking_a_like_back_removes_the_thumbs_up_reaction()
    {
        // Act
        var request = PackDiscussionQueries.Like("D_kwDO42", like: false);

        // Assert
        request["query"]!.GetValue<string>().Should().Contain("removeReaction(input: {subjectId: $id, content: THUMBS_UP})");
    }

    [Fact]
    public void A_new_report_is_a_discussion_comment_with_the_body()
    {
        // Act
        var request = PackDiscussionQueries.AddComment("D_kwDO42", "body");

        // Assert
        request["query"]!.GetValue<string>().Should().Contain("addDiscussionComment(input: {discussionId: $id, body: $body})");
        request["variables"]!.ToJsonString().Should().Be("""{"id":"D_kwDO42","body":"body"}""");
    }

    [Fact]
    public void A_replaced_report_edits_the_comment()
    {
        // Act
        var request = PackDiscussionQueries.UpdateComment("DC_1", "body");

        // Assert
        request["query"]!.GetValue<string>().Should().Contain("updateDiscussionComment(input: {commentId: $id, body: $body})");
        request["variables"]!.ToJsonString().Should().Be("""{"id":"DC_1","body":"body"}""");
    }

    // ---------- Answers ----------

    [Fact]
    public void The_state_gives_the_thumbs_up_count_and_the_viewers_reaction()
    {
        // Act
        var state = PackDiscussionQueries.ParseState(Json(State(likes: 7, liked: true)));

        // Assert
        state.Value.Should().BeEquivalentTo(new { Id = "D_kwDO42", Likes = 7, ViewerLiked = true, Locked = false, ViewerLogin = "bob" });
    }

    [Fact]
    public void Only_report_comments_are_read_with_who_wrote_them()
    {
        // Arrange
        var answer = State(comments: [Comment("1", "Nice pack!", mine: false), Comment("2", PackReportComment.Format(Report), mine: true)]);

        // Act
        var state = PackDiscussionQueries.ParseState(Json(answer));

        // Assert
        state.Value!.Reports.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = "2", Report, ByViewer = true });
    }

    [Fact]
    public void The_viewers_report_of_the_revision_is_the_latest_one()
    {
        // Arrange
        var answer = State(comments: [
            Comment("1", PackReportComment.Format(Report), mine: true),
            Comment("2", PackReportComment.Format(Report), mine: false),
            Comment("3", PackReportComment.Format(Report with { Verdict = PackVerdict.DoesNotWork }), mine: true),
            Comment("4", PackReportComment.Format(Report with { Revision = 2 }), mine: true)]);
        var state = PackDiscussionQueries.ParseState(Json(answer)).Value!;

        // Act
        var mine = state.ViewerReport(Report);

        // Assert
        mine!.Id.Should().Be("3");
    }

    [Fact]
    public void A_discussion_without_thumbs_up_has_no_likes()
    {
        // Arrange
        var answer = """{"data":{"viewer":{"login":"bob"},"repository":{"discussion":{"id":"D_1","url":"https://github.com/a/b/discussions/1","locked":false,"reactionGroups":[],"comments":{"nodes":[]}}}}}""";

        // Act
        var state = PackDiscussionQueries.ParseState(Json(answer));

        // Assert
        state.Value.Should().BeEquivalentTo(new { Likes = 0, ViewerLiked = false });
    }

    [Fact]
    public void A_missing_discussion_is_not_found()
    {
        // Arrange
        var answer = """{"data":{"viewer":{"login":"bob"},"repository":{"discussion":null}}}""";

        // Act
        var state = PackDiscussionQueries.ParseState(Json(answer));

        // Assert
        state.Error!.Problem.Should().Be(RatingProblem.NotFound);
    }

    [Theory]
    [InlineData("NOT_FOUND", "Could not resolve to a Repository", RatingProblem.NotFound)]
    [InlineData("RATE_LIMITED", "API rate limit exceeded", RatingProblem.RateLimited)]
    [InlineData("FORBIDDEN", "Resource not accessible by integration", RatingProblem.Forbidden)]
    [InlineData("INSUFFICIENT_SCOPES", "Your token has not been granted the required scopes", RatingProblem.Forbidden)]
    [InlineData("UNPROCESSABLE", "This discussion is locked", RatingProblem.Locked)]
    [InlineData(null, "Something odd", RatingProblem.Unexpected)]
    public void GraphQL_errors_are_typed(string? type, string message, RatingProblem expected)
    {
        // Arrange
        var answer = Json(JsonSerializer.Serialize(new { data = (object?)null, errors = new[] { new { type, message } } }));

        // Act
        var error = PackDiscussionQueries.Errors(answer);

        // Assert
        error.Should().Be(new RatingError(expected, message));
    }

    [Fact]
    public void A_comment_answer_gives_its_id_and_link()
    {
        // Arrange
        var answer = """{"data":{"addDiscussionComment":{"comment":{"id":"DC_9","url":"https://github.com/ailocalpack/registry/discussions/42#discussioncomment-9"}}}}""";

        // Act
        var sent = PackDiscussionQueries.ParseComment(Json(answer), "addDiscussionComment", replaced: false);

        // Assert
        sent.Value.Should().Be(new SentReport("DC_9", new Uri("https://github.com/ailocalpack/registry/discussions/42#discussioncomment-9"), false));
    }

    [Fact]
    public void A_comment_answer_without_a_comment_is_unexpected()
    {
        // Act
        var sent = PackDiscussionQueries.ParseComment(Json("""{"data":{"addDiscussionComment":null}}"""), "addDiscussionComment", replaced: false);

        // Assert
        sent.Error!.Problem.Should().Be(RatingProblem.Unexpected);
    }
}
