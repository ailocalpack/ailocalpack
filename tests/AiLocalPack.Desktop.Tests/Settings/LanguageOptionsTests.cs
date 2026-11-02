using System.Globalization;
using AiLocalPack.Core.Languages;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.Settings;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class LanguageOptionsTests
{
    private static readonly Localizer English = new(CultureInfo.GetCultureInfo("en"));

    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    [Fact]
    public void Ui_list_starts_with_system_named_after_the_os_language()
    {
        // Arrange
        var os = C("ru-RU");

        // Act
        var options = LanguageOptions.ForUi(English, os);

        // Assert
        options[0].Value.Should().BeNull();
        options[0].Display.Should().Be("System (Русский)");
    }

    [Fact]
    public void Ui_system_option_names_the_language_the_app_will_actually_use()
    {
        // Arrange
        var os = C("de-DE");

        // Act
        var options = LanguageOptions.ForUi(English, os);

        // Assert
        options[0].Display.Should().Be("System (English)");
    }

    [Fact]
    public void Ui_list_has_every_supported_language_by_native_name_sorted()
    {
        // Arrange
        var os = C("en-US");

        // Act
        var options = LanguageOptions.ForUi(English, os);

        // Assert
        options.Skip(1).Select(o => o.Display).Should().Equal("English", "Русский");
        options.Skip(1).Select(o => o.Value).Should().BeEquivalentTo(UiLanguages.Supported);
    }

    [Fact]
    public void Game_list_starts_with_system_named_after_the_os_language()
    {
        // Arrange
        var os = C("pt-BR");

        // Act
        var options = LanguageOptions.ForGames(English, os);

        // Assert
        options[0].Value.Should().BeNull();
        options[0].Display.Should().Be($"System ({LanguageNames.Native("pt-BR")})");
    }

    [Fact]
    public void Game_list_has_every_game_language_by_native_name_sorted()
    {
        // Arrange
        var os = C("en-US");

        // Act
        var options = LanguageOptions.ForGames(English, os);

        // Assert
        var names = options.Skip(1).Select(o => o.Display).ToList();
        names.Should().BeInAscendingOrder(LanguageNames.Order);
        names.Should().Contain(["Deutsch", "English", "Русский", "Українська", "日本語"]);
        options.Skip(1).Select(o => o.Value).Should().BeEquivalentTo(GameLanguages.Tags);
    }

    [Fact]
    public void System_option_follows_the_ui_language()
    {
        // Arrange
        var localizer = new Localizer(C("en"));
        var options = LanguageOptions.ForUi(localizer, C("ru-RU"));

        // Act
        localizer.SetCulture(C("ru"));

        // Assert
        options[0].Display.Should().Be("Системный (Русский)");
    }
}
