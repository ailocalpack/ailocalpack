using System.Security.Cryptography;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Cli.Tests;

public sealed class PackValidateTests : IDisposable
{
    private const string PackPath = "games/steam-620/ru/full";
    private const string StringsHash = "5140d3f499f0bdfc7fbd01c224750f7effa27c7cf720911a99036572b6f3ef9b";

    private static readonly string Registry = Path.Combine(AppContext.BaseDirectory, "fixtures", "registry");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>A copy of the sample registry's valid <c>steam:620/ru/full</c> pack at <paramref name="at"/>.</summary>
    private string CopyPack(string at = PackPath)
    {
        _dir.CopyFolder(Path.Combine(Registry, PackPath), at);
        return _dir.Combine(at);
    }

    private void EditManifest(string from, string to)
    {
        var path = _dir.Combine($"{PackPath}/manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(from, to, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Valid_pack_passes()
    {
        // Arrange
        var folder = CopyPack();

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder);

        // Assert
        (result.ExitCode, result.StdOut).Should().Match<(int, string)>(r =>
            r.Item1 == 0 && r.Item2.Contains("valid (steam:620/ru/full rev 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sample_registry_packs_all_pass()
    {
        // Arrange
        var folders = Directory.GetDirectories(Path.Combine(Registry, "games"), "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")));

        // Act
        var result = await CliRunner.RunAsync(["pack", "validate", .. folders]);

        // Assert
        (result.ExitCode, result.StdOut.Split('\n').Count(l => l.Contains(": valid (", StringComparison.Ordinal))).Should().Be((0, 4));
    }

    [Fact]
    public async Task Pack_for_an_opted_out_game_fails()
    {
        // Arrange
        var folder = CopyPack();
        _dir.File("policy/opt-out.json", """{"schemaVersion": 1, "games": [{"game": "steam:620"}]}""");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder, "--github");

        // Assert
        (result.ExitCode, result.StdOut).Should().Match<(int, string)>(r =>
            r.Item1 == 1 && r.Item2.Contains("title=game::steam:620 is on the registry's opt-out list", StringComparison.Ordinal));
    }

    public static TheoryData<string, string> InvalidPacks => new()
    {
        { "bad-manifest", "manifest.json: authors[1].github: 'bob_2' is not a GitHub login." },
        { "wrong-folder", "The pack folder must be games/steam-620/ru/full/" },
        { "wrong-hash", "manifest.json: strings.sha256: doesn't match strings/strings.json.gz" },
        { "wrong-size", "manifest.json: strings.size: is 143, but strings/strings.json.gz is 142 bytes." },
        { "missing-manifest", "manifest.json: is missing." },
        { "missing-strings", "strings/strings.json.gz: is missing" },
        { "extra-file", "README.md: is not part of the pack" },
        { "disguised-exe", "strings/extra.json: is a Windows executable (PE)." },
        { "script", "hook.sh: has an executable, script or archive extension (.sh)." },
    };

    private string MakeInvalid(string kind)
    {
        switch (kind)
        {
            case "wrong-folder":
                return CopyPack("games/steam-620/ru/other");
            case "bad-manifest":
                CopyPack();
                EditManifest("\"github\": \"bob-2\"", "\"github\": \"bob_2\"");
                break;
            case "wrong-hash":
                CopyPack();
                EditManifest(StringsHash, new string('0', 64));
                break;
            case "wrong-size":
                CopyPack();
                EditManifest("\"size\": 142", "\"size\": 143");
                break;
            case "missing-manifest":
                File.Delete(Path.Combine(CopyPack(), "manifest.json"));
                break;
            case "missing-strings":
                File.Delete(Path.Combine(CopyPack(), "strings", "strings.json.gz"));
                break;
            case "extra-file":
                CopyPack();
                _dir.File($"{PackPath}/README.md", "notes");
                break;
            case "disguised-exe":
                CopyPack();
                _dir.File($"{PackPath}/strings/extra.json", [.. "MZ"u8, 0x90, 0x00, 0x03]);
                break;
            case "script":
                CopyPack();
                _dir.File($"{PackPath}/hook.sh", "echo hi");
                break;
        }

        return _dir.Combine(PackPath);
    }

    [Theory]
    [MemberData(nameof(InvalidPacks))]
    public async Task Invalid_pack_fails_with_its_problem(string kind, string expected)
    {
        // Arrange
        var folder = MakeInvalid(kind);

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder);

        // Assert
        (result.ExitCode, result.StdOut).Should().Match<(int, string)>(r =>
            r.Item1 == 1 && r.Item2.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_invalid_pack_fails_the_run()
    {
        // Arrange
        var valid = Path.Combine(Registry, "games", "steam-620", "de", "komplett");
        var invalid = MakeInvalid("disguised-exe");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", valid, invalid);

        // Assert
        result.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task Github_option_prints_annotations()
    {
        // Arrange
        var folder = MakeInvalid("disguised-exe");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder, "--github");

        // Assert
        result.StdOut.Split('\n').Should().Contain(l =>
            l.StartsWith("::error file=", StringComparison.Ordinal)
            && l.Contains("strings/extra.json::is a Windows executable (PE).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Github_annotation_names_the_field()
    {
        // Arrange
        var folder = MakeInvalid("bad-manifest");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder, "--github");

        // Assert
        result.StdOut.Should().Contain("manifest.json,title=authors[1].github::'bob_2' is not a GitHub login.");
    }

    /// <summary>The copied pack turned into a Ren'Py pack whose string table holds <paramref name="translation"/>.</summary>
    private string CopyRenPyPack(string translation)
    {
        var folder = CopyPack();
        var tablePath = Path.Combine(folder, "strings", "strings.json.gz");
        using (var stream = File.Create(tablePath))
        {
            StringTableSerializer.Write(new StringTable("ru", new Dictionary<string, string> { ["say:start_0123abcd"] = translation }), stream);
        }

        var bytes = File.ReadAllBytes(tablePath);
        EditManifest("\"tool\": \"rpgmaker\"", "\"tool\": \"renpy\"");
        EditManifest(StringsHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        EditManifest("\"size\": 142", $"\"size\": {bytes.Length}");
        return folder;
    }

    [Fact]
    public async Task Unbalanced_tags_of_the_recipe_engine_are_warnings_that_keep_the_pack_valid()
    {
        // Arrange
        var folder = CopyRenPyPack("{/b}Текст");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder);

        // Assert
        (result.ExitCode, result.StdOut.Contains(
            "  warning: strings/strings.json.gz: say:start_0123abcd: tag-unbalanced: Tag {/b} closes nothing: no tag it closes is open before it.",
            StringComparison.Ordinal)).Should().Be((0, true));
    }

    [Fact]
    public async Task Github_option_prints_string_warnings_as_warning_annotations()
    {
        // Arrange
        var folder = CopyRenPyPack("{/b}Текст");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder, "--github");

        // Assert
        result.StdOut.Split('\n').Should().Contain(l =>
            l.StartsWith("::warning file=", StringComparison.Ordinal)
            && l.Contains("strings/strings.json.gz,title=tag-unbalanced::say:start_0123abcd: Tag {/b} closes nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Balanced_tags_give_no_warnings()
    {
        // Arrange
        var folder = CopyRenPyPack("{b}Текст{/b}");

        // Act
        var result = await CliRunner.RunAsync("pack", "validate", folder);

        // Assert
        result.StdOut.Should().NotContain("warning");
    }

    [Fact]
    public async Task Missing_folder_fails()
    {
        // Act
        var result = await CliRunner.RunAsync("pack", "validate", _dir.Combine("nothing"));

        // Assert
        (result.ExitCode, result.StdOut).Should().Match<(int, string)>(r =>
            r.Item1 == 1 && r.Item2.Contains("The pack folder doesn't exist.", StringComparison.Ordinal));
    }
}
