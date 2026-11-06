namespace AiLocalPack.Cli.Tests;

/// <summary><c>dev apply-files</c> / <c>dev uninstall</c> (task 27) against a fake Steam game.</summary>
public sealed class DevApplyFilesTests : IDisposable
{
    private const string GameFolder = "Steam/steamapps/common/Portal 2";

    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public DevApplyFilesTests()
    {
        _steam = new SteamFixture(_dir);
        _steam.AddGame(620, "Portal 2", "100");
        _dir.File($"{GameFolder}/www/data/Map001.json", "map original")
            .File($"{GameFolder}/www/data/System.json", "system original")
            .File("files/www/data/Map001.json", "map ru")
            .File("files/www/fonts/ru.json", "font ru");
    }

    public void Dispose() => _dir.Dispose();

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
                ["AILOCALPACK_INDEX_URL"] = _dir.Combine("no-registry"),
            },
            args);

    private string Read(string path) => File.ReadAllText(_dir.Combine($"{GameFolder}/{path}"));

    [Fact]
    public async Task Apply_files_replaces_and_adds_the_files()
    {
        // Arrange
        var files = _dir.Combine("files");

        // Act
        var result = await RunAsync("dev", "apply-files", "steam:620", files);

        // Assert
        new { result.ExitCode, Map = Read("www/data/Map001.json"), Font = Read("www/fonts/ru.json"), Said = result.StdOut.Contains("1 file replaced, 1 added", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { ExitCode = 0, Map = "map ru", Font = "font ru", Said = true });
    }

    [Fact]
    public async Task Uninstall_brings_the_game_folder_back()
    {
        // Arrange
        (await RunAsync("dev", "apply-files", "steam:620", _dir.Combine("files"))).ExitCode.Should().Be(0);

        // Act
        var result = await RunAsync("dev", "uninstall", "steam:620");

        // Assert
        new
        {
            result.ExitCode,
            Map = Read("www/data/Map001.json"),
            FontGone = !Directory.Exists(_dir.Combine($"{GameFolder}/www/fonts")),
            Said = result.StdOut.Contains("1 file restored, 1 removed", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(new { ExitCode = 0, Map = "map original", FontGone = true, Said = true });
    }

    [Fact]
    public async Task Apply_files_twice_is_refused()
    {
        // Arrange
        (await RunAsync("dev", "apply-files", "steam:620", _dir.Combine("files"))).ExitCode.Should().Be(0);

        // Act
        var result = await RunAsync("dev", "apply-files", "steam:620", _dir.Combine("files"));

        // Assert
        new { result.ExitCode, Said = result.StdErr.Contains("already installed", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { ExitCode = 1, Said = true });
    }

    [Fact]
    public async Task Uninstall_without_a_pack_exits_1()
    {
        // Arrange
        var game = "steam:620";

        // Act
        var result = await RunAsync("dev", "uninstall", game);

        // Assert
        new { result.ExitCode, Said = result.StdErr.Contains("no pack is installed", StringComparison.Ordinal) }
            .Should().BeEquivalentTo(new { ExitCode = 1, Said = true });
    }

    [Fact]
    public async Task Game_not_in_the_catalog_exits_1()
    {
        // Arrange
        var files = _dir.Combine("files");

        // Act
        var result = await RunAsync("dev", "apply-files", "steam:999", files);

        // Assert
        result.ExitCode.Should().Be(1);
    }
}
