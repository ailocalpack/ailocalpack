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
    [InlineData("nl-NL")]
    [InlineData("zh-TW")]
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
    [InlineData("nl")]
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

    [Theory]
    [InlineData("de-AT", "de-AT")]
    [InlineData("es-MX", "es-MX")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("pt-BR", "pt-BR")]
    public void Keeps_a_regional_culture_of_a_supported_language(string os, string expected)
    {
        // Arrange
        var osCulture = C(os);

        // Act
        var culture = UiLanguages.Resolve(null, osCulture);

        // Assert
        culture.Name.Should().Be(expected);
    }

    [Fact]
    public void Another_variant_of_a_supported_language_gets_the_supported_one()
    {
        // Arrange
        var os = C("pt-PT");

        // Act
        var culture = UiLanguages.Resolve(null, os);

        // Assert
        culture.Name.Should().Be("pt-BR");
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("uk-UA", "uk")]
    [InlineData("en-US", "en")]
    [InlineData("nl-NL", "en")]
    public void Tag_names_the_supported_language_a_culture_reads(string name, string expected)
    {
        // Arrange
        var culture = C(name);

        // Act
        var tag = UiLanguages.TagOf(culture);

        // Assert
        tag.Should().Be(expected);
    }
}
