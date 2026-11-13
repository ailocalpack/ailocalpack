using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Requests;

/// <summary><c>requests collect</c>'s collector on the sample registry against the fake GitHub (task 74).</summary>
public sealed class RequestCollectorTests : IDisposable
{
    private const string Repo = "ailocalpack/registry";
    private static readonly GitHubOAuthApp App = new("test-client", new Uri("https://github.example/"), new Uri("https://api.github.example/"));
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly RequestKey French = new(GameId.Steam(620), "fr");

    private readonly FakeGitHubApi _github = new(App.Api, "maintainer");
    private readonly HttpClient _http;
    private readonly RequestCollector _collector;

    public RequestCollectorTests()
    {
        _http = new HttpClient(_github, disposeHandler: false);
        _collector = new RequestCollector(new RegistryDiscussionClient(_http, App, FakeGitHubApi.Token), new FakeClock(Now));
    }

    public void Dispose()
    {
        _http.Dispose();
        _github.Dispose();
    }

    private FakeDiscussion Request(RequestKey key, int number, params string[] voters)
    {
        var discussion = _github.AddDiscussion(Repo, number);
        discussion.Category = "Requests";
        discussion.Body = RequestDiscussionText.Body(key, "Sample");
        discussion.CreatedAt = new DateTimeOffset(2026, 11, number, 10, 0, 0, TimeSpan.Zero);
        discussion.ThumbsUp.UnionWith(voters);
        return discussion;
    }

    private static FakeDiscussionComment Claim(string login, RequestClaimState state, DateTimeOffset at, RequestKey? key = null) =>
        new($"C_{login}_{at.Ticks}", login, RequestClaimComment.Format(new RequestClaim(key ?? French, state, GenerationStage.Translate, 45, "claude")))
        {
            CreatedAt = at,
        };

    private Task<RatingResult<RequestCollection>> CollectAsync(bool close = false) =>
        _collector.CollectAsync(RegistryFixtures.Folder, new RegistryRepo("ailocalpack", "registry"), close, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Open_request_is_collected_with_its_votes()
    {
        // Arrange
        var discussion = Request(French, 1, "p1", "p2");

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Requests[French].Should().Be(new RegistryRequest(2, discussion.Url, discussion.CreatedAt));
    }

    [Fact]
    public async Task Votes_of_duplicates_count_once_on_the_oldest()
    {
        // Arrange
        var oldest = Request(French, 1, "p1", "p2");
        Request(French, 2, "p2", "p3");

        // Act
        var result = await CollectAsync();

        // Assert
        (result.Value!.Requests[French].Votes, result.Value.Requests[French].Discussion).Should().Be((3, oldest.Url));
    }

    [Fact]
    public async Task Duplicates_are_closed_when_asked()
    {
        // Arrange
        Request(French, 1, "p1");
        var newer = Request(French, 2, "p2");

        // Act
        await CollectAsync(close: true);

        // Assert
        (newer.Closed, newer.CloseReason).Should().Be((true, "DUPLICATE"));
    }

    [Fact]
    public async Task Closed_discussion_is_not_collected()
    {
        // Arrange
        Request(French, 1, "p1").Closed = true;

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_without_votes_is_left_out()
    {
        // Arrange
        Request(French, 1);

        // Act
        var result = await CollectAsync();

        // Assert
        (result.Value!.Requests.Count, result.Value.Lines.Single().Outcome).Should().Be((0, RequestOutcome.NoVotes));
    }

    [Fact]
    public async Task Request_with_a_pack_is_done()
    {
        // Arrange
        Request(new RequestKey(GameId.Steam(620), "ru"), 1, "p1");

        // Act
        var result = await CollectAsync();

        // Assert
        (result.Value!.Requests.Count, result.Value.Lines.Single().Outcome).Should().Be((0, RequestOutcome.Done));
    }

    [Fact]
    public async Task Done_request_is_closed_with_a_comment_naming_the_pack()
    {
        // Arrange
        var discussion = Request(new RequestKey(GameId.Steam(620), "ru"), 1, "p1");

        // Act
        await CollectAsync(close: true);

        // Assert
        (discussion.CloseReason, discussion.Comments.Single().Body.Contains("«Только меню»", StringComparison.Ordinal)).Should().Be(("RESOLVED", true));
    }

    [Fact]
    public async Task Request_of_an_opted_out_game_is_left_out()
    {
        // Arrange
        Request(new RequestKey(GameId.Steam(400), "ru"), 1, "p1");

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Lines.Single().Outcome.Should().Be(RequestOutcome.OptedOut);
    }

    [Fact]
    public async Task Newest_live_claim_is_the_requests_claim()
    {
        // Arrange
        var discussion = Request(French, 1, "p1");
        discussion.Comments.Add(Claim("alice", RequestClaimState.Working, Now.AddDays(-3)));
        discussion.Comments.Add(Claim("bob", RequestClaimState.Built, Now.AddDays(-1)));

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Requests[French].Claim.Should().Be(new RegistryRequestClaim("bob", RequestClaimState.Built, null, 45, Now.AddDays(-1)));
    }

    [Fact]
    public async Task Claim_without_news_for_two_weeks_is_dropped()
    {
        // Arrange
        Request(French, 1, "p1").Comments.Add(Claim("alice", RequestClaimState.Working, Now.AddDays(-15)));

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Requests[French].Claim.Should().BeNull();
    }

    [Fact]
    public async Task Stopped_claim_is_dropped()
    {
        // Arrange
        Request(French, 1, "p1").Comments.Add(Claim("alice", RequestClaimState.Stopped, Now.AddHours(-1)));

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Requests[French].Claim.Should().BeNull();
    }

    [Fact]
    public async Task Claim_of_another_request_is_ignored()
    {
        // Arrange
        Request(French, 1, "p1").Comments.Add(Claim("alice", RequestClaimState.Working, Now.AddHours(-1), new RequestKey(GameId.Steam(620), "de")));

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Requests[French].Claim.Should().BeNull();
    }

    [Fact]
    public async Task Pack_discussions_are_not_requests()
    {
        // Arrange
        var pack = _github.AddDiscussion(Repo, 1);
        pack.Body = PackDiscussionMarker.Format(new RegistryPackKey(GameId.Steam(620), "ru", "full"));

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_category_is_a_warning_not_a_failure()
    {
        // Arrange
        _github.DiscussionCategories.Remove("Requests");

        // Act
        var result = await CollectAsync();

        // Assert
        (result.IsOk, result.Value!.Warnings.Count).Should().Be((true, 1));
    }
}
