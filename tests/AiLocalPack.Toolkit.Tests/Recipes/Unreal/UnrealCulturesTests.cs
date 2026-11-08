using AiLocalPack.Toolkit.Recipes.Unreal;

namespace AiLocalPack.Toolkit.Tests.Recipes.Unreal;

/// <summary>Which of a game's cultures a string table's language replaces (task 37).</summary>
public sealed class UnrealCulturesTests
{
    [Theory]
    [InlineData("ru", "en,ru", "ru")]
    [InlineData("pt-BR", "en,PT-br", "PT-br")]
    [InlineData("zh-Hans", "en,zh-Hans,zh-CN", "zh-Hans")]
    public void The_same_culture_wins_whatever_its_case(string language, string cultures, string expected)
    {
        // Arrange
        var available = cultures.Split(',');

        // Act
        var picked = UnrealCultures.Pick(language, available);

        // Assert
        picked.Should().Be(expected);
    }

    [Theory]
    [InlineData("zh-Hans", "en,zh-TW,zh-CN", "zh-CN")]
    [InlineData("zh-Hant", "en,zh-CN,zh-TW", "zh-TW")]
    [InlineData("es-419", "en,es,es-MX", "es-MX")]
    [InlineData("es-ES", "en,es-MX,es", "es")]
    [InlineData("pt-PT", "en,pt-BR,pt", "pt")]
    [InlineData("nb", "en,no", "no")]
    public void A_language_store_tags_name_differently_finds_the_game_name(string language, string cultures, string expected)
    {
        // Arrange
        var available = cultures.Split(',');

        // Act
        var picked = UnrealCultures.Pick(language, available);

        // Assert
        picked.Should().Be(expected);
    }

    [Theory]
    [InlineData("pt-BR", "en,pt")]
    [InlineData("pt-PT", "en,pt-BR")]
    [InlineData("zh-Hant", "en,zh-CN,zh")]
    [InlineData("es-419", "en,es")]
    public void Another_variant_of_the_language_is_never_taken(string language, string cultures)
    {
        // Arrange
        var available = cultures.Split(',');

        // Act
        var picked = UnrealCultures.Pick(language, available);

        // Assert
        picked.Should().BeNull();
    }

    [Theory]
    [InlineData("ru", "en,ru-RU", "ru-RU")]
    [InlineData("fr", "en,fr-CA,fr-FR", "fr-FR")]
    [InlineData("ja", "en,ja-JP", "ja-JP")]
    public void A_bare_language_finds_its_regional_culture(string language, string cultures, string expected)
    {
        // Arrange
        var available = cultures.Split(',');

        // Act
        var picked = UnrealCultures.Pick(language, available);

        // Assert
        picked.Should().Be(expected);
    }

    [Fact]
    public void A_language_the_game_lacks_gives_none()
    {
        // Arrange
        string[] available = ["en", "fr", "de"];

        // Act
        var picked = UnrealCultures.Pick("ru", available);

        // Assert
        picked.Should().BeNull();
    }
}
