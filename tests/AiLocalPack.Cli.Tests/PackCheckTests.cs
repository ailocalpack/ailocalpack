using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Cli.Tests;

public sealed class PackCheckTests : IDisposable
{
    private const string Map = "www/data/Map001.json";
    private static readonly DateTimeOffset Date = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly SteamFixture _steam;

    public PackCheckTests() => _steam = new SteamFixture(_dir);

    public void Dispose() => _dir.Dispose();

    private Task<CliResult> RunAsync(params string[] args) =>
        CliRunner.RunAsync(
            new Dictionary<string, string>
            {
                ["AILOCALPACK_STEAM_ROOT"] = _steam.Root,
                ["AILOCALPACK_HOME"] = _dir.Combine("home"),
            },
            args);

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Steam game 620 with build <paramref name="buildId"/> and <see cref="Map"/> holding <paramref name="map"/>.</summary>
    private void InstallGame(string buildId, string map)
    {
        _steam.AddGame(620, "Portal 2", buildId);
        _dir.File($"Steam/steamapps/common/Portal 2/{Map}", map);
    }

    /// <summary>Writes a pack for game 620 made for build 100 (where <see cref="Map"/> held "map v1"); returns its folder.</summary>
    private string WritePack()
    {
        var manifest = new PackManifest(
            "full",
            GameId.Steam(620),
            "ru",
            "Полный перевод",
            [new PackAuthor("alice")],
            [new PackRevision(1, Date, "alice")],
            PackStatus.Verified,
            null,
            [new PackBuild("100", new Dictionary<string, string> { [Map] = Sha256("map v1") })],
            new PackRecipe("rpgmaker", "1.0.0"),
            new PackStringsFile(PackStringsFile.DefaultPath, new string('a', 64), 100),
            null,
            [],
            [],
            new PackCoverage(10, 10),
            Date,
            Date);
        _dir.File("pack/manifest.json", PackManifestSerializer.Write(manifest));
        return _dir.Combine("pack");
    }

    [Fact]
    public async Task A_pack_for_the_installed_build_is_compatible()
    {
        // Arrange
        InstallGame("100", "map v1");
        var pack = WritePack();

        // Act
        var result = await RunAsync("pack", "check", Path.Combine(pack, "manifest.json"), "--game", "steam:620");

        // Assert
        result.Should().BeEquivalentTo(new
        {
            ExitCode = 0,
            StdOut = "compatible: made for build 100, which is your build." + Environment.NewLine + Environment.NewLine
                + "pack:  steam:620/ru/full rev 1 \"Полный перевод\"" + Environment.NewLine
                + "       recipe rpgmaker 1.0.0; 1 build: 100" + Environment.NewLine
                + "game:  steam:620 \"Portal 2\", build 100" + Environment.NewLine
                + $"       {Path.Combine(_steam.Root, "steamapps", "common", "Portal 2")}" + Environment.NewLine,
            StdErr = "",
        });
    }

    [Fact]
    public async Task Changed_files_are_probably_compatible_and_listed()
    {
        // Arrange
        InstallGame("200", "map v2");
        var pack = WritePack();

        // Act
        var result = await RunAsync("pack", "check", pack, "--game", "steam:620");

        // Assert
        result.ExitCode.Should().Be(0);
        result.StdOut.Should().StartWith(
            "probably compatible: built for build 100; you have build 200. 1 of 1 file the pack changes differ; "
            + "they are rebuilt from your copy, and text the update added stays untranslated.")
            .And.EndWith($"changed:{Environment.NewLine}  {Map}{Environment.NewLine}");
    }

    [Fact]
    public async Task A_missing_touched_file_is_incompatible_with_exit_code_2()
    {
        // Arrange
        _steam.AddGame(620, "Portal 2", "200");
        var pack = WritePack();

        // Act
        var result = await RunAsync("pack", "check", pack, "--game", "steam:620");

        // Assert
        result.ExitCode.Should().Be(2);
        result.StdOut.Should().StartWith("incompatible: 1 of 1 file the pack changes is not in the game folder.");
    }

    [Fact]
    public async Task Json_carries_the_verdict_and_its_details()
    {
        // Arrange
        InstallGame("200", "map v2");
        var pack = WritePack();
        var expected = new JsonObject
        {
            ["pack"] = "steam:620/ru/full",
            ["revision"] = 1,
            ["game"] = "steam:620",
            ["verdict"] = "probably-compatible",
            ["reason"] = "files-changed",
            ["explanation"] = "built for build 100; you have build 200. 1 of 1 file the pack changes differ; "
                + "they are rebuilt from your copy, and text the update added stays untranslated.",
            ["packBuilds"] = new JsonArray("100"),
            ["gameBuild"] = "200",
            ["referenceBuild"] = "100",
            ["touchedFiles"] = 1,
            ["changedFiles"] = new JsonArray(Map),
            ["missingFiles"] = new JsonArray(),
            ["unreadableFile"] = null,
        };

        // Act
        var result = await RunAsync("pack", "check", pack, "--game", "steam:620", "--json");

        // Assert
        JsonNode.DeepEquals(JsonNode.Parse(result.StdOut), expected).Should().BeTrue(result.StdOut);
    }

    [Fact]
    public async Task A_game_not_in_the_catalog_fails()
    {
        // Arrange
        var pack = WritePack();

        // Act
        var result = await RunAsync("pack", "check", pack, "--game", "steam:620");

        // Assert
        result.Should().BeEquivalentTo(new
        {
            ExitCode = 1,
            StdOut = "",
            StdErr = "steam:620 is not in the game catalog: install it in Steam or add its folder in the app, then run 'games list'."
                + Environment.NewLine,
        });
    }

    [Fact]
    public async Task An_invalid_manifest_fails_with_its_errors()
    {
        // Arrange
        InstallGame("100", "map v1");
        _dir.File("pack/manifest.json", """{"schemaVersion": 1, "id": "Bad ID"}""");

        // Act
        var result = await RunAsync("pack", "check", _dir.Combine("pack"), "--game", "steam:620");

        // Assert
        result.ExitCode.Should().Be(1);
        result.StdErr.Should().StartWith($"{_dir.Combine("pack/manifest.json")} is not a valid pack manifest:")
            .And.Contain("  id: ");
    }

    [Fact]
    public async Task A_bad_game_id_fails()
    {
        // Arrange
        var pack = WritePack();

        // Act
        var result = await RunAsync("pack", "check", pack, "--game", "620");

        // Assert
        result.Should().BeEquivalentTo(new
        {
            ExitCode = 1,
            StdErr = "'620' is not a game ID (steam:<app id> or fp:<64 lowercase hex>)." + Environment.NewLine,
        });
    }
}
