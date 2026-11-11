using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings.About;
using AiLocalPack.Desktop.Welcome;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Welcome;

/// <summary>The welcome tour (design §10): slides, closing, the first-run rule and where it opens from.</summary>
public sealed class WelcomeTourTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build(steam: s => s.AddGame(10, "Alpha").AddGame(20, "Beta"));

    public void Dispose() => _services.Dispose();

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private WelcomeTourViewModel Open() => Get<IDialogService>().Show<WelcomeTourViewModel>();

    [Fact]
    public void Starts_on_the_welcome_slide()
    {
        // Arrange
        var tour = Open();

        // Act
        var step = tour.StepText;

        // Assert
        step.Should().Be("1 / 4 · Welcome");
    }

    [Fact]
    public void Welcome_slide_counts_the_games_found()
    {
        // Arrange
        var tour = Open();

        // Act
        var found = tour.GamesFound;

        // Assert
        found.Should().Be("We found 2 games in your library.");
    }

    [Fact]
    public void Welcome_slide_says_nothing_about_games_before_any_are_found()
    {
        // Arrange
        using var empty = TestServices.Build();

        // Act
        var tour = empty.GetRequiredService<IDialogService>().Show<WelcomeTourViewModel>();

        // Assert
        tour.GamesFound.Should().BeNull();
    }

    [Fact]
    public void Next_goes_to_the_install_slide()
    {
        // Arrange
        var tour = Open();

        // Act
        tour.NextCommand.Execute(null);

        // Assert
        tour.Slide.Should().Be(WelcomeSlide.Install);
    }

    [Fact]
    public void Back_returns_to_the_previous_slide()
    {
        // Arrange
        var tour = Open();
        tour.NextCommand.Execute(null);
        tour.NextCommand.Execute(null);

        // Act
        tour.BackCommand.Execute(null);

        // Assert
        tour.Slide.Should().Be(WelcomeSlide.Install);
    }

    [Fact]
    public void Back_is_off_on_the_first_slide()
    {
        // Arrange
        var tour = Open();

        // Act
        var canGoBack = tour.BackCommand.CanExecute(null);

        // Assert
        canGoBack.Should().BeFalse();
    }

    [Fact]
    public void Main_button_reads_lets_go_on_the_first_slide()
    {
        // Arrange
        var tour = Open();

        // Act
        var text = tour.NextText;

        // Assert
        text.Should().Be("Let's go");
    }

    [Fact]
    public void Main_button_opens_the_library_on_the_last_slide()
    {
        // Arrange
        var tour = Open();

        // Act
        tour.Index = WelcomeTourViewModel.SlideCount - 1;

        // Assert
        tour.NextText.Should().Be("Open my library");
    }

    [Fact]
    public void Install_slide_lists_undo_updates_and_anti_cheat()
    {
        // Arrange
        var tour = Open();

        // Act
        tour.NextCommand.Execute(null);

        // Assert
        tour.Points.Select(p => p.IconKey).Should().Equal("IconUndo", "IconRefresh", "IconShield");
    }

    [Fact]
    public void Next_on_the_last_slide_closes_the_tour()
    {
        // Arrange
        var tour = Open();
        tour.Index = WelcomeTourViewModel.SlideCount - 1;

        // Act
        tour.NextCommand.Execute(null);

        // Assert
        Get<IDialogService>().Current.Should().BeNull();
    }

    [Fact]
    public void Skip_closes_the_tour()
    {
        // Arrange
        var tour = Open();

        // Act
        tour.SkipCommand.Execute(null);

        // Assert
        Get<IDialogService>().Current.Should().BeNull();
    }

    [Fact]
    public void Closing_the_tour_remembers_it_was_shown()
    {
        // Arrange
        var tour = Open();

        // Act
        tour.SkipCommand.Execute(null);

        // Assert
        Get<SettingsStore>().Current.WelcomeTourShown.Should().BeTrue();
    }

    [Fact]
    public void Language_choice_saves_the_game_language()
    {
        // Arrange
        var tour = Open();
        var german = tour.GameLanguage.Options.Single(o => Equals(o.Value, "de"));

        // Act
        tour.GameLanguage.SelectedOption = german;

        // Assert
        Get<SettingsStore>().Current.GameLanguage.Should().Be("de");
    }

    [Fact]
    public void First_run_shows_the_tour()
    {
        // Arrange
        var welcome = Get<WelcomeTour>();

        // Act
        welcome.ShowOnFirstRun();

        // Assert
        Get<IDialogService>().Current.Should().BeOfType<WelcomeTourViewModel>();
    }

    [Fact]
    public void Later_runs_do_not_show_the_tour()
    {
        // Arrange
        Get<SettingsStore>().Update(s => s with { WelcomeTourShown = true });

        // Act
        var shown = Get<WelcomeTour>().ShowOnFirstRun();

        // Assert
        shown.Should().BeFalse();
    }

    [Fact]
    public void First_run_leaves_another_open_dialog_alone()
    {
        // Arrange
        var other = Get<IDialogService>().Show<ConfirmDialogViewModel>(new ConfirmRequest("Title", "Message", "OK", () => { }));

        // Act
        Get<WelcomeTour>().ShowOnFirstRun();

        // Assert
        Get<IDialogService>().Current.Should().BeSameAs(other);
    }

    [Fact]
    public void Question_mark_in_the_chrome_shows_the_tour_again()
    {
        // Arrange
        Get<SettingsStore>().Update(s => s with { WelcomeTourShown = true });
        var chrome = Get<MainWindowViewModel>();

        // Act
        chrome.ShowWelcomeTourCommand.Execute(null);

        // Assert
        Get<IDialogService>().Current.Should().BeOfType<WelcomeTourViewModel>();
    }

    [Fact]
    public void Settings_about_shows_the_tour_again()
    {
        // Arrange
        Get<SettingsStore>().Update(s => s with { WelcomeTourShown = true });
        var about = Get<AboutSettingsSection>();

        // Act
        about.WelcomeTour.ShowCommand.Execute(null);

        // Assert
        Get<IDialogService>().Current.Should().BeOfType<WelcomeTourViewModel>();
    }
}
