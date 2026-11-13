using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Requests;

namespace AiLocalPack.Cli.Tests;

/// <summary>
/// <c>requests collect</c> + <c>index build --requests</c> and <c>request</c> (task 74) on the sample registry against a
/// local fake GitHub.
/// </summary>
public sealed class RequestsCommandTests : IDisposable
{
    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");
    private static readonly RequestKey French = new(GameId.Steam(620), "fr");

    private readonly TempDir _dir = new();
    private readonly FakeGitHubServer _github = new();

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    private string Out => _dir.Combine("requests.json");

    private Task<CliResult> RunAsync(bool withToken, params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
                ["AILOCALPACK_SECRETS_FILE"] = _dir.Combine("secrets.json"),
                ["AILOCALPACK_GITHUB_URL"] = _github.Url,
                ["AILOCALPACK_INDEX_URL"] = Registry,
                ["GITHUB_TOKEN"] = withToken ? FakeGitHubServer.Token : "",
            },
            args);

    private Task<CliResult> CollectAsync(params string[] extra) =>
        RunAsync(true, ["requests", "collect", Registry, "--out", Out, "--repo", "ailocalpack/registry", .. extra]);

    private FakeDiscussion Request(RequestKey key, int number, params string[] voters)
    {
        var discussion = _github.Api.AddDiscussion("ailocalpack/registry", number);
        discussion.Category = "Requests";
        discussion.Title = RequestDiscussionText.Title(key, "Sample");
        discussion.Body = RequestDiscussionText.Body(key, "Sample");
        discussion.ThumbsUp.UnionWith(voters);
        return discussion;
    }

    [Fact]
    public async Task Collect_writes_the_requests_file()
    {
        // Arrange
        Request(French, 1, "p1", "p2");

        // Act
        await CollectAsync();

        // Assert
        RegistryRequests.Read(File.ReadAllText(Out)).Requests![French].Votes.Should().Be(2);
    }

    [Fact]
    public async Task Collect_prints_a_line_per_request()
    {
        // Arrange
        var discussion = Request(French, 1, "p1");
        discussion.Comments.Add(new FakeDiscussionComment("C1", "alice", RequestClaimComment.Format(new RequestClaim(French, RequestClaimState.Working, GenerationStage.Translate, 45)))
        {
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });

        // Act
        var result = await CollectAsync();

        // Assert
        result.StdOut.Should().Contain("steam:620/fr: 1 vote, alice working (translate 45%) https://github.com/ailocalpack/registry/discussions/1");
    }

    [Fact]
    public async Task Close_closes_a_done_request()
    {
        // Arrange
        var done = Request(new RequestKey(GameId.Steam(620), "ru"), 1, "p1");

        // Act
        var result = await CollectAsync("--close");

        // Assert
        (result.ExitCode, done.Closed, result.StdOut.Trim().Split('\n')[^1]).Should().Be((0, true, $"Wrote {Out}: 0 open requests, 1 Discussion closed."));
    }

    [Fact]
    public async Task Collected_request_goes_into_the_built_index()
    {
        // Arrange
        Request(French, 1, "p1", "p2");
        await CollectAsync();
        var index = _dir.Combine("index.json");

        // Act
        var built = await RunAsync(false, "index", "build", Registry, "--out", index, "--requests", Out, "--generated", "2026-11-04T12:00:00Z");

        // Assert
        built.ExitCode.Should().Be(0, built.StdErr);
        RegistryIndexSerializer.Read(File.ReadAllText(index)).Index!.RequestFor(GameId.Steam(620), "fr")!.Votes.Should().Be(2);
    }

    [Fact]
    public async Task Broken_requests_file_stops_the_build()
    {
        // Arrange
        File.WriteAllText(Out, """{"schemaVersion":1,"requests":{"nope":{}}}""");

        // Act
        var built = await RunAsync(false, "index", "build", Registry, "--out", _dir.Combine("index.json"), "--requests", Out);

        // Assert
        (built.ExitCode, built.StdErr.Contains("is not a valid requests file", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Ask_opens_the_request_with_the_players_vote()
    {
        // Arrange
        await RunAsync(false, "github", "login", "--no-browser");

        // Act
        var result = await RunAsync(false, "request", "steam:620/fr", "--ask", "--name", "Sample", "--note", "Menus first");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        _github.Api.DiscussionsOf("ailocalpack/registry").Single().Should().BeEquivalentTo(new { Category = "Requests", Title = "Sample → Français · steam:620/fr" });
    }

    [Fact]
    public async Task Ask_prints_the_vote()
    {
        // Arrange
        await RunAsync(false, "github", "login", "--no-browser");

        // Act
        var result = await RunAsync(false, "request", "steam:620/fr", "--ask", "--name", "Sample");

        // Assert
        result.StdOut.Split('\n')[1].Trim().Should().StartWith("1 vote, one of them yours");
    }

    [Fact]
    public async Task Withdraw_takes_the_vote_back()
    {
        // Arrange
        await RunAsync(false, "github", "login", "--no-browser");
        var discussion = Request(French, 1);
        await RunAsync(false, "request", "steam:620/fr", "--ask", "--discussion", discussion.Url.AbsoluteUri);

        // Act
        var result = await RunAsync(false, "request", "steam:620/fr", "--withdraw", "--discussion", discussion.Url.AbsoluteUri);

        // Assert
        (result.ExitCode, discussion.ThumbsUp.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task Request_without_sign_in_fails()
    {
        // Act
        var result = await RunAsync(false, "request", "steam:620/fr", "--ask");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "Not signed in: run 'ailocalpack github login' first."));
    }

    [Fact]
    public async Task Bad_key_is_refused()
    {
        // Act
        var result = await RunAsync(false, "request", "steam:620", "--ask");

        // Assert
        result.ExitCode.Should().Be(1);
    }
}
