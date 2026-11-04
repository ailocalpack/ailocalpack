using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>The banner after a damaged saved game list was rebuilt from the scan (design §9).</summary>
public sealed class LibraryCatalogDamagedTests
{
    private static ServiceProvider WithDamagedCatalog()
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        File.WriteAllText(services.GetRequiredService<AppPaths>().GamesFile, "{ not json");
        services.GetRequiredService<GameCatalog>().Load();
        return services;
    }

    private static LibraryViewModel Create(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

    [Fact]
    public void Banner_shows_once_the_scan_found_the_games_again()
    {
        // Arrange
        using var services = WithDamagedCatalog();
        var vm = Create(services);

        // Act
        services.GetRequiredService<GameLibrary>().Refresh();

        // Assert
        vm.ShowCatalogDamaged.Should().BeTrue();
        vm.CatalogDamagedText.Should().MatchRegex(@"^The saved game list was damaged\. .* The old file is kept as games\.json\.\d{8}-\d{6}\.bak\.$");
    }

    [Fact]
    public void Dismissed_banner_stays_hidden()
    {
        // Arrange
        using var services = WithDamagedCatalog();
        var vm = Create(services);
        services.GetRequiredService<GameLibrary>().Refresh();

        // Act
        vm.DismissCatalogDamagedCommand.Execute(null);

        // Assert
        vm.ShowCatalogDamaged.Should().BeFalse();
    }

    [Fact]
    public void No_banner_for_a_readable_catalog()
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<GameCatalog>().Load();

        // Act
        var vm = Create(services);

        // Assert
        vm.ShowCatalogDamaged.Should().BeFalse();
    }
}
