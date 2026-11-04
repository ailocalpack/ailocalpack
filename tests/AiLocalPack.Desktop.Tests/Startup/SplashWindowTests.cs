using System.Globalization;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiLocalPack.Desktop.Tests.Startup;

public sealed class SplashWindowTests
{
    private static (SplashWindow Window, StartupStep Step) Open()
    {
        var localizer = new Localizer(CultureInfo.GetCultureInfo("en"));
        var clock = new FakeClock();
        var step = new StartupStep("settings", StartupPhase.Blocking, "Startup_Step_Settings", localizer, clock);
        var window = new SplashWindow { DataContext = new SplashViewModel(new StartupProgress([step], localizer), localizer, clock) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, step);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public Task Shows_the_running_step() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, step) = Open();

        // Act
        step.Start();
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<TextBlock>(window, "Current").Text.Should().Be("Loading settings…");
        Find<TextBlock>(window, "Version").Text.Should().Be($"Version {AppVersion.Current}");
        Find<ItemsControl>(window, "Steps").IsVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Details_button_shows_the_steps() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open();

        // Act
        Find<Button>(window, "DetailsToggle").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        var steps = Find<ItemsControl>(window, "Steps");
        (steps.IsVisible, steps.ItemCount).Should().Be((true, 1));
        window.GetVisualDescendants().OfType<StartupStepView>().Should().ContainSingle();
        window.Close();
    });
}
