using System.Net;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary><c>ratings collect</c>'s collector on the sample registry against the fake GitHub (task 64).</summary>
public sealed class RatingCollectorTests : IDisposable
{
    private const string Repo = "ailocalpack/registry";
    private static readonly GitHubOAuthApp App = new("test-client", new Uri("https://github.example/"), new Uri("https://api.github.example/"));
    private static readonly RegistryPackKey Full = new(GameId.Steam(620), "ru", "full");

    private readonly FakeGitHubApi _github = new(App.Api, "maintainer");
    private readonly HttpClient _http;
    private readonly RatingCollector _collector;

    public RatingCollectorTests()
    {
        _http = new HttpClient(_github, disposeHandler: false);
        _collector = new RatingCollector(new RegistryDiscussionClient(_http, App, FakeGitHubApi.Token));
    }

    public void Dispose()
    {
        _http.Dispose();
        _github.Dispose();
    }

    private FakeDiscussion PackDiscussion(RegistryPackKey pack, int number)
    {
        var discussion = _github.AddDiscussion(Repo, number);
        discussion.Body = PackDiscussionMarker.Format(pack) + "\nAbout the pack.";
        return discussion;
    }

    private static FakeDiscussionComment Works(string login, int revision = 2, string build = "1250000") =>
        new($"C_{login}_{revision}", login, PackReportComment.Format(new PackReport(PackVerdict.Works, GameId.Steam(620), "ru", "full", revision, build)));

    private Task<RatingResult<RatingCollection>> CollectAsync(bool createMissing = false) =>
        _collector.CollectAsync(RegistryFixtures.Folder, new RegistryRepo("ailocalpack", "registry"), createMissing, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reports_in_the_packs_discussion_are_counted()
    {
        // Arrange
        var discussion = PackDiscussion(Full, 1);
        discussion.Comments.AddRange([Works("p1"), Works("p2"), Works("p3"), Works("alice")]);
        discussion.ThumbsUp.UnionWith(["p1", "bob-2"]);

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Ratings[Full].Should().Be(new RegistryPackRating(new RegistryRating(1, 3, 0), discussion.Url, 2));
    }

    [Fact]
    public async Task Packs_without_a_discussion_get_one_when_asked()
    {
        // Act
        var result = await CollectAsync(createMissing: true);

        // Assert
        _github.DiscussionsOf(Repo).Select(d => PackDiscussionMarker.TryParse(d.Body, out var key) ? key.ToString() : "?").Should().BeEquivalentTo(
            "fp:3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f/ru/full", "steam:620/de/komplett", "steam:620/ru/full", "steam:620/ru/menus");
    }

    [Fact]
    public async Task Opened_discussions_go_into_the_ratings()
    {
        // Act
        var result = await CollectAsync(createMissing: true);

        // Assert
        (result.Value!.Ratings.Count, result.Value.Packs.Count(p => p.Created)).Should().Be((4, 4));
    }

    [Fact]
    public async Task Opened_discussion_is_in_the_packs_category_with_its_title()
    {
        // Act
        await CollectAsync(createMissing: true);

        // Assert
        _github.DiscussionsOf(Repo).Should().Contain(d => d.Category == "Packs" && d.Title == "Только меню · steam:620/ru/menus");
    }

    [Fact]
    public async Task Existing_discussion_is_not_opened_again()
    {
        // Arrange
        PackDiscussion(Full, 1);

        // Act
        var result = await CollectAsync(createMissing: true);

        // Assert
        result.Value!.Packs.Single(p => p.Pack.Key == Full).Created.Should().BeFalse();
    }

    [Fact]
    public async Task Without_create_missing_nothing_is_opened()
    {
        // Act
        await CollectAsync();

        // Assert
        _github.DiscussionsOf(Repo).Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_discussion_of_a_pack_is_reported_and_the_oldest_counts()
    {
        // Arrange
        var oldest = PackDiscussion(Full, 1);
        PackDiscussion(Full, 2).Comments.AddRange([Works("p1"), Works("p2"), Works("p3")]);

        // Act
        var result = await CollectAsync();

        // Assert
        (result.Value!.Ratings[Full].Discussion, result.Value.Warnings.Single().Message).Should().Be(
            (oldest.Url, "https://github.com/ailocalpack/registry/discussions/2 is another Discussion of steam:620/ru/full; only https://github.com/ailocalpack/registry/discussions/1 counts."));
    }

    [Fact]
    public async Task Discussions_of_other_categories_are_not_read()
    {
        // Arrange
        var general = PackDiscussion(Full, 1);
        general.Category = "General";
        general.Comments.AddRange([Works("p1"), Works("p2"), Works("p3")]);

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Ratings.Should().NotContainKey(Full);
    }

    [Fact]
    public async Task Comments_past_the_first_page_are_counted()
    {
        // Arrange
        var discussion = PackDiscussion(Full, 1);
        discussion.Comments.AddRange(Enumerable.Range(0, 150).Select(i => new FakeDiscussionComment($"C{i}", $"chat{i}", "Nice!")));
        discussion.Comments.AddRange([Works("p1"), Works("p2"), Works("p3")]);

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Ratings[Full].Rating.Works.Should().Be(3);
    }

    [Fact]
    public async Task Discussions_past_the_first_page_are_read()
    {
        // Arrange
        for (var i = 1; i <= 30; i++)
        {
            _github.AddDiscussion(Repo, i).Body = "Other news";
        }

        var discussion = PackDiscussion(Full, 31);

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Ratings[Full].Discussion.Should().Be(discussion.Url);
    }

    [Fact]
    public async Task Missing_category_fails()
    {
        // Arrange
        _github.DiscussionCategories.Remove("Packs");

        // Act
        var result = await CollectAsync();

        // Assert
        result.Error!.Problem.Should().Be(RatingProblem.NotFound);
    }

    [Fact]
    public async Task GitHub_error_fails_the_whole_collection()
    {
        // Arrange
        _github.Fail("POST", "^graphql$", HttpStatusCode.BadGateway, """{"message":"Server Error"}""", times: 10);

        // Act
        var result = await CollectAsync();

        // Assert
        result.Error!.Problem.Should().Be(RatingProblem.Network);
    }

    [Fact]
    public async Task Discussion_of_a_pack_not_in_the_registry_is_reported()
    {
        // Arrange
        PackDiscussion(new RegistryPackKey(GameId.Steam(620), "ru", "gone"), 1);

        // Act
        var result = await CollectAsync();

        // Assert
        result.Value!.Warnings.Should().ContainSingle().Which.Message.Should().Contain("steam:620/ru/gone, which is not in the registry");
    }
}
