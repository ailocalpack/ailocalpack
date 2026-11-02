using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;

namespace AiLocalPack.Desktop.Tests;

public sealed class NavigationServiceTests
{
    private sealed class RootPage : PageViewModel
    {
        public override string Title => "Root";
    }

    private static GamePageViewModel Game(string id) => new(new StubGame(id, id));

    [Fact]
    public void Navigate_to_pushes_the_page()
    {
        // Arrange
        var navigation = new NavigationService();
        var root = new RootPage();
        navigation.Reset(root);
        var game = Game("stub:1");

        // Act
        navigation.NavigateTo(game);

        // Assert
        navigation.Current.Should().BeSameAs(game);
        navigation.Previous.Should().BeSameAs(root);
        navigation.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void Navigate_to_raises_navigated()
    {
        // Arrange
        var navigation = new NavigationService();
        navigation.Reset(new RootPage());
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;

        // Act
        navigation.NavigateTo(Game("stub:1"));

        // Assert
        raised.Should().Be(1);
    }

    [Fact]
    public void Back_returns_to_the_previous_page()
    {
        // Arrange
        var navigation = new NavigationService();
        var root = new RootPage();
        navigation.Reset(root);
        navigation.NavigateTo(Game("stub:1"));

        // Act
        navigation.GoBack();

        // Assert
        navigation.Current.Should().BeSameAs(root);
        navigation.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void Back_on_root_does_nothing()
    {
        // Arrange
        var navigation = new NavigationService();
        var root = new RootPage();
        navigation.Reset(root);
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;

        // Act
        navigation.GoBack();

        // Assert
        navigation.Current.Should().BeSameAs(root);
        raised.Should().Be(0);
    }

    [Fact]
    public void Back_before_any_page_does_nothing()
    {
        // Arrange
        var navigation = new NavigationService();

        // Act
        navigation.GoBack();

        // Assert
        navigation.Current.Should().BeNull();
    }

    [Fact]
    public void Navigating_to_settings_from_settings_is_a_no_op()
    {
        // Arrange
        var navigation = new NavigationService();
        navigation.Reset(new RootPage());
        var settings = TestSettingsPages.Create();
        navigation.NavigateTo(settings);
        var raised = 0;
        navigation.Navigated += (_, _) => raised++;

        // Act
        navigation.NavigateTo(TestSettingsPages.Create());

        // Assert
        navigation.Current.Should().BeSameAs(settings);
        navigation.Previous.Should().BeOfType<RootPage>();
        raised.Should().Be(0);
    }

    [Fact]
    public void Navigating_to_the_same_game_is_a_no_op()
    {
        // Arrange
        var navigation = new NavigationService();
        navigation.Reset(new RootPage());
        var game = Game("stub:1");
        navigation.NavigateTo(game);

        // Act
        navigation.NavigateTo(Game("stub:1"));

        // Assert
        navigation.Current.Should().BeSameAs(game);
    }

    [Fact]
    public void Navigating_to_another_game_pushes_it()
    {
        // Arrange
        var navigation = new NavigationService();
        navigation.Reset(new RootPage());
        var first = Game("stub:1");
        navigation.NavigateTo(first);
        var second = Game("stub:2");

        // Act
        navigation.NavigateTo(second);

        // Assert
        navigation.Current.Should().BeSameAs(second);
        navigation.Previous.Should().BeSameAs(first);
    }

    [Fact]
    public void Reset_clears_the_back_stack()
    {
        // Arrange
        var navigation = new NavigationService();
        navigation.Reset(new RootPage());
        navigation.NavigateTo(Game("stub:1"));
        var root = new RootPage();

        // Act
        navigation.Reset(root);

        // Assert
        navigation.Current.Should().BeSameAs(root);
        navigation.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void Navigate_to_null_throws()
    {
        // Arrange
        var navigation = new NavigationService();

        // Act
        var act = () => navigation.NavigateTo(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}
