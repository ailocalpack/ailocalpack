using AiLocalPack.Core.Games;
using AiLocalPack.Core.Ratings;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>pack rate</c> (task 63) against a local fake GitHub and the sample registry index (steam:620/ru/full, Discussion #1).</summary>
public sealed class PackRateTests : IDisposable
{
    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");

    private readonly TempDir _dir = new();
    private readonly FakeGitHubServer _github = new();
    private readonly FakeDiscussion _discussion;

    public PackRateTests() => _discussion = _github.Api.AddDiscussion("ailocalpack/registry", 1);

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
                ["AILOCALPACK_SECRETS_FILE"] = _dir.Combine("secrets.json"),
                ["AILOCALPACK_GITHUB_URL"] = _github.Url,
                ["AILOCALPACK_INDEX_URL"] = Registry,
            },
            args);

    [Fact]
    public async Task Like_gives_the_thumbs_up()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");

        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/full", "--like");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        (result.StdOut.Trim(), _discussion.ThumbsUp.Count).Should().Be(("Liked: 1 👍 on ailocalpack/registry#1", 1));
    }

    [Fact]
    public async Task Works_posts_a_report_of_the_index_revision()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");

        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/full", "--works", "--build", "1250000", "--text", "Всё отлично");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        PackReportComment.TryParse(_discussion.Comments.Single().Body, out var report).Should().BeTrue();
        report.Should().Be(new PackReport(PackVerdict.Works, GameId.Steam(620), "ru", "full", 2, "1250000"));
    }

    [Fact]
    public async Task A_second_report_replaces_the_first()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");
        await RunAsync("pack", "rate", "steam:620/ru/full", "--works", "--build", "1250000");

        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/full", "--doesnt-work", "--build", "1250000");

        // Assert
        result.StdOut.Should().StartWith("Replaced your report: doesn't work, rev 2");
        _discussion.Comments.Should().ContainSingle().Which.Body.Should().Contain("Doesn't work");
    }

    [Fact]
    public async Task Without_an_action_it_shows_the_discussion()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");
        await RunAsync("pack", "rate", "steam:620/ru/full", "--works", "--build", "1250000");

        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/full");

        // Assert
        result.StdOut.Should().Contain("works: steam:620/ru/full rev 2, build 1250000 (yours)");
    }

    [Fact]
    public async Task Without_signing_in_it_says_so()
    {
        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/full", "--like");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "Not signed in: run 'ailocalpack github login' first."));
    }

    [Fact]
    public async Task One_action_at_a_time()
    {
        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/full", "--like", "--works");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "Give one of --like, --unlike, --works, --doesnt-work."));
    }

    [Fact]
    public async Task A_pack_without_a_discussion_needs_one_given()
    {
        // Act
        var result = await RunAsync("pack", "rate", "steam:620/ru/menus", "--like");

        // Assert
        result.StdErr.Should().Contain("The registry index lists no Discussion for steam:620/ru/menus; give --discussion <url>.");
    }
}
