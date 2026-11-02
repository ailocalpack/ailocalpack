using System.Globalization;
using AiLocalPack.Core.Languages;

namespace AiLocalPack.Core.Tests.Languages;

public sealed class LanguageNamesTests
{
    [Theory]
    [InlineData("ru", "Русский")]
    [InlineData("en", "English")]
    [InlineData("de", "Deutsch")]
    [InlineData("cs", "Čeština")]
    [InlineData("ja", "日本語")]
    public void Native_name_is_in_the_language_itself_and_capitalized(string tag, string expected)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo(tag);

        // Act
        var name = LanguageNames.Native(culture);

        // Assert
        name.Should().Be(expected);
    }

    [Fact]
    public void Native_name_keeps_the_region()
    {
        // Arrange
        const string tag = "pt-BR";

        // Act
        var name = LanguageNames.Native(tag);

        // Assert
        name.Should().StartWith("Português").And.Contain("Brasil");
    }

    [Fact]
    public void Unknown_tag_is_shown_as_is()
    {
        // Arrange
        const string tag = "xx-unknown";

        // Act
        var name = LanguageNames.Native(tag);

        // Assert
        name.Should().Be(tag);
    }
}
