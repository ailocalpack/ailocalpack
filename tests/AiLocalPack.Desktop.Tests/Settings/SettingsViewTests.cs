using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Settings;
using AiLocalPack.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class SettingsViewTests
{
    private static MainWindow Open(ServiceProvider services, SettingsSectionId section = SettingsSectionId.General)
    {
        var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        var pages = services.GetRequiredService<IPageFactory>();
        services.GetRequiredService<INavigationService>().NavigateTo(pages.Create<SettingsViewModel>(section));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static SettingsViewModel Page(ServiceProvider services) =>
        (SettingsViewModel)services.GetRequiredService<INavigationService>().Current!;

    private static void Type(Window window, string text)
    {
        Find<TextBox>(window, "SearchBox").Focus();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Shows_the_section_list_and_general() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var window = Open(services);

        // Assert
        Find<TextBlock>(window, "SectionTitle").Text.Should().Be("General");
        Find<ListBox>(window, "SectionList").ItemCount.Should().Be(6);
        window.GetVisualDescendants().OfType<HighlightTextBlock>().Select(t => t.Source)
            .Should().Contain(["Interface language", "Translate games into", "Theme"]);
        Find<Button>(window, "SettingsButton").Classes.Should().Contain("on");
        window.Close();
    });

    [Fact]
    public Task Deep_link_opens_the_right_section() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();

        // Act
        var window = Open(services, SettingsSectionId.Libraries);

        // Assert
        Find<TextBlock>(window, "SectionTitle").Text.Should().Be("Libraries");
        Find<ListBox>(window, "SectionList").SelectedItem.Should().BeAssignableTo<SettingsSection>()
            .Which.Id.Should().Be(SettingsSectionId.Libraries);
        Find<TextBlock>(window, "ComingSoon").IsEffectivelyVisible.Should().BeFalse();
        window.Close();
    });

    [Fact]
    public Task Agents_section_shows_each_agent_with_its_status() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");

        // Act
        var window = Open(services, SettingsSectionId.Agents);

        // Assert
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "AgentStatus")
            .Select(b => b.GetVisualDescendants().OfType<TextBlock>().Last().Text)
            .Should().Equal("Ready · signed in", "Not installed");
        Find<TextBlock>(window, "SectionLead").IsEffectivelyVisible.Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Searching_theme_shows_the_appearance_row_under_general() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);

        // Act
        Type(window, "theme");

        // Assert
        var results = Find<StackPanel>(window, "SearchResults");
        results.IsVisible.Should().BeTrue();
        Find<StackPanel>(window, "SectionPage").IsVisible.Should().BeFalse();
        results.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "General");
        results.GetVisualDescendants().OfType<HighlightTextBlock>().Should().ContainSingle()
            .Which.HighlightedText.Should().Be("Theme");
        window.Close();
    });

    [Fact]
    public Task A_search_without_matches_says_so() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);

        // Act
        Type(window, "xyzzy");

        // Assert
        var noResults = Find<TextBlock>(window, "NoResults");
        noResults.IsEffectivelyVisible.Should().BeTrue();
        noResults.Text.Should().Be("No settings match “xyzzy”.");
        window.Close();
    });

    [Fact]
    public Task Escape_clears_the_search_and_stays_in_settings() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);
        Type(window, "theme");

        // Act
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Page(services).Query.Should().BeEmpty();
        Find<TextBox>(window, "SearchBox").Text.Should().BeNullOrEmpty();
        Find<StackPanel>(window, "SectionPage").IsVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<SettingsView>().Should().ContainSingle();
        window.Close();
    });

    [Fact]
    public Task Second_escape_goes_back_to_the_library() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);
        Type(window, "theme");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        // Act
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        // Assert
        window.GetVisualDescendants().OfType<LibraryView>().Should().ContainSingle();
        window.Close();
    });

    [Fact]
    public Task Ctrl_f_focuses_the_search() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);

        // Act
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<TextBox>(window, "SearchBox").IsFocused.Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Clicking_a_section_opens_it() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);
        var item = Find<ListBox>(window, "SectionList").GetVisualDescendants().OfType<ListBoxItem>().ElementAt(5);
        var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;

        // Act
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<TextBlock>(window, "SectionTitle").Text.Should().Be("About");
        window.Close();
    });

    [Fact]
    public Task Clicking_dark_saves_the_theme() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build();
        var window = Open(services);
        var dark = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(i => i.DataContext is ChoiceOption { Value: AppTheme.Dark });
        var center = dark.TranslatePoint(new Point(dark.Bounds.Width / 2, dark.Bounds.Height / 2), window)!.Value;

        // Act
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Assert
        services.GetRequiredService<SettingsStore>().Current.Theme.Should().Be(AppTheme.Dark);
        window.Close();
    });

    [Fact]
    public Task Libraries_shows_folders_the_alert_and_the_badge() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        string card = "";
        using var services = TestServices.Build(steam: s => card = s.AddLibrary("SdCard", create: false));

        // Act
        var window = Open(services, SettingsSectionId.Libraries);

        // Assert
        window.GetVisualDescendants().OfType<HighlightTextBlock>().Select(t => t.Source)
            .Should().Contain([card, "Add Steam library folder", "Add game folder", "Epic Games, GOG, EA app"]);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
            .Should().Contain(["A Steam library isn't available", "Not available"]);
        window.GetVisualDescendants().OfType<HighlightTextBlock>().Where(t => t.Name == "FolderPath")
            .Should().ContainSingle(t => t.Source == card).Which.Classes.Should().Contain("struck");
        var libraries = Find<ListBox>(window, "SectionList").Items.OfType<SettingsSection>().Single(s => s.Id == SettingsSectionId.Libraries);
        libraries.HasBadge.Should().BeTrue();
        window.Close();
    });

    [Fact]
    public Task Removing_a_folder_shows_the_confirmation_dialog() => HeadlessUi.RunAsync(() =>
    {
        // Arrange
        using var services = TestServices.Build(steam: s => s.AddLibrary("SdCard", create: false));
        var window = Open(services, SettingsSectionId.Libraries);
        var section = services.GetRequiredService<ViewModels.Settings.Libraries.LibrariesSettingsSection>();

        // Act
        section.Folders[1].RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        Find<TextBlock>(window, "ConfirmTitle").Text.Should().StartWith("Remove «");
        Find<Button>(window, "ConfirmButton").Classes.Should().Contain("danger");
        window.Close();
    });
}
