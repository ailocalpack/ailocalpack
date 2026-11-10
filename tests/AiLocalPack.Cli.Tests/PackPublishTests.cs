using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>pack publish</c> (task 60) against a local fake GitHub and the sample registry index.</summary>
public sealed class PackPublishTests : IDisposable
{
    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeGitHubServer _github = new();

    public PackPublishTests()
    {
        _github.Api.AddRepo("ailocalpack/registry");
        var manifest = new PackManifest(
            "claude-code",
            GameId.Steam(620),
            "ru",
            "Claude Code translation",
            [new PackAuthor(LocalPackStore.PlaceholderAuthor)],
            [new PackRevision(1, T0, LocalPackStore.PlaceholderAuthor)],
            PackStatus.Alpha,
            new PackAgent("claude-code"),
            [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) })],
            new PackRecipe("rpgmaker", "1.0.0"),
            null,
            null,
            [],
            [],
            new PackCoverage(1, 1),
            T0,
            T0);
        var report = new LocalPackReport("job-1", T0, TestRunResult.Skipped(TestRunSkip.OtherPlatform, "Windows program"), LocalPackFonts.NotChecked, [], [], 0, Tried: T0);
        new LocalPackStore(AppPaths.UnderHome(Home)).Save(manifest, new StringTable("ru", new Dictionary<string, string> { ["a"] = "Зелье" }), report);
    }

    public void Dispose()
    {
        _github.Dispose();
        _dir.Dispose();
    }

    private string Home => _dir.Combine("home");

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_HOME"] = Home,
                ["AILOCALPACK_SECRETS_FILE"] = _dir.Combine("secrets.json"),
                ["AILOCALPACK_GITHUB_URL"] = _github.Url,
                ["AILOCALPACK_INDEX_URL"] = Registry,
            },
            args);

    [Fact]
    public async Task Publish_opens_a_pull_request()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");

        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/claude-code", "--new");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().Contain("Opened PR #1 for claude-code rev 1: https://github.com/ailocalpack/registry/pull/1");
    }

    [Fact]
    public async Task Publish_takes_the_folder_pack_local_prints()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");
        var folder = Path.Combine(Home, "data", "local-packs", "games", "steam-620", "ru", "claude-code");

        // Act
        var result = await RunAsync("pack", "publish", folder, "--new", "--registry", "ailocalpack/registry");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        _github.Api.PullRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_with_similar_packs_asks_to_choose()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");

        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/claude-code");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("choose --revision-of <id> (its players get the update) or --new").And.Contain("  full: «");
    }

    [Fact]
    public async Task Publish_without_signing_in_says_so()
    {
        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/claude-code", "--new");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("Sign in to GitHub first");
    }

    [Fact]
    public async Task Publish_of_an_unknown_local_pack_says_how_to_list_them()
    {
        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/nothing", "--new");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("No local pack 'steam:620/ru/nothing'; 'ailocalpack pack local' lists them.");
    }
}
