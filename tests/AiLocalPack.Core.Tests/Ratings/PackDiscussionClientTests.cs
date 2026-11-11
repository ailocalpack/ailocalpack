using System.Net;
using System.Text;
using System.Text.Json;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Ratings;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary>Likes and reports through GitHub's GraphQL API, against a fake GitHub (task 63).</summary>
public sealed class PackDiscussionClientTests : IDisposable
{
    private const string GraphQl = "https://api.github.example/graphql";
    private static readonly GitHubOAuthApp App = new("test-client", new Uri("https://github.example/"), new Uri("https://api.github.example/"));
    private static readonly DiscussionRef Discussion = new("ailocalpack", "registry", 42);
    private static readonly PackReport Report = PackDiscussionQueriesTests.Report;

    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly PackDiscussionClient _discussions;
    private readonly List<string> _queries = [];
    private string _state = PackDiscussionQueriesTests.State();

    public PackDiscussionClientTests()
    {
        _client = _http.CreateClient();
        _discussions = new PackDiscussionClient(_client, App);
        _http.On(HttpMethod.Post, GraphQl, request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body);
            var query = json.RootElement.GetProperty("query").GetString()!;
            _queries.Add(query);
            var answer = query switch
            {
                _ when query.Contains("addDiscussionComment", StringComparison.Ordinal) => """{"data":{"addDiscussionComment":{"comment":{"id":"DC_new","url":"https://github.com/ailocalpack/registry/discussions/42#discussioncomment-7"}}}}""",
                _ when query.Contains("updateDiscussionComment", StringComparison.Ordinal) => """{"data":{"updateDiscussionComment":{"comment":{"id":"DC_mine","url":"https://github.com/ailocalpack/registry/discussions/42#discussioncomment-3"}}}}""",
                _ when query.Contains("Reaction", StringComparison.Ordinal) => """{"data":{"addReaction":{"reaction":{"content":"THUMBS_UP"}}}}""",
                _ => _state,
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_go_to_graphql_with_the_players_token()
    {
        // Act
        await _discussions.ReadAsync(Discussion, "gho_token", Ct);

        // Assert
        _http.Requests.Should().ContainSingle().Which.Header("Authorization").Should().Be("Bearer gho_token");
    }

    [Fact]
    public async Task A_like_adds_the_reaction_and_counts_it()
    {
        // Act
        var liked = await _discussions.SetLikeAsync(Discussion, like: true, "token", Ct);

        // Assert
        liked.Value.Should().Be(new LikeState(true, 6));
        _queries.Last().Should().Contain("addReaction");
    }

    [Fact]
    public async Task A_like_already_given_sends_nothing_more()
    {
        // Arrange
        _state = PackDiscussionQueriesTests.State(likes: 5, liked: true);

        // Act
        var liked = await _discussions.SetLikeAsync(Discussion, like: true, "token", Ct);

        // Assert
        liked.Value.Should().Be(new LikeState(true, 5));
        _queries.Should().ContainSingle();
    }

    [Fact]
    public async Task Taking_the_like_back_removes_the_reaction()
    {
        // Arrange
        _state = PackDiscussionQueriesTests.State(likes: 5, liked: true);

        // Act
        var liked = await _discussions.SetLikeAsync(Discussion, like: false, "token", Ct);

        // Assert
        liked.Value.Should().Be(new LikeState(false, 4));
        _queries.Last().Should().Contain("removeReaction");
    }

    [Fact]
    public async Task A_first_report_is_a_new_comment_in_the_report_format()
    {
        // Act
        var sent = await _discussions.ReportAsync(Discussion, Report, "token", Ct);

        // Assert
        sent.Value.Should().BeEquivalentTo(new { CommentId = "DC_new", Replaced = false });
        Variables(_http.Requests.Last()).GetProperty("body").GetString().Should().Be(PackReportComment.Format(Report));
    }

    [Fact]
    public async Task A_second_report_of_the_same_revision_edits_the_players_earlier_one()
    {
        // Arrange
        _state = PackDiscussionQueriesTests.State(comments: [PackDiscussionQueriesTests.Comment("DC_mine", PackReportComment.Format(Report), mine: true)]);

        // Act
        var sent = await _discussions.ReportAsync(Discussion, Report with { Verdict = PackVerdict.DoesNotWork }, "token", Ct);

        // Assert
        sent.Value!.Replaced.Should().BeTrue();
        Variables(_http.Requests.Last()).GetProperty("id").GetString().Should().Be("DC_mine");
    }

    [Fact]
    public async Task Someone_elses_report_of_the_revision_is_left_alone()
    {
        // Arrange
        _state = PackDiscussionQueriesTests.State(comments: [PackDiscussionQueriesTests.Comment("DC_alice", PackReportComment.Format(Report), mine: false)]);

        // Act
        var sent = await _discussions.ReportAsync(Discussion, Report, "token", Ct);

        // Assert
        sent.Value!.CommentId.Should().Be("DC_new");
    }

    [Fact]
    public async Task A_locked_discussion_takes_no_report()
    {
        // Arrange
        _state = PackDiscussionQueriesTests.State(locked: true);

        // Act
        var sent = await _discussions.ReportAsync(Discussion, Report, "token", Ct);

        // Assert
        sent.Error!.Problem.Should().Be(RatingProblem.Locked);
        _queries.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RatingProblem.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway, RatingProblem.Network)]
    public async Task Http_errors_are_typed(HttpStatusCode status, RatingProblem expected)
    {
        // Arrange
        _http.On(HttpMethod.Post, GraphQl, _ => new HttpResponseMessage(status) { Content = new StringContent("""{"message":"nope"}""") });

        // Act
        var read = await _discussions.ReadAsync(Discussion, "token", Ct);

        // Assert
        read.Error!.Problem.Should().Be(expected);
    }

    private static JsonElement Variables(RecordedRequest request)
    {
        using var json = JsonDocument.Parse(request.Body!);
        return json.RootElement.GetProperty("variables").Clone();
    }
}
