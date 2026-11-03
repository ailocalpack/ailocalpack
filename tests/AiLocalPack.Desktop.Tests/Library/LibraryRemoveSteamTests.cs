using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

public sealed class LibraryRemoveSteamTests
{
    [Fact]
    public void Steam_games_can_not_be_removed()
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Celeste"));
        var library = services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

        // Act
        var canRemove = library.VisibleCards.Single().RemoveCommand.CanExecute(null);

        // Assert
        canRemove.Should().BeFalse();
    }
}
