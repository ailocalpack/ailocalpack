using System.Security.Cryptography;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>toolkit patch</c> (task 34): a binary patch pack applied to a game folder into another folder.</summary>
public sealed class ToolkitPatchTests : IDisposable
{
    private const string File1 = "data/text.txt";

    private readonly TempDir _dir = new();

    public ToolkitPatchTests()
    {
        _dir.File("game/" + File1, "Hello, lamplighter");
        var original = File.ReadAllBytes(Path.Combine(Game, File1));
        VcdiffFixture.Archive(_dir.Combine("pack/patch.zip"), new Dictionary<string, byte[]> { [File1] = VcdiffFixture.Encode(original, Patched) });
        var date = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var manifest = new PackManifest(
            "patch",
            GameId.Steam(10),
            "ru",
            "Patch",
            [new PackAuthor("alice")],
            [new PackRevision(1, date, "alice")],
            PackStatus.Alpha,
            null,
            [new PackBuild("1000", new Dictionary<string, string> { [File1] = Sha(original) })],
            null,
            null,
            new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/patch.zip"), new string('a', 64), 1, new Dictionary<string, string> { [File1] = Sha(Patched) }),
            [],
            [],
            new PackCoverage(1, 1),
            date,
            date);
        _dir.File("pack/manifest.json", PackManifestSerializer.Write(manifest));
    }

    public void Dispose() => _dir.Dispose();

    private static readonly byte[] Patched = "Привет, фонарщик"u8.ToArray();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Game => _dir.Combine("game");

    private string Out => _dir.Combine("out");

    [Fact]
    public async Task Patched_files_are_written_into_the_output_folder()
    {
        // Act
        var result = await CliRunner.RunAsync("toolkit", "patch", Game, "--pack", _dir.Combine("pack"), "--out", Out);

        // Assert
        (result.ExitCode, File.ReadAllBytes(Path.Combine(Out, File1))).Should().BeEquivalentTo((0, Patched));
    }

    [Fact]
    public async Task The_summary_lists_the_files()
    {
        // Act
        var result = await CliRunner.RunAsync("toolkit", "patch", Game, "--pack", _dir.Combine("pack/manifest.json"), "--out", Out);

        // Assert
        result.StdOut.ReplaceLineEndings("\n").Should().Be($"Patched 1 files into {Out}.\n  {File1}\n");
    }

    [Fact]
    public async Task A_changed_game_file_is_refused_as_incompatible()
    {
        // Arrange
        _dir.File("game/" + File1, "Hello, modded lamplighter");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "patch", Game, "--pack", _dir.Combine("pack"), "--out", Out);

        // Assert
        (result.ExitCode, result.StdErr.Contains("not the file the patch was made from", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task A_missing_archive_is_refused()
    {
        // Arrange
        File.Delete(_dir.Combine("pack/patch.zip"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "patch", Game, "--pack", _dir.Combine("pack"), "--out", Out);

        // Assert
        (result.ExitCode, result.StdErr.Contains("no patch archive", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task The_game_folder_is_refused_as_output()
    {
        // Act
        var result = await CliRunner.RunAsync("toolkit", "patch", Game, "--pack", _dir.Combine("pack"), "--out", Game);

        // Assert
        (result.ExitCode, File.ReadAllText(Path.Combine(Game, File1))).Should().Be((1, "Hello, lamplighter"));
    }
}
