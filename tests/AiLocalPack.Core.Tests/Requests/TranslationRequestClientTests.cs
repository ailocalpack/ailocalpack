using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Requests;

namespace AiLocalPack.Core.Tests.Requests;

/// <summary>Asking, voting, withdrawing and claiming through GitHub's GraphQL API, against the fake GitHub (task 74).</summary>
public sealed class TranslationRequestClientTests : IDisposable
{
    private const string Repo = "ailocalpack/registry";
    private static readonly GitHubOAuthApp App = new("test-client", new Uri("https://github.example/"), new Uri("https://api.github.example/"));
    private static readonly RegistryRepo Registry = new("ailocalpack", "registry");
    private static readonly RequestKey Key = new(GameId.Steam(620), "ru");

    private readonly FakeGitHubApi _github = new(App.Api, "mkzm");
    private readonly HttpClient _http;
    private readonly TranslationRequestClient _client;

    public TranslationRequestClientTests()
    {
        _http = new HttpClient(_github, disposeHandler: false);
        _client = new TranslationRequestClient(_http, App);
    }

    public void Dispose()
    {
        _http.Dispose();
        _github.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeDiscussion Existing(int number, RequestKey? key = null)
    {
        var discussion = _github.AddDiscussion(Repo, number);
        discussion.Category = "Requests";
        discussion.Title = RequestDiscussionText.Title(key ?? Key, "Portal");
        discussion.Body = RequestDiscussionText.Body(key ?? Key, "Portal");
        return discussion;
    }

    private Task<RatingResult<RequestState>> AskAsync(Uri? known = null, string? note = null) =>
        _client.AskAsync(Registry, Key, "Portal", known, note, FakeGitHubApi.Token, Ct);

    [Fact]
    public async Task First_ask_opens_the_discussion_in_requests()
    {
        // Act
        await AskAsync();

        // Assert
        _github.DiscussionsOf(Repo).Single().Should().BeEquivalentTo(new { Category = "Requests", Title = "Portal → Русский · steam:620/ru" });
    }

    [Fact]
    public async Task Ask_gives_the_players_vote()
    {
        // Act
        var result = await AskAsync();

        // Assert
        (result.Value!.Votes, result.Value.Voted).Should().Be((1, true));
    }

    [Fact]
    public async Task Ask_finds_the_open_discussion_by_search()
    {
        // Arrange
        var existing = Existing(5);
        existing.ThumbsUp.Add("other");

        // Act
        var result = await AskAsync();

        // Assert
        (result.Value!.Url, _github.DiscussionsOf(Repo).Count, result.Value.Votes).Should().Be((existing.Url, 1, 2));
    }

    [Fact]
    public async Task Ask_uses_the_index_link_first()
    {
        // Arrange
        var linked = Existing(7);

        // Act
        var result = await AskAsync(linked.Url);

        // Assert
        result.Value!.Url.Should().Be(linked.Url);
    }

    [Fact]
    public async Task Closed_discussion_is_not_reused()
    {
        // Arrange
        var closed = Existing(5);
        closed.Closed = true;

        // Act
        var result = await AskAsync(closed.Url);

        // Assert
        result.Value!.Url.Should().NotBe(closed.Url);
    }

    [Fact]
    public async Task Discussion_of_another_request_is_not_reused()
    {
        // Arrange
        var other = Existing(5, new RequestKey(GameId.Steam(620), "de"));

        // Act
        var result = await AskAsync(other.Url);

        // Assert
        result.Value!.Key.Should().Be(Key);
    }

    [Fact]
    public async Task Ask_with_a_note_posts_it()
    {
        // Act
        var result = await AskAsync(note: "Menus first, please.");

        // Assert
        result.Value!.Notes.Single().Body.Should().Be("Menus first, please.");
    }

    [Fact]
    public async Task Asking_again_doesnt_vote_twice()
    {
        // Arrange
        var first = await AskAsync();

        // Act
        var second = await AskAsync(first.Value!.Url);

        // Assert
        second.Value!.Votes.Should().Be(1);
    }

    [Fact]
    public async Task Missing_category_fails_as_not_found()
    {
        // Arrange
        _github.DiscussionCategories.Remove("Requests");

        // Act
        var result = await AskAsync();

        // Assert
        result.Error!.Problem.Should().Be(RatingProblem.NotFound);
    }

    [Fact]
    public async Task Withdraw_takes_the_vote_back()
    {
        // Arrange
        var asked = await AskAsync();
        DiscussionRef.TryParse(asked.Value!.Url, out var link);

        // Act
        var result = await _client.WithdrawAsync(link!, FakeGitHubApi.Token, Ct);

        // Assert
        (result.Value!.Voted, _github.DiscussionsOf(Repo).Single().ThumbsUp.Count).Should().Be((false, 0));
    }

    [Fact]
    public async Task First_claim_adds_a_comment()
    {
        // Arrange
        var discussion = Existing(5);
        var claim = new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Setup, 0, "claude");

        // Act
        var result = await _client.ClaimAsync(new DiscussionRef("ailocalpack", "registry", 5), claim, null, FakeGitHubApi.Token, Ct);

        // Assert
        (result.Value, discussion.Comments.Single().Author).Should().Be((discussion.Comments.Single().Id, "mkzm"));
    }

    [Fact]
    public async Task Later_claim_edits_the_same_comment()
    {
        // Arrange
        var discussion = Existing(5);
        var link = new DiscussionRef("ailocalpack", "registry", 5);
        var first = await _client.ClaimAsync(link, new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Setup, 0), null, FakeGitHubApi.Token, Ct);

        // Act
        await _client.ClaimAsync(link, new RequestClaim(Key, RequestClaimState.Built, null, 100), first.Value, FakeGitHubApi.Token, Ct);

        // Assert
        RequestClaimComment.TryParse(discussion.Comments.Single().Body, out var claim).Should().BeTrue();
        claim!.State.Should().Be(RequestClaimState.Built);
    }

    [Fact]
    public async Task Claim_without_a_known_comment_edits_the_players_own()
    {
        // Arrange
        var discussion = Existing(5);
        var link = new DiscussionRef("ailocalpack", "registry", 5);
        await _client.ClaimAsync(link, new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Setup, 0), null, FakeGitHubApi.Token, Ct);

        // Act
        await _client.ClaimAsync(link, new RequestClaim(Key, RequestClaimState.Stopped), null, FakeGitHubApi.Token, Ct);

        // Assert
        discussion.Comments.Should().ContainSingle();
    }

    [Fact]
    public async Task State_shows_the_live_claim()
    {
        // Arrange
        var discussion = Existing(5);
        discussion.Comments.Add(new FakeDiscussionComment("C1", "alice", RequestClaimComment.Format(new RequestClaim(Key, RequestClaimState.Working, GenerationStage.Translate, 45)))
        {
            CreatedAt = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero),
        });

        // Act
        var state = await _client.ReadAsync(new DiscussionRef("ailocalpack", "registry", 5), FakeGitHubApi.Token, Ct);

        // Assert
        state.Value!.LiveClaim(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero))!.Comment.Author.Should().Be("alice");
    }

    [Fact]
    public async Task Claims_are_not_notes()
    {
        // Arrange
        var discussion = Existing(5);
        discussion.Comments.Add(new FakeDiscussionComment("C1", "alice", RequestClaimComment.Format(new RequestClaim(Key, RequestClaimState.Working))));
        discussion.Comments.Add(new FakeDiscussionComment("C2", "bob", "Please keep the names."));

        // Act
        var state = await _client.ReadAsync(new DiscussionRef("ailocalpack", "registry", 5), FakeGitHubApi.Token, Ct);

        // Assert
        state.Value!.Notes.Select(n => n.Author).Should().Equal("bob");
    }
}
