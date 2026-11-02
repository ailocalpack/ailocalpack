using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class GeneralSettingsSectionTests : IDisposable
{
    private readonly TestLogger<GeneralSettingsSection> _logger = new();
    private readonly ServiceProvider _services;

    public GeneralSettingsSectionTests()
    {
        _services = TestServices.Build(s => s.AddSingleton<ILogger<GeneralSettingsSection>>(_logger));
        Store.Load();
    }

    private SettingsStore Store => _services.GetRequiredService<SettingsStore>();

    private GeneralSettingsSection Section => _services.GetRequiredService<GeneralSettingsSection>();

    public void Dispose() => _services.Dispose();

    // A folder where the settings file should be: every save fails.
    private void BreakSettingsFile()
    {
        var file = _services.GetRequiredService<AppPaths>().SettingsFile;
        File.Delete(file);
        Directory.CreateDirectory(file);
    }

    private static ChoiceOption Option(ChoiceSettingRow row, object? value) => row.Options.Single(o => Equals(o.Value, value));

    [Fact]
    public void Has_language_appearance_background_and_notification_groups()
    {
        // Arrange
        var section = Section;

        // Act
        var groups = section.Groups.Select(g => g.Title);

        // Assert
        groups.Should().Equal("Language", "Appearance", "Background", "Notifications");
        section.IsComingSoon.Should().BeFalse();
    }

    [Fact]
    public void Defaults_preselect_the_os_language_and_the_system_theme()
    {
        // Arrange
        var section = Section;

        // Act
        var selected = (section.UiLanguage.SelectedOption, section.GameLanguage.SelectedOption, section.Theme.SelectedOption);

        // Assert
        selected.Item1.Should().BeSameAs(section.UiLanguage.Options[0]);
        selected.Item2.Should().BeSameAs(section.GameLanguage.Options[0]);
        selected.Item3!.Value.Should().Be(AppTheme.System);
        selected.Item1!.Display.Should().StartWith("System (");
    }

    [Fact]
    public void Picking_a_theme_saves_it()
    {
        // Arrange
        var theme = Section.Theme;

        // Act
        theme.SelectedOption = Option(theme, AppTheme.Dark);

        // Assert
        Store.Current.Theme.Should().Be(AppTheme.Dark);
    }

    [Fact]
    public void Picking_a_game_language_saves_its_tag()
    {
        // Arrange
        var games = Section.GameLanguage;

        // Act
        games.SelectedOption = Option(games, "uk");

        // Assert
        Store.Current.GameLanguage.Should().Be("uk");
    }

    [Fact]
    public void Picking_system_language_clears_the_setting()
    {
        // Arrange
        var ui = Section.UiLanguage;
        Store.Update(s => s with { UiLanguage = "ru" });

        // Act
        ui.SelectedOption = ui.Options[0];

        // Assert
        Store.Current.UiLanguage.Should().BeNull();
    }

    [Fact]
    public void A_null_selection_from_the_control_is_ignored()
    {
        // Arrange
        var games = Section.GameLanguage;
        Store.Update(s => s with { GameLanguage = "de" });

        // Act
        games.SelectedOption = null;

        // Assert
        Store.Current.GameLanguage.Should().Be("de");
        games.SelectedOption!.Value.Should().Be("de");
    }

    [Fact]
    public void Turning_a_toggle_off_saves_it()
    {
        // Arrange
        var tray = Section.RunInBackground;

        // Act
        tray.IsOn = false;

        // Assert
        Store.Current.RunInBackground.Should().BeFalse();
    }

    [Fact]
    public void Rows_follow_changes_made_elsewhere()
    {
        // Arrange
        var section = Section;

        // Act
        Store.Update(s => s with { Theme = AppTheme.Light, NotifyCommunity = false, GameLanguage = "ja" });

        // Assert
        section.Theme.SelectedOption!.Value.Should().Be(AppTheme.Light);
        section.NotifyCommunity.IsOn.Should().BeFalse();
        section.GameLanguage.SelectedOption!.Value.Should().Be("ja");
    }

    [Fact]
    public void An_unknown_stored_language_shows_system()
    {
        // Arrange
        var section = Section;

        // Act
        Store.Update(s => s with { GameLanguage = "xx-unknown" });

        // Assert
        section.GameLanguage.SelectedOption.Should().BeSameAs(section.GameLanguage.Options[0]);
    }

    [Fact]
    public void Reapply_explanation_says_only_at_start_while_the_tray_is_off()
    {
        // Arrange
        var section = Section;

        // Act
        section.RunInBackground.IsOn = false;

        // Assert
        section.ReapplyAfterGameUpdates.Help.Should().Be("Only when the app starts, because the tray is off.");
        section.Groups[3].Note.Should().StartWith("System notifications need the tray");
    }

    [Fact]
    public void Reapply_explanation_changes_when_turned_off()
    {
        // Arrange
        var reapply = Section.ReapplyAfterGameUpdates;

        // Act
        reapply.IsOn = false;

        // Assert
        reapply.Help.Should().Be("You’ll be notified instead and can re-apply by hand.");
    }

    [Fact]
    public void Interface_language_links_to_the_translations_on_github()
    {
        // Arrange
        var ui = Section.UiLanguage;

        // Act
        var link = (ui.HelpLinkText, ui.HelpLinkUri);

        // Assert
        link.HelpLinkText.Should().Be("Fix it on GitHub");
        link.HelpLinkUri.Should().Be(GeneralSettingsSection.TranslationsUri);
    }

    [Fact]
    public void Failed_save_keeps_the_stored_value_and_logs()
    {
        // Arrange
        var theme = Section.Theme;
        BreakSettingsFile();

        // Act
        theme.SelectedOption = Option(theme, AppTheme.Dark);

        // Assert
        Store.Current.Theme.Should().Be(AppTheme.System);
        theme.SelectedOption!.Value.Should().Be(AppTheme.System);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Failed_toggle_save_switches_the_toggle_back()
    {
        // Arrange
        var tray = Section.RunInBackground;
        BreakSettingsFile();

        // Act
        tray.IsOn = false;

        // Assert
        tray.IsOn.Should().BeTrue();
        Store.Current.RunInBackground.Should().BeTrue();
    }
}
