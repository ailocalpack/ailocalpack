using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.AddGame;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>Library side of adding and removing manual games: + Folder, a dropped folder, "Remove from library".</summary>
public sealed class LibraryAddFolderTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();
    private readonly TempDir _games = new();

    public void Dispose()
    {
        _services.Dispose();
        _games.Dispose();
    }

    private LibraryViewModel Library() => _services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

    private IDialogService Dialogs => _services.GetRequiredService<IDialogService>();

    private GameCatalog Catalog => _services.GetRequiredService<GameCatalog>();

    [Fact]
    public void Add_folder_opens_the_dialog_on_step_one()
    {
        // Arrange
        var library = Library();

        // Act
        library.AddFolderCommand.Execute(null);

        // Assert
        Dialogs.Current.Should().BeOfType<AddGameDialogViewModel>().Which.Step.Should().Be(AddGameStep.Pick);
    }

    [Fact]
    public async Task Dropped_folder_opens_the_dialog_on_its_summary()
    {
        // Arrange
        var library = Library();
        _games.RenPy("Wren");

        // Act
        await library.AddDroppedFolderAsync(_games.Combine("Wren"));

        // Assert
        Dialogs.Current.Should().BeOfType<AddGameDialogViewModel>().Which.Step.Should().Be(AddGameStep.Summary);
    }

    [Fact]
    public void Added_game_shows_up_as_a_manual_card()
    {
        // Arrange
        var library = Library();

        // Act
        Catalog.AddManual(TestGames.Manual("Wren"));

        // Assert
        library.VisibleCards.Should().ContainSingle().Which.IsManual.Should().BeTrue();
    }

    [Fact]
    public void Remove_takes_a_manual_game_and_its_cover_out_of_the_library()
    {
        // Arrange
        var game = TestGames.Manual("Wren");
        Catalog.AddManual(game);
        var covers = _services.GetRequiredService<ManualCoverStore>();
        covers.Save(game.Id, [1], ".png", isIcon: true);
        var library = Library();

        // Act
        library.VisibleCards.Single().RemoveCommand.Execute(null);

        // Assert
        Catalog.Games.Should().BeEmpty();
        library.VisibleCards.Should().BeEmpty();
        covers.Find(game.Id).Should().BeNull();
    }
}
