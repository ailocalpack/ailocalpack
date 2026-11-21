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

    /// <summary>
    /// Task 87: the fake registry's pack <c>full</c> rev 1 made on Windows (its string table isn't in the registry, so
    /// nothing is carried over) and a local pack <c>mac</c> of the same build made on macOS.
    /// </summary>
    private void WindowsPackAndMacLocal()
    {
        var local = new LocalPackStore(AppPaths.UnderHome(Home));
        var pack = local.Find(GameId.Steam(620), "ru", "claude-code")!;
        var mac = pack.Manifest with
        {
            Id = "mac",
            Builds = [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('a', 64) }, OsKind.MacOS)],
        };
        local.Save(mac, new StringTable("ru", new Dictionary<string, string> { ["a"] = "Зелье" }), pack.Report);
        var existing = pack.Manifest with
        {
            Id = "full",
            Title = "Full translation",
            Authors = [new PackAuthor("alice")],
            Revisions = [new PackRevision(1, T0.AddDays(-5), "alice")],
            Builds = [new PackBuild("1000", new Dictionary<string, string> { ["data/Items.json"] = new('c', 64) }, OsKind.Windows)],
        };
        _github.Api.Commit("ailocalpack/registry", "master", new Dictionary<string, string?>
        {
            ["games/steam-620/ru/full/manifest.json"] = PackManifestSerializer.Write(existing),
        });
    }

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

    [Fact]
    public async Task A_revision_taking_a_platform_away_is_refused()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");
        WindowsPackAndMacLocal();

        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/mac", "--revision-of", "full");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("rev 1 supports Windows; this revision would support only macOS: Windows players would lose a compatible translation.")
            .And.Contain("--drop-platforms publishes the revision anyway");
    }

    [Fact]
    public async Task A_revision_taking_a_platform_away_is_sent_with_drop_platforms()
    {
        // Arrange
        await RunAsync("github", "login", "--no-browser");
        WindowsPackAndMacLocal();

        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/mac", "--revision-of", "full", "--drop-platforms");

        // Assert
        result.ExitCode.Should().Be(0, result.StdErr);
        result.StdOut.Should().Contain("Opened PR #1 for full rev 2");
    }

    [Fact]
    public async Task Drop_platforms_needs_a_revision()
    {
        // Act
        var result = await RunAsync("pack", "publish", "steam:620/ru/claude-code", "--new", "--drop-platforms");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("--drop-platforms goes with --revision-of <id>.");
    }
}
