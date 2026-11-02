using System.Globalization;
using AiLocalPack.Desktop.Localization;

namespace AiLocalPack.Desktop.Tests;

public sealed class UiLanguagesTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    [Fact]
    public void Follows_the_os_language_and_keeps_its_region()
    {
        // Arrange
        var os = C("ru-RU");

        // Act
        var culture = UiLanguages.Resolve(null, os);

        // Assert
        culture.Name.Should().Be("ru-RU");
    }

    [Fact]
    public void Setting_wins_over_the_os_language()
    {
        // Arrange
        var os = C("ru-RU");

        // Act
        var culture = UiLanguages.Resolve("en", os);

        // Assert
        culture.Name.Should().Be("en");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja")]
    [InlineData("")]
    public void Falls_back_to_english_for_an_unsupported_os_language(string os)
    {
        // Arrange
        var osCulture = os.Length == 0 ? CultureInfo.InvariantCulture : C(os);

        // Act
        var culture = UiLanguages.Resolve(null, osCulture);

        // Assert
        culture.Name.Should().Be("en");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("not a language")]
    [InlineData("  ")]
    public void Ignores_an_unsupported_or_invalid_setting(string setting)
    {
        // Arrange
        var os = C("ru-RU");

        // Act
        var culture = UiLanguages.Resolve(setting, os);

        // Assert
        culture.Name.Should().Be("ru-RU");
    }
}
