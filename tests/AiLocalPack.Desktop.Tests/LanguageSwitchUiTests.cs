using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests;

[Collection(typeof(UiLanguageSwitchCollection))]
public sealed class LanguageSwitchUiTests
{
    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public Task Switching_language_updates_the_open_window() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Composition.BuildServices(NullLoggerFactory.Instance);
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var library = (LibraryViewModel)vm.CurrentPage!;
        library.OpenGameCommand.Execute(library.Games[0]);
        Dispatcher.UIThread.RunJobs();

        try
        {
            // Act
            Localizer.Current.SetLanguage("ru");
            Dispatcher.UIThread.RunJobs();

            // Assert
            Find<Button>(window, "BackButton").Content.Should().Be("‹ Библиотека");
            ToolTip.GetTip(Find<Button>(window, "SettingsButton")).Should().Be("Настройки");
            ToolTip.GetTip(Find<Button>(window, "ActivityButton")).Should().Be("Активность");
        }
        finally
        {
            Localizer.Current.SetLanguage(UiLanguages.Fallback);
            window.Close();
        }
    });

    [Fact]
    public Task Library_shows_the_game_count_in_the_current_language() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = Composition.BuildServices(NullLoggerFactory.Instance);
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            // Act
            Localizer.Current.SetLanguage("ru");
            Dispatcher.UIThread.RunJobs();

            // Assert
            Find<TextBlock>(window, "GameCount").Text.Should().Be("2 игры");
            window.GetVisualDescendants().OfType<LibraryView>().Should().ContainSingle();
            window.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "Библиотека");
        }
        finally
        {
            Localizer.Current.SetLanguage(UiLanguages.Fallback);
            window.Close();
        }
    });
}
