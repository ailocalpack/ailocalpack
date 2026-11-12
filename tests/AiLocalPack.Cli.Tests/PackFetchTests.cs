using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>pack fetch</c> (task 26) against the sample registry.</summary>
public sealed class PackFetchTests : IDisposable
{
    /// <summary>Nothing listens on port 1: the connection is refused at once, as when offline.</summary>
    private const string UnreachableUrl = "https://127.0.0.1:1/index.json";

    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private Task<CliResult> RunAsync(params string[] args) => RunAtAsync(Registry, args);

    private Task<CliResult> RunAtAsync(string indexUrl, params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_INDEX_URL"] = indexUrl,
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
            },
            ["pack", "fetch", .. args]);

    /// <summary>A copy of the sample index saved by an earlier run against <paramref name="source"/>.</summary>
    private void SaveIndexCopy(string source)
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

    [Fact]
    public async Task Fetch_puts_the_string_table_into_the_cache()
    {
        // Act
        var result = await RunAsync("steam:620/ru/full");

        // Assert
        (result.ExitCode, Directory.EnumerateFiles(_dir.Combine("home/cache/packs/steam-620/ru/full"), "strings.json.gz", SearchOption.AllDirectories).Count())
            .Should().Be((0, 1));
    }

    [Fact]
    public async Task Pack_id_with_game_and_language_options_is_found()
    {
        // Act
        var result = await RunAsync("full", "--game", "steam:620", "--lang", "ru");

        // Assert
        (result.ExitCode, result.StdOut).Should().Match<(int, string)>(r => r.Item1 == 0 && r.Item2.StartsWith("steam:620/ru/full rev 2: downloaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ambiguous_pack_id_lists_the_candidates()
    {
        // Act
        var result = await RunAsync("full");

        // Assert
        (result.ExitCode, result.StdErr.Contains("  steam:620/ru/full", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Unknown_pack_fails()
    {
        // Act
        var result = await RunAsync("nope");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "The registry has no pack 'nope'."));
    }

    [Fact]
    public async Task Json_names_the_cached_files()
    {
        // Act
        var result = await RunAsync("steam:620/ru/menus", "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        (json["problem"]?.GetValue<string>(), File.Exists(json["strings"]!.GetValue<string>()), File.Exists(json["manifest"]!.GetValue<string>()))
            .Should().Be((null, true, true));
    }

    [Fact]
    public async Task Offline_fetch_without_a_cached_pack_fails()
    {
        // Arrange
        SaveIndexCopy(UnreachableUrl);

        // Act
        var result = await RunAtAsync(UnreachableUrl, "steam:620/ru/full");

        // Assert
        (result.ExitCode, result.StdErr.Contains("isn't cached", StringComparison.Ordinal)).Should().Be((1, true));
    }
}
