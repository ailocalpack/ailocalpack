using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using AiLocalPack.Desktop.Welcome;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Welcome;

/// <summary>The welcome tour in the window (design §10): drawn as a dialog, arrows move, Esc closes, <c>?</c> opens it.</summary>
public sealed class WelcomeTourViewTests
{
    private static (MainWindow Window, ServiceProvider Services) Open(bool tour = true)
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        window.Closed += (_, _) => services.Dispose();
        window.Show();
        if (tour)
        {
            services.GetRequiredService<WelcomeTour>().Show();
        }

        Dispatcher.UIThread.RunJobs();
        return (window, services);
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Tour_shows_the_welcome_slide() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open();

        // Act
        var title = Find<TextBlock>(window, "SlideTitle").Text;

        // Assert
        title.Should().Be("Play your games in your language");
        window.Close();
    });

    [Fact]
    public Task Tour_starts_with_the_focus_on_its_main_button() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open();

        // Act
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<Button>(window, "NextButton").IsFocused.Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Right_arrow_moves_to_the_next_slide() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, services) = Open();

        // Act
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Assert
        ((WelcomeTourViewModel)services.GetRequiredService<IDialogService>().Current!).Slide.Should().Be(WelcomeSlide.Install);
        window.Close();
    });

    [Fact]
    public Task Escape_closes_the_tour() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, services) = Open();

        // Act
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Assert
        services.GetRequiredService<IDialogService>().Current.Should().BeNull();
        window.Close();
    });

    [Fact]
    public Task Question_mark_button_opens_the_tour() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        var (window, _) = Open(tour: false);

        // Act
        Click(window, Find<Button>(window, "WelcomeTourButton"));

        // Assert
        window.GetVisualDescendants().OfType<WelcomeTourView>().Should().ContainSingle();
        window.Close();
    });
}
