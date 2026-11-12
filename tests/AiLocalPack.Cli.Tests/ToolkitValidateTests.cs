using System.Text.Json.Nodes;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>toolkit validate</c> (task 48).</summary>
public sealed class ToolkitValidateTests : IDisposable
{
    private const string Description = "data/Items.json:/2/description";
    private const string MapName = "data/Map001.json:/displayName";

    private readonly TempDir _dir = new();

    public ToolkitValidateTests() => _dir.RpgMakerGame("game", mv: true);

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    /// <summary>A plain JSON string table in Russian with <paramref name="strings"/>.</summary>
    private string Table(params (string Key, string Text)[] strings)
    {
        var entries = new JsonObject();
        foreach (var (key, text) in strings)
        {
            entries[key] = text;
        }

        var path = _dir.Combine("strings.json");
        File.WriteAllText(path, new JsonObject { ["schemaVersion"] = 1, ["language"] = "ru", ["strings"] = entries }.ToJsonString());
        return path;
    }

    [Fact]
    public async Task Bad_translation_is_listed_with_its_rule_and_fails()
    {
        // Arrange
        var table = Table((Description, "Тёплый напиток. Редкость!"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", Game);

        // Assert
        (result.ExitCode, result.StdOut.Contains(
            $"{Description}: error placeholder-missing: Placeholder \\C[0] appears 1 time(s) in the original but 0 in the translation; keep it exactly as written.",
            StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Summary_counts_errors_and_warnings()
    {
        // Arrange
        var table = Table((Description, @"Тёплый напиток. \C[2]Редкость!\C[0]\C[3]"), (MapName, "Village of Ash"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", Game);

        // Assert
        result.StdOut.Should().EndWith("Checked 2 translations: 1 error, 1 warning.\n");
    }

    [Fact]
    public async Task Warnings_alone_pass()
    {
        // Arrange
        var table = Table((MapName, "Village of Ash"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", Game);

        // Assert
        (result.ExitCode, result.StdOut.Contains($"{MapName}: warning untranslated:", StringComparison.Ordinal)).Should().Be((0, true));
    }

    [Fact]
    public async Task Empty_entries_are_untranslated_and_skipped()
    {
        // Arrange
        var table = Table((MapName, ""), (Description, @"Тёплый напиток. \C[2]Редкость!\C[0]"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", Game);

        // Assert
        (result.ExitCode, result.StdOut.Trim()).Should().Be((0, "Checked 1 translations: 0 errors, 0 warnings."));
    }

    [Fact]
    public async Task Keys_not_in_the_game_are_named_on_stderr()
    {
        // Arrange
        var table = Table(("data/Nothing.json:/0", "Ничего"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", Game);

        // Assert
        (result.ExitCode, result.StdErr.Contains("warning: 1 keys are not in this game (not checked):", StringComparison.Ordinal)).Should().Be((0, true));
    }

    [Fact]
    public async Task Json_lists_each_issue()
    {
        // Arrange
        var table = Table((Description, "Тёплый напиток."), ("data/Nothing.json:/0", "Ничего"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", Game, "--json");

        // Assert
        var json = JsonNode.Parse(result.StdOut)!;
        (json["checked"]!.GetValue<int>(), json["errors"]!.GetValue<int>(), json["issues"]![0]!["key"]!.GetValue<string>(),
            json["issues"]![0]!["severity"]!.GetValue<string>(), json["issues"]![0]!["rule"]!.GetValue<string>(), json["unknownKeys"]![0]!.GetValue<string>())
            .Should().Be((1, 2, Description, "error", "placeholder-missing", "data/Nothing.json:/0"));
    }

    [Fact]
    public async Task Invalid_table_fails()
    {
        // Arrange
        var path = _dir.Combine("broken.json");
        File.WriteAllText(path, "{ not json");

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", path, "--game", Game);

        // Assert
        (result.ExitCode, result.StdErr.Contains("is not a valid string table", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Missing_game_folder_fails()
    {
        // Arrange
        var table = Table((MapName, "Деревня"));

        // Act
        var result = await CliRunner.RunAsync("toolkit", "validate", table, "--game", _dir.Combine("nothing"));

        // Assert
        (result.ExitCode, result.StdErr.Contains("Folder not found", StringComparison.Ordinal)).Should().Be((1, true));
    }
}
