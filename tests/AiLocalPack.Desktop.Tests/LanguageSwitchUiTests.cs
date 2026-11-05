using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Library;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.Views;
using Avalonia.Automation;
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
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var library = (LibraryViewModel)vm.CurrentPage!;
        library.VisibleCards[0].OpenCommand.Execute(null);
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
    public Task Library_shows_its_toolbar_in_the_current_language() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            // Act
            Localizer.Current.SetLanguage("ru");
            Dispatcher.UIThread.RunJobs();

            // Assert
            Find<TextBox>(window, "SearchBox").PlaceholderText.Should().Be("Поиск игр");
            Find<ComboBox>(window, "SortBox").SelectedItem.Should().BeOfType<LibrarySortOption>().Which.Display.Should().Be("Сначала важное");
            AutomationProperties.GetName(Find<ItemsControl>(window, "Games")).Should().Be("1 игра");
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
