using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Mcp.Tests;

/// <summary>The server reads the game folder through its game view (_Live game folder_): an installed pack's files read as the game's own.</summary>
public sealed class GameViewTests : IDisposable
{
    private readonly TempDir _dir = new();

    public GameViewTests()
    {
        _dir.File("game/data/System.json", "{\"gameTitle\":\"Квест\"}")
            .File("backup/data/System.json", "{\"gameTitle\":\"Quest\"}")
            .Dir("job");
    }

    public void Dispose() => _dir.Dispose();

    private ToolkitTools Tools()
    {
        var game = new PristineGameFiles(
            GameFiles.Folder(_dir.Combine("game")),
            new Dictionary<string, string> { ["data/System.json"] = _dir.Combine("backup/data/System.json") },
            []);
        var options = new ToolkitServerOptions(_dir.Combine("game"), _dir.Combine("job"), "ru", RecipeTools.Builtin) { Game = game };
        return new ToolkitTools(new ToolkitSession(options));
    }

    [Fact]
    public void A_file_an_installed_pack_replaced_reads_as_the_games_own()
    {
        // Act
        var snippet = Tools().ReadFileSnippet("data/System.json");

        // Assert
        snippet.Content.Should().Contain("Quest").And.NotContain("Квест");
    }
}
