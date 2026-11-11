using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>ratings collect</c> (task 64) on the sample registry against a local fake GitHub, then <c>index build</c> with its output.</summary>
public sealed class RatingsCollectTests : IDisposable
{
    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");

    private readonly TempDir _dir = new();
    private readonly FakeGitHubServer _github = new();

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    private string Out => _dir.Combine("ratings.json");

    private Task<CliResult> CollectAsync(params string[] extra) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
                ["AILOCALPACK_SECRETS_FILE"] = _dir.Combine("secrets.json"),
                ["AILOCALPACK_GITHUB_URL"] = _github.Url,
                ["GITHUB_TOKEN"] = FakeGitHubServer.Token,
            },
            ["ratings", "collect", Registry, "--out", Out, "--repo", "ailocalpack/registry", .. extra]);

    /// <summary>The menus pack's Discussion with confirmations of rev 1 on build 1250000 from <paramref name="players"/>.</summary>
    private void ConfirmMenus(params string[] players)
    {
        var discussion = _github.Api.AddDiscussion("ailocalpack/registry", 1);
        discussion.Body = "<!-- ailocalpack-pack steam:620/ru/menus -->\nМеню";
        foreach (var player in players)
        {
            discussion.Comments.Add(new FakeDiscussionComment($"C_{player}", player,
                PackReportComment.Format(new PackReport(PackVerdict.Works, GameId.Steam(620), "ru", "menus", 1, "1250000"))));
        }
    }

    [Fact]
    public async Task Collect_writes_the_ratings_file()
    {
        // Arrange
        ConfirmMenus("p1", "p2");

        // Act
        await CollectAsync();

        // Assert
        RegistryRatings.Read(File.ReadAllText(Out)).Ratings.Should().BeEquivalentTo(new Dictionary<RegistryPackKey, RegistryPackRating>
        {
            [new RegistryPackKey(GameId.Steam(620), "ru", "menus")] = new(new RegistryRating(0, 2, 0), new Uri("https://github.com/ailocalpack/registry/discussions/1"), 1),
        });
    }

    [Fact]
    public async Task Collect_prints_a_line_per_pack()
    {
        // Arrange
        ConfirmMenus("p1", "p2", "p3", "carol");

        // Act
        var result = await CollectAsync();

        // Assert
        result.StdOut.Should().Contain("steam:620/ru/menus rev 1: 3 works, 0 doesn't work, 0 👍, confirmed (left out: 1 author) https://github.com/ailocalpack/registry/discussions/1");
    }

    [Fact]
    public async Task Create_missing_opens_discussions_and_says_so()
    {
        // Act
        var result = await CollectAsync("--create-missing");

        // Assert
        (result.ExitCode, result.StdOut.Trim().Split('\n')[^1]).Should().Be((0, $"Wrote {Out}: 4 packs, 4 Discussions opened."));
    }

    [Fact]
    public async Task Confirmed_pack_is_verified_in_the_built_index()
    {
        // Arrange
        ConfirmMenus("p1", "p2", "p3");
        await CollectAsync();

        // Act
        await CliRunner.RunAsync("index", "build", Registry, "--out", _dir.Combine("index.json"), "--ratings", Out, "--generated", "2026-11-04T12:00:00Z");

        // Assert
        RegistryIndexSerializer.Read(File.ReadAllText(_dir.Combine("index.json"))).Index!
            .PacksFor(GameId.Steam(620), "ru").Single(p => p.Id == "menus").Status.Should().Be(PackStatus.Verified);
    }

    [Fact]
    public async Task Missing_category_writes_nothing()
    {
        // Arrange
        _github.Api.DiscussionCategories.Remove("Packs");

        // Act
        var result = await CollectAsync();

        // Assert
        (result.ExitCode, File.Exists(Out), result.StdErr).Should().Be((1, false,
            "Can't read the Discussions of ailocalpack/registry (NotFound): ailocalpack/registry has no Discussion category \"Packs\" (made by hand in the repository settings).\nNothing written.\n"));
    }

    [Fact]
    public async Task Without_a_token_or_sign_in_it_refuses()
    {
        // Act
        var result = await CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_SECRETS_FILE"] = _dir.Combine("secrets.json"),
                ["AILOCALPACK_GITHUB_URL"] = _github.Url,
                ["GITHUB_TOKEN"] = "",
            },
            "ratings", "collect", Registry, "--out", Out);

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "No GitHub token: set GITHUB_TOKEN or run 'ailocalpack github login' first."));
    }
}
