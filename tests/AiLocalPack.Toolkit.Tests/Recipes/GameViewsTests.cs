using System.Text;
using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Tests.Recipes;

/// <summary>How generation sees a game folder: without the pack installed there.</summary>
public sealed class GameViewsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public GameViewsTests()
    {
        _dir.File("game/data/System.json", "translated")
            .File("game/data/tl.json", "added")
            .File("backup/data/System.json", "original");
    }

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    [Fact]
    public void A_game_without_an_installed_pack_is_read_as_it_is()
    {
        // Act
        var view = GameViews.Of(Game, InstalledOriginals.None);

        // Assert
        Encoding.UTF8.GetString(view.ReadAllBytes("data/System.json")).Should().Be("translated");
    }

    [Fact]
    public void A_game_with_an_installed_pack_is_read_with_the_packs_originals()
    {
        // Arrange
        var originals = new InstalledOriginals(
            new Dictionary<string, string> { ["data/System.json"] = _dir.Combine("backup/data/System.json") },
            new HashSet<string> { "data/tl.json" });

        // Act
        var view = GameViews.Of(Game, originals);

        // Assert
        new { Text = Encoding.UTF8.GetString(view.ReadAllBytes("data/System.json")), Added = view.FileExists("data/tl.json") }
            .Should().Be(new { Text = "original", Added = false });
    }
}
