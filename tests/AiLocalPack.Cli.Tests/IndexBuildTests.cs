namespace AiLocalPack.Cli.Tests;

public sealed class IndexBuildTests : IDisposable
{
    private const string Generated = "2026-11-04T12:00:00Z";

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static readonly string Registry = Path.Combine(Fixtures, "registry");
    private static readonly string Ratings = Path.Combine(Fixtures, "registry-ratings.json");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Out => _dir.Combine("out/index.json");

    private string CopyRegistry()
    {
        _dir.CopyFolder(Registry, "registry");
        return _dir.Combine("registry");
    }

    [Fact]
    public async Task Sample_registry_builds_the_golden_index()
    {
        // Act
        await CliRunner.RunAsync("index", "build", Registry, "--out", Out, "--ratings", Ratings, "--generated", Generated);

        // Assert
        File.ReadAllText(Out).Should().Be(File.ReadAllText(Path.Combine(Registry, "index.json")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Build_reports_what_it_wrote()
    {
        // Act
        var result = await CliRunner.RunAsync("index", "build", Registry, "--out", Out, "--ratings", Ratings, "--generated", Generated);

        // Assert
        (result.ExitCode, result.StdOut.Trim(), result.StdErr).Should().Be((0, $"Wrote {Out}: 4 games, 4 packs.", ""));
    }

    [Fact]
    public async Task Invalid_pack_is_left_out_with_a_warning()
    {
        // Arrange
        var root = CopyRegistry();
        _dir.File("registry/games/steam-620/ru/menus/strings/extra.json", [0x7F, (byte)'E', (byte)'L', (byte)'F']);

        // Act
        var result = await CliRunner.RunAsync("index", "build", root, "--out", Out, "--generated", Generated);

        // Assert
        (result.ExitCode, result.StdOut.Contains("3 packs", StringComparison.Ordinal),
            result.StdErr.Contains("warning: games/steam-620/ru/menus/strings/extra.json: is a Linux executable (ELF)", StringComparison.Ordinal))
            .Should().Be((0, true, true));
    }

    [Fact]
    public async Task Github_option_prints_warnings_as_annotations()
    {
        // Arrange
        var root = CopyRegistry();
        _dir.File("registry/games/steam-620/notes.txt", "x");

        // Act
        var result = await CliRunner.RunAsync("index", "build", root, "--out", Out, "--generated", Generated, "--github");

        // Assert
        result.StdErr.Should().StartWith("::warning file=").And.Contain("games/steam-620/notes.txt::");
    }

    [Fact]
    public async Task Broken_policy_fails_and_writes_nothing()
    {
        // Arrange
        var root = CopyRegistry();
        _dir.File("registry/policy/opt-out.json", "{");

        // Act
        var result = await CliRunner.RunAsync("index", "build", root, "--out", Out);

        // Assert
        (result.ExitCode, File.Exists(Out), result.StdErr.Contains("policy/opt-out.json", StringComparison.Ordinal)).Should().Be((1, false, true));
    }

    [Fact]
    public async Task Broken_ratings_fail()
    {
        // Arrange
        _dir.File("ratings.json", """{"schemaVersion": 1, "packs": {"nope": {}}}""");

        // Act
        var result = await CliRunner.RunAsync("index", "build", Registry, "--out", Out, "--ratings", _dir.Combine("ratings.json"));

        // Assert
        (result.ExitCode, result.StdErr.Contains("'nope' is not a pack", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Bad_generated_time_fails()
    {
        // Act
        var result = await CliRunner.RunAsync("index", "build", Registry, "--out", Out, "--generated", "yesterday");

        // Assert
        (result.ExitCode, result.StdErr.Contains("'yesterday' is not a date", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Missing_registry_fails()
    {
        // Act
        var result = await CliRunner.RunAsync("index", "build", _dir.Combine("nothing"), "--out", Out);

        // Assert
        (result.ExitCode, result.StdErr.Contains("The registry folder doesn't exist.", StringComparison.Ordinal)).Should().Be((1, true));
    }
}
