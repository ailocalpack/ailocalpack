using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

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
        using var services = TestServices.Build();
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
        using var services = TestServices.Build();
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

    [Fact]
    public Task Picking_russian_in_settings_switches_the_ui_without_restart() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<SettingsApplier>().Start();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var language = services.GetRequiredService<GeneralSettingsSection>().UiLanguage;

        try
        {
            // Act
            language.SelectedOption = language.Options.Single(o => (string?)o.Value == "ru");
            Dispatcher.UIThread.RunJobs();

            // Assert
            Find<TextBlock>(window, "SectionTitle").Text.Should().Be("Общие");
            Find<Button>(window, "BackButton").Content.Should().Be("‹ Библиотека");
            window.GetVisualDescendants().OfType<HighlightTextBlock>().Should().Contain(t => t.Source == "Тема");
            language.Options[0].Display.Should().StartWith("Системный (");
        }
        finally
        {
            Localizer.Current.SetLanguage(UiLanguages.Fallback);
            window.Close();
        }
    });
}
