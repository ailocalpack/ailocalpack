using System.Globalization;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.ViewModels;

namespace AiLocalPack.Desktop.Tests.Startup;

public sealed class SplashViewModelTests
{
    private readonly Localizer _localizer = new(CultureInfo.GetCultureInfo("en"));
    private readonly FakeClock _clock = new();
    private readonly StartupStep _settings;
    private readonly StartupStep _catalog;
    private readonly SplashViewModel _vm;

    public SplashViewModelTests()
    {
        _settings = new StartupStep("settings", StartupPhase.Blocking, "Startup_Step_Settings", _localizer, _clock);
        _catalog = new StartupStep("catalog", StartupPhase.Blocking, "Startup_Step_Catalog", _localizer, _clock);
        _vm = new SplashViewModel(new StartupProgress([_settings, _catalog], _localizer), _localizer, _clock);
    }

    [Fact]
    public void Names_the_running_step()
    {
        // Arrange
        _settings.Complete(StartupTone.Ok, () => "English · dark theme");

        // Act
        _catalog.Start();

        // Assert
        _vm.CurrentText.Should().Be("Opening your game library…");
    }

    [Fact]
    public void Details_are_closed_on_a_quick_start()
    {
        // Arrange
        _settings.Start();
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        _vm.CheckSlow();

        // Assert
        (_vm.IsSlow, _vm.IsDetailsOpen, _vm.ToggleText).Should().Be((false, false, "Details"));
    }

    [Fact]
    public void Slow_step_opens_the_details()
    {
        // Arrange
        _settings.Start();
        _clock.Advance(SplashViewModel.SlowAfter);

        // Act
        _vm.CheckSlow();

        // Assert
        (_vm.IsSlow, _vm.IsDetailsOpen, _vm.ToggleText).Should().Be((true, true, "Hide details"));
    }

    [Fact]
    public void Slow_note_says_how_far_it_got()
    {
        // Arrange
        _settings.Complete(StartupTone.Ok, () => "ok");
        _catalog.Start();
        _clock.Advance(SplashViewModel.SlowAfter);

        // Act
        _vm.CheckSlow();

        // Assert
        _vm.SlowText.Should().Be("Taking longer than usual. Please don't close the app: 1 of 2 steps done.");
    }

    [Fact]
    public void Player_can_close_the_details_of_a_slow_step()
    {
        // Arrange
        _settings.Start();
        _clock.Advance(SplashViewModel.SlowAfter);
        _vm.CheckSlow();

        // Act
        _vm.ToggleDetailsCommand.Execute(null);

        // Assert
        _vm.IsDetailsOpen.Should().BeFalse();
    }

    [Fact]
    public void Warning_of_a_blocking_step_shows_as_a_note()
    {
        // Arrange
        _settings.Complete(StartupTone.Ok, () => "ok");

        // Act
        _catalog.Complete(StartupTone.Warn, () => "The game list was damaged");

        // Assert
        (_vm.HasWarning, _vm.Warning).Should().Be((true, "The game list was damaged"));
    }

    [Fact]
    public void Progress_follows_the_steps()
    {
        // Arrange
        _settings.Complete(StartupTone.Ok, () => "ok");

        // Act
        _catalog.Complete(StartupTone.Ok, () => "2 games");

        // Assert
        _vm.Percent.Should().Be(100);
    }
}
