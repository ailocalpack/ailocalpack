using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

public sealed class IndexShowTests : IDisposable
{
    /// <summary>Nothing listens on port 1: the connection is refused at once, as when offline.</summary>
    private const string UnreachableUrl = "https://127.0.0.1:1/index.json";

    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private Task<CliResult> RunAsync(string indexUrl, params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_INDEX_URL"] = indexUrl,
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
            },
            ["index", "show", .. args]);

    /// <summary>A copy of the sample index saved by an earlier run against <paramref name="source"/>.</summary>
    private void SaveCopy(string source)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Registry, "index.json"));
        var meta = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["source"] = source,
            ["fetchedAt"] = "2026-11-01T10:00:00Z",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        _dir.File("home/cache/registry/index.json", bytes);
        _dir.File("home/cache/registry/index.meta.json", meta.ToJsonString());
    }

    private static IEnumerable<string> PackLines(CliResult result) =>
        result.StdOut.Split('\n').Where(l => l.StartsWith("steam:", StringComparison.Ordinal) || l.StartsWith("fp:", StringComparison.Ordinal));

    [Fact]
    public async Task Local_registry_lists_its_packs()
    {
        // Act
        var result = await RunAsync(Registry);

        // Assert
        (result.ExitCode, PackLines(result).Count()).Should().Be((0, 4));
    }

    [Fact]
    public async Task Local_registry_shows_a_pack_row()
    {
        // Act
        var result = await RunAsync(Registry);

        // Assert
        PackLines(result).Should().Contain(l =>
            l.Contains("ru", StringComparison.Ordinal) && l.Contains("full", StringComparison.Ordinal)
            && l.Contains("verified", StringComparison.Ordinal) && l.Contains("1234567,1250000", StringComparison.Ordinal)
            && l.EndsWith("Полный перевод", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_registry_lists_the_game_flags()
    {
        // Act
        var result = await RunAsync(Registry);

        // Assert
        result.StdOut.Should().Contain("steam:400: opt-out").And.Contain("steam:570: anti-cheat allowed");
    }

    [Fact]
    public async Task Game_and_language_filter_the_packs()
    {
        // Act
        var result = await RunAsync(Registry, "--game", "steam:620", "--lang", "RU", "--json");

        // Assert
        var games = JsonNode.Parse(result.StdOut)!["games"]!.AsArray();
        games.Select(g => (string)g!["id"]!).Should().Equal("steam:620");
        games[0]!["packs"]!.AsArray().Select(p => (string)p!["id"]!).Should().Equal("full", "menus");
    }

    [Fact]
    public async Task Json_resolves_the_manifest_inside_the_registry()
    {
        // Act
        var result = await RunAsync(Registry, "--game", "steam:620", "--lang", "de", "--json");

        // Assert
        var manifest = (string)JsonNode.Parse(result.StdOut)!["games"]![0]!["packs"]![0]!["manifest"]!;
        File.Exists(new Uri(manifest).LocalPath).Should().BeTrue(manifest);
    }

    [Fact]
    public async Task Language_without_packs_says_so()
    {
        // Act
        var result = await RunAsync(Registry, "--lang", "ja");

        // Assert
        result.StdOut.Should().Contain("No packs.");
    }

    [Fact]
    public async Task Unknown_language_is_an_error()
    {
        // Act
        var result = await RunAsync(Registry, "--lang", "klingon");

        // Assert
        (result.ExitCode, result.StdErr).Should().Match<(int, string)>(r => r.Item1 == 1 && r.Item2.Contains("'klingon' is not a supported language"));
    }

    [Fact]
    public async Task Bad_game_id_is_an_error()
    {
        // Act
        var result = await RunAsync(Registry, "--game", "620");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "'620' is not a game ID (steam:<app id> or fp:<64 lowercase hex>)."));
    }

    [Fact]
    public async Task Folder_without_an_index_is_a_clear_error()
    {
        // Arrange
        var folder = _dir.Dir("empty").Combine("empty");

        // Act
        var result = await RunAsync(folder);

        // Assert
        (result.ExitCode, result.StdErr.Trim())
            .Should().Be((1, $"Can't read the registry: There is no index.json at {Path.Combine(folder, "index.json")}."));
    }

    [Fact]
    public async Task Plain_http_override_is_an_error()
    {
        // Act
        var result = await RunAsync("http://example.com/index.json");

        // Assert
        (result.ExitCode, result.StdErr.Trim())
            .Should().Be((1, "AILOCALPACK_INDEX_URL: 'http://example.com/index.json' must be an https:// URL or a local folder."));
    }

    [Fact]
    public async Task Offline_without_a_saved_copy_is_a_clear_error()
    {
        // Act
        var result = await RunAsync(UnreachableUrl);

        // Assert
        (result.ExitCode, result.StdErr).Should().Match<(int, string)>(r =>
            r.Item1 == 1 && r.Item2.Contains($"Can't read the registry at {UnreachableUrl}") && r.Item2.Contains("there is no saved copy yet."));
    }

    [Fact]
    public async Task Offline_shows_the_saved_copy_with_its_date()
    {
        // Arrange
        SaveCopy(UnreachableUrl);

        // Act
        var result = await RunAsync(UnreachableUrl, "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        ((string)json["state"]!, (string)json["problem"]!, (string)json["fetchedAt"]!, json["games"]!.AsArray().Count)
            .Should().Be(("stale", "offline", "2026-11-01T10:00:00+00:00", 4));
    }

    [Fact]
    public async Task Offline_table_says_the_list_is_saved()
    {
        // Arrange
        SaveCopy(UnreachableUrl);

        // Act
        var result = await RunAsync(UnreachableUrl);

        // Assert
        (result.ExitCode, result.StdOut.Contains("showing the saved copy from", StringComparison.Ordinal), PackLines(result).Count())
            .Should().Be((0, true, 4));
    }
}
