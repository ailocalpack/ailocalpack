using System.Globalization;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Tests.Languages;

public sealed class GameLanguagesTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("de-AT", "de")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-AO", "pt-PT")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es", "es-ES")]
    [InlineData("es-MX", "es-419")]
    [InlineData("nb-NO", "nb")]
    public void Follows_the_os_language(string os, string expected)
    {
        // Arrange
        var culture = C(os);

        // Act
        var tag = GameLanguages.Resolve(null, culture);

        // Assert
        tag.Should().Be(expected);
    }

    [Fact]
    public void Setting_wins_over_the_os_language()
    {
        // Arrange
        var os = C("ru-RU");

        // Act
        var tag = GameLanguages.Resolve("uk", os);

        // Assert
        tag.Should().Be("uk");
    }

    [Fact]
    public void Setting_is_matched_case_insensitively_and_returned_in_canonical_case()
    {
        // Arrange
        var os = C("en-US");

        // Act
        var tag = GameLanguages.Resolve("PT-br", os);

        // Assert
        tag.Should().Be("pt-BR");
    }

    [Theory]
    [InlineData("xx-unknown")]
    [InlineData("  ")]
    [InlineData("eo")]
    public void An_unknown_setting_falls_back_to_the_os_language(string setting)
    {
        // Arrange
        var os = C("de-DE");

        // Act
        var tag = GameLanguages.Resolve(setting, os);

        // Assert
        tag.Should().Be("de");
    }

    [Theory]
    [InlineData("sw-KE")]
    [InlineData("")]
    public void Falls_back_to_english_when_the_os_language_is_not_supported(string os)
    {
        // Arrange
        var culture = C(os);

        // Act
        var tag = GameLanguages.Resolve(null, culture);

        // Assert
        tag.Should().Be(GameLanguages.Fallback);
    }

    [Fact]
    public void Every_tag_is_a_culture_dotnet_knows()
    {
        // Arrange
        var tags = GameLanguages.Tags;

        // Act
        var unknown = tags.Where(t => CultureInfo.GetCultureInfo(t, predefinedOnly: true).Name != t).ToList();

        // Assert
        unknown.Should().BeEmpty();
        tags.Should().OnlyHaveUniqueItems().And.Contain(GameLanguages.Fallback);
    }
}
