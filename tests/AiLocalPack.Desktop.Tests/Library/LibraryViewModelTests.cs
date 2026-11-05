using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Library;
using AiLocalPack.Desktop.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

public sealed class LibraryViewModelTests
{
    private static LibraryViewModel Create(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

    private static ServiceProvider WithGames(params string[] names) =>
        TestServices.Build(steam: s =>
        {
            for (var i = 0; i < names.Length; i++)
            {
                s.AddGame((uint)(10 + i), names[i]);
            }
        });

    private static IEnumerable<string> Names(LibraryViewModel vm) => vm.VisibleCards.Select(c => c.Name);

    [Fact]
    public void Shows_the_catalog_games_sorted_by_name()
    {
        // Arrange
        using var services = WithGames("beta", "Gamma", "Alpha");

        // Act
        var vm = Create(services);

        // Assert
        Names(vm).Should().Equal("Alpha", "beta", "Gamma");
        vm.ShowGrid.Should().BeTrue();
    }

    [Fact]
    public void Search_ignores_case()
    {
        // Arrange
        using var services = WithGames("Hollow Knight", "Celeste");
        var vm = Create(services);

        // Act
        vm.Query = "HOLLOW";

        // Assert
        Names(vm).Should().Equal("Hollow Knight");
    }

    [Fact]
    public void Search_ignores_diacritics()
    {
        // Arrange
        using var services = WithGames("Pokémon Legends", "Celeste");
        var vm = Create(services);

        // Act
        vm.Query = "pokemon";

        // Assert
        Names(vm).Should().Equal("Pokémon Legends");
    }

    [Fact]
    public void Search_matches_inside_the_name()
    {
        // Arrange
        using var services = WithGames("Hollow Knight", "Celeste");
        var vm = Create(services);

        // Act
        vm.Query = "knig";

        // Assert
        Names(vm).Should().Equal("Hollow Knight");
    }

    [Fact]
    public void Search_without_matches_shows_nothing_found()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);

        // Act
        vm.Query = "zelda ";

        // Assert
        vm.HasNoResults.Should().BeTrue();
        vm.ShowGrid.Should().BeFalse();
        vm.IsEmpty.Should().BeFalse();
        vm.NoResultsText.Should().Be("No games match “zelda”. Game not in the library? Add its folder.");
    }

    [Fact]
    public void Clear_search_shows_every_game_again()
    {
        // Arrange
        using var services = WithGames("Celeste", "Hades");
        var vm = Create(services);
        vm.Query = "zelda";

        // Act
        vm.ClearSearchCommand.Execute(null);

        // Assert
        Names(vm).Should().Equal("Celeste", "Hades");
        vm.HasNoResults.Should().BeFalse();
    }

    [Fact]
    public void Escape_clears_the_search()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);
        vm.Query = "cel";

        // Act
        var handled = vm.TryHandleEscape();

        // Assert
        handled.Should().BeTrue();
        vm.Query.Should().BeEmpty();
    }

    [Fact]
    public void Escape_without_a_search_is_left_to_the_window()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);

        // Act
        var handled = vm.TryHandleEscape();

        // Assert
        handled.Should().BeFalse();
    }

    [Fact]
    public void Recently_played_puts_the_latest_first_and_never_played_last()
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s
            .AddGame(10, "Alpha", lastPlayed: 1_700_000_000)
            .AddGame(20, "Beta")
            .AddGame(30, "Gamma", lastPlayed: 1_800_000_000)
            .AddGame(40, "Delta"));
        var vm = Create(services);

        // Act
        vm.SelectedSort = vm.SortOptions.Single(o => o.Value == LibrarySort.RecentlyPlayed);

        // Assert
        Names(vm).Should().Equal("Gamma", "Alpha", "Beta", "Delta");
    }

    [Fact]
    public void Sort_options_are_important_first_recently_played_and_by_name()
    {
        // Arrange
        using var services = WithGames("Celeste");

        // Act
        var vm = Create(services);

        // Assert
        vm.SortOptions.Select(o => o.Display).Should().Equal("Important first", "Recently played", "By name");
        vm.SelectedSort.Value.Should().Be(LibrarySort.ImportantFirst);
    }

    [Fact]
    public void Before_the_first_scan_shows_skeletons()
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var vm = Create(services);

        // Assert
        vm.IsScanning.Should().BeTrue();
        vm.ShowSkeletons.Should().BeTrue();
        vm.IsEmpty.Should().BeFalse();
        vm.HasNoResults.Should().BeFalse();
    }

    [Fact]
    public void Scanning_with_known_games_shows_them_instead_of_skeletons()
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<GameCatalog>().AddManual(TestGames.Manual("Indie"));

        // Act
        var vm = Create(services);

        // Assert
        vm.IsScanning.Should().BeTrue();
        vm.ShowSkeletons.Should().BeFalse();
        Names(vm).Should().Equal("Indie");
    }

    [Fact]
    public void Steam_not_found_shows_the_empty_state()
    {
        // Arrange
        using var services = TestServices.Build();
        var vm = Create(services);

        // Act
        services.GetRequiredService<GameLibrary>().Refresh();

        // Assert
        vm.IsEmpty.Should().BeTrue();
        vm.IsScanning.Should().BeFalse();
        vm.ShowSkeletons.Should().BeFalse();
        vm.HasNoResults.Should().BeFalse();
    }

    [Fact]
    public void A_game_added_to_the_catalog_appears()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);

        // Act
        services.GetRequiredService<GameCatalog>().AddManual(TestGames.Manual("Indie"));

        // Assert
        Names(vm).Should().Equal("Celeste", "Indie");
    }

    [Fact]
    public void A_catalog_update_keeps_the_card()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);
        var card = vm.VisibleCards.Single();

        // Act
        services.GetRequiredService<GameCatalog>().Update(card.Game.Id, g => g with { Engine = "godot:4" });

        // Assert
        vm.VisibleCards.Single().Should().BeSameAs(card);
        card.ContextLine.Should().Be("Godot 4");
    }

    [Fact]
    public void Clicking_a_card_opens_its_game_page()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.Reset(vm);

        // Act
        vm.VisibleCards[0].OpenCommand.Execute(null);

        // Assert
        navigation.Current.Should().BeOfType<GamePageViewModel>().Which.Game.Name.Should().Be("Celeste");
    }

    [Fact]
    public void Open_folder_opens_the_install_folder()
    {
        // Arrange
        using var services = WithGames("Celeste");
        var vm = Create(services);
        var card = vm.VisibleCards[0];

        // Act
        card.OpenFolderCommand.Execute(null);

        // Assert
        services.GetRequiredService<FakeShell>().OpenedFolders.Should().Equal(card.Game.InstallPath);
    }

    [Fact]
    public void Libraries_button_opens_settings_libraries()
    {
        // Arrange
        using var services = TestServices.Build();
        var vm = Create(services);
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.Reset(vm);

        // Act
        vm.OpenLibrarySettingsCommand.Execute(null);

        // Assert
        navigation.Current.Should().BeOfType<SettingsViewModel>().Which.SelectedSection.Id.Should().Be(SettingsSectionId.Libraries);
    }

    [Fact]
    public void Game_count_names_the_grid()
    {
        // Arrange
        using var services = WithGames("Celeste", "Hades");

        // Act
        var vm = Create(services);

        // Assert
        vm.GameCount.Should().Be("2 games");
    }
}
