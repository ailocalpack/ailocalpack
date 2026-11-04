using System.Globalization;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Startup;

namespace AiLocalPack.Desktop.Tests.Startup;

public sealed class StartupProgressTests
{
    private readonly Localizer _localizer = new(CultureInfo.GetCultureInfo("en"));
    private readonly FakeClock _clock = new();

    private StartupStep Step(string id, StartupPhase phase) => new(id, phase, "Startup_Step_Settings", _localizer, _clock);

    [Fact]
    public void Waiting_step_says_waiting()
    {
        // Arrange
        var step = Step("a", StartupPhase.Blocking);

        // Act
        var detail = step.Detail;

        // Assert
        detail.Should().Be("Waiting");
    }

    [Fact]
    public void Started_step_records_when_it_started()
    {
        // Arrange
        var step = Step("a", StartupPhase.Blocking);

        // Act
        step.Start();

        // Assert
        (step.IsRunning, step.StartedAt).Should().Be((true, _clock.GetUtcNow()));
    }

    [Fact]
    public void Running_step_counts_half_of_the_blocking_progress()
    {
        // Arrange
        var first = Step("a", StartupPhase.Blocking);
        var second = Step("b", StartupPhase.Blocking);
        var progress = new StartupProgress([first, second], _localizer);
        first.Complete(StartupTone.Ok, () => "done");

        // Act
        second.Start();

        // Assert
        progress.BlockingPercent.Should().Be(75);
    }

    [Fact]
    public void Current_is_the_running_blocking_step()
    {
        // Arrange
        var first = Step("a", StartupPhase.Blocking);
        var second = Step("b", StartupPhase.Blocking);
        var progress = new StartupProgress([first, second], _localizer);
        first.Skip(() => "nothing to do");

        // Act
        second.Start();

        // Assert
        progress.Current.Should().BeSameAs(second);
    }

    [Fact]
    public void Background_runs_while_one_of_its_steps_runs()
    {
        // Arrange
        var blocking = Step("a", StartupPhase.Blocking);
        var background = Step("b", StartupPhase.Background);
        var progress = new StartupProgress([blocking, background], _localizer);

        // Act
        background.Start();

        // Assert
        progress.IsBackgroundRunning.Should().BeTrue();
    }

    [Fact]
    public void Background_summary_counts_finished_checks()
    {
        // Arrange
        var first = Step("a", StartupPhase.Background);
        var second = Step("b", StartupPhase.Background);
        var progress = new StartupProgress([first, second], _localizer);

        // Act
        first.Complete(StartupTone.Warn, () => "failed");

        // Assert
        progress.BackgroundSummary.Should().Be("1 of 2");
    }

    [Fact]
    public void Step_change_raises_the_totals()
    {
        // Arrange
        var step = Step("a", StartupPhase.Blocking);
        var progress = new StartupProgress([step], _localizer);
        var raised = new List<string?>();
        progress.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        step.Start();

        // Assert
        raised.Should().Contain([nameof(StartupProgress.BlockingPercent), nameof(StartupProgress.Current)]);
    }
}
