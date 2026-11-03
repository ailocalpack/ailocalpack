using System.Globalization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.Library;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Tests.Library;

public sealed class GameCardViewModelTests
{
    private static GameCardViewModel Card(Game game) =>
        new(game, new Localizer(CultureInfo.GetCultureInfo("en")), AntiCheatPolicy.Bundled, _ => { }, _ => { });

    [Theory]
    [InlineData("unity:il2cpp", "Unity (IL2CPP)")]
    [InlineData("unreal:5", "Unreal Engine 5")]
    [InlineData("unknown", "Unknown engine")]
    [InlineData(null, "")]
    public void Context_line_shows_the_engine(string? engine, string expected)
    {
        // Arrange
        var card = Card(TestGames.Steam(10, "Alpha", engine: engine));

        // Act
        var line = card.ContextLine;

        // Assert
        line.Should().Be(expected);
    }

    [Fact]
    public void Anti_cheat_game_is_blocked_with_its_name()
    {
        // Arrange
        var game = TestGames.Steam(10, "Arena", engine: "unreal:5", antiCheat: "eac");

        // Act
        var card = Card(game);

        // Assert
        card.IsBlocked.Should().BeTrue();
        card.ContextLine.Should().Be("Easy Anti-Cheat");
        card.BadgeText.Should().Be("Anti-cheat");
        card.AccessibleName.Should().Be("Arena (blocked: Easy Anti-Cheat)");
    }

    [Fact]
    public void Game_without_anti_cheat_is_not_blocked()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha", antiCheat: AntiCheatPolicy.NoneId);

        // Act
        var card = Card(game);

        // Assert
        card.IsBlocked.Should().BeFalse();
        card.AccessibleName.Should().Be("Alpha");
    }

    [Fact]
    public void Unavailable_game_is_dimmed_and_cannot_open_its_folder()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha", engine: "renpy", available: false);

        // Act
        var card = Card(game);

        // Assert
        card.IsDimmed.Should().BeTrue();
        card.ContextLine.Should().Be("Folder not available");
        card.OpenFolderCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Manual_game_has_the_manual_marker()
    {
        // Arrange
        var game = TestGames.Manual("Indie", engine: "renpy");

        // Act
        var card = Card(game);

        // Assert
        card.IsManual.Should().BeTrue();
        card.ManualText.Should().Be("manual");
        card.ShowManualSeparator.Should().BeTrue();
    }

    [Fact]
    public void Steam_game_has_no_manual_marker()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha", engine: "renpy");

        // Act
        var card = Card(game);

        // Assert
        card.IsManual.Should().BeFalse();
        card.ShowManualSeparator.Should().BeFalse();
    }

    [Fact]
    public void Placeholder_color_is_the_same_for_the_same_game()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha");

        // Act
        var (first, second) = (Card(game).PlaceholderBrush, Card(game).PlaceholderBrush);

        // Assert
        Colors(first).Should().Equal(Colors(second));
    }

    private static IEnumerable<Color> Colors(IBrush brush) =>
        brush.Should().BeAssignableTo<IGradientBrush>().Which.GradientStops.Select(s => s.Color).ToList();

    [Fact]
    public void Update_with_another_game_is_rejected()
    {
        // Arrange
        var card = Card(TestGames.Steam(10, "Alpha"));

        // Act
        var act = () => card.Update(TestGames.Steam(20, "Beta"));

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Update_raises_the_changed_texts()
    {
        // Arrange
        var game = TestGames.Steam(10, "Alpha");
        var card = Card(game);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        card.Update(game with { Engine = "renpy" });

        // Assert
        changed.Should().Contain(nameof(GameCardViewModel.ContextLine));
        card.ContextLine.Should().Be("Ren'Py");
    }
}
