using System.Globalization;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests;

public sealed class MainWindowViewModelTests
{
    private static MainWindowViewModel Create(out ServiceProvider services)
    {
        services = TestServices.Build();
        return services.GetRequiredService<MainWindowViewModel>();
    }

    [Fact]
    public void Starts_on_library_without_back()
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var vm = services.GetRequiredService<MainWindowViewModel>();

        // Assert
        vm.CurrentPage.Should().BeOfType<LibraryViewModel>();
        vm.CanGoBack.Should().BeFalse();
        vm.GoBackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Opening_a_game_shows_its_page_with_back_to_library()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;
        var library = (LibraryViewModel)vm.CurrentPage!;

        // Act
        library.OpenGameCommand.Execute(library.Games[0]);

        // Assert
        vm.CurrentPage.Should().BeOfType<GamePageViewModel>().Which.Game.Should().Be(library.Games[0]);
        vm.BackLabel.Should().Be("‹ Library");
        vm.GoBackCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Back_from_a_game_returns_to_library()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;
        var library = (LibraryViewModel)vm.CurrentPage!;
        library.OpenGameCommand.Execute(library.Games[0]);

        // Act
        vm.GoBackCommand.Execute(null);

        // Assert
        vm.CurrentPage.Should().BeSameAs(library);
    }

    [Fact]
    public void Open_settings_shows_settings()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;

        // Act
        vm.OpenSettingsCommand.Execute(null);

        // Assert
        vm.CurrentPage.Should().BeOfType<SettingsViewModel>();
    }

    [Fact]
    public void Navigation_raises_property_changed_for_current_page()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Act
        vm.OpenSettingsCommand.Execute(null);

        // Assert
        changed.Should().Contain([nameof(MainWindowViewModel.CurrentPage), nameof(MainWindowViewModel.CanGoBack), nameof(MainWindowViewModel.BackLabel)]);
    }

    [Fact]
    public void Keeps_the_page_the_navigation_already_shows()
    {
        // Arrange
        var navigation = new NavigationService();
        var settings = TestSettingsPages.Create();
        navigation.Reset(settings);
        using var services = new ServiceCollection().BuildServiceProvider();

        // Act
        var vm = new MainWindowViewModel(navigation, new PageFactory(services), new Localizer(CultureInfo.GetCultureInfo("en")));

        // Assert
        vm.CurrentPage.Should().BeSameAs(settings);
    }

    [Fact]
    public void Escape_goes_back_when_the_page_does_not_use_it()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;
        vm.OpenSettingsCommand.Execute(null);

        // Act
        vm.EscapeCommand.Execute(null);

        // Assert
        vm.CurrentPage.Should().BeOfType<LibraryViewModel>();
    }

    [Fact]
    public void Escape_clears_a_settings_search_before_going_back()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;
        vm.OpenSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)vm.CurrentPage!;
        settings.Query = "theme";

        // Act
        vm.EscapeCommand.Execute(null);

        // Assert
        vm.CurrentPage.Should().BeSameAs(settings);
        settings.Query.Should().BeEmpty();
    }

    [Fact]
    public void Escape_on_the_library_does_nothing()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;
        var library = vm.CurrentPage;

        // Act
        vm.EscapeCommand.Execute(null);

        // Assert
        vm.CurrentPage.Should().BeSameAs(library);
    }

    [Fact]
    public void Settings_button_is_active_while_settings_is_open()
    {
        // Arrange
        var vm = Create(out var services);
        using var _ = services;

        // Act
        vm.OpenSettingsCommand.Execute(null);

        // Assert
        vm.IsSettingsOpen.Should().BeTrue();
    }
}
