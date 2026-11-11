using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>storage show / move-backups / clear-cache</c> (task 66) on a throwaway app home.</summary>
public sealed class StorageCommandTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Home => _dir.Combine("home");

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(new Dictionary<string, string> { ["AILOCALPACK_HOME"] = Home }, ["storage", .. args]);

    private void Backup() => _dir.File("home/data/backups/steam-620/20261101-120000-abcdefgh/files/data.json", new byte[100]);

    [Fact]
    public async Task Show_json_lists_backups_working_copies_and_cache()
    {
        // Arrange
        Backup();
        _dir.File("home/data/work/steam-620/20261101-100000-aaaaaa/game/a.bin", new byte[30]);
        _dir.File("home/cache/packs/x/manifest.json", new byte[7]);

        // Act
        var result = await RunAsync("show", "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        (json["backups"]!["bytes"]!.GetValue<long>(), json["backups"]!["games"]!.GetValue<int>(), json["workingCopies"]![0]!["state"]!.GetValue<string>(),
            json["workingCopies"]![0]!["bytes"]!.GetValue<long>(), json["cacheBytes"]!.GetValue<long>(), json["totalBytes"]!.GetValue<long>())
            .Should().Be((100L, 1, "no-job", 30L, 7L, 137L));
    }

    [Fact]
    public async Task Move_backups_moves_them_and_show_names_the_new_folder()
    {
        // Arrange
        Backup();
        var target = Path.Combine(_dir.Combine("elsewhere"), "ailocalpack-backups");
        await RunAsync("move-backups", _dir.Combine("elsewhere"));

        // Act
        var result = await RunAsync("show", "--json");

        // Assert
        (JsonNode.Parse(result.StdOut)!["backups"]!["folder"]!.GetValue<string>(), File.Exists(Path.Combine(target, "steam-620/20261101-120000-abcdefgh/files/data.json")))
            .Should().Be((target, true));
    }

    [Fact]
    public async Task Move_backups_into_a_folder_with_files_fails_and_moves_nothing()
    {
        // Arrange
        Backup();
        _dir.File("elsewhere/ailocalpack-backups/mine.txt", "mine");

        // Act
        var result = await RunAsync("move-backups", _dir.Combine("elsewhere"));

        // Assert
        (result.ExitCode, result.StdErr.Contains("the folder already holds files", StringComparison.Ordinal), File.Exists(_dir.Combine("home/data/backups/steam-620/20261101-120000-abcdefgh/files/data.json")))
            .Should().Be((1, true, true));
    }

    [Fact]
    public async Task Clear_cache_deletes_the_downloads()
    {
        // Arrange
        _dir.File("home/cache/packs/x/manifest.json", new byte[2048]);

        // Act
        var result = await RunAsync("clear-cache");

        // Assert
        (result.ExitCode, result.StdOut.Trim(), Directory.Exists(_dir.Combine("home/cache/packs"))).Should().Be((0, "Cache cleared: 2 KB freed.", false));
    }
}
