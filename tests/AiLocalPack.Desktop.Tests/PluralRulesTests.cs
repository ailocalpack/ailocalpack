using System.Globalization;
using AiLocalPack.Desktop.Localization;

namespace AiLocalPack.Desktop.Tests;

public sealed class PluralRulesTests
{
    [Theory]
    [InlineData(0, PluralCategory.Other)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Other)]
    [InlineData(5, PluralCategory.Other)]
    [InlineData(11, PluralCategory.Other)]
    [InlineData(21, PluralCategory.Other)]
    [InlineData(22, PluralCategory.Other)]
    public void English_has_one_and_other(long count, PluralCategory expected)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo("en-US");

        // Act
        var category = PluralRules.Select(culture, count);

        // Assert
        category.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, PluralCategory.Many)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(4, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(11, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(14, PluralCategory.Many)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(25, PluralCategory.Many)]
    [InlineData(101, PluralCategory.One)]
    [InlineData(111, PluralCategory.Many)]
    [InlineData(-1, PluralCategory.One)]
    public void Russian_has_one_few_many(long count, PluralCategory expected)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo("ru-RU");

        // Act
        var category = PluralRules.Select(culture, count);

        // Assert
        category.Should().Be(expected);
    }

    [Theory]
    [InlineData(1, PluralCategory.One)]
    [InlineData(3, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(21, PluralCategory.One)]
    [InlineData(111, PluralCategory.Many)]
    public void Ukrainian_follows_the_russian_rule(long count, PluralCategory expected)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo("uk-UA");

        // Act
        var category = PluralRules.Select(culture, count);

        // Assert
        category.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, PluralCategory.Many)]
    [InlineData(1, PluralCategory.One)]
    [InlineData(2, PluralCategory.Few)]
    [InlineData(5, PluralCategory.Many)]
    [InlineData(12, PluralCategory.Many)]
    [InlineData(21, PluralCategory.Many)]
    [InlineData(22, PluralCategory.Few)]
    [InlineData(112, PluralCategory.Many)]
    public void Polish_has_one_only_for_one(long count, PluralCategory expected)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo("pl-PL");

        // Act
        var category = PluralRules.Select(culture, count);

        // Assert
        category.Should().Be(expected);
    }

    [Theory]
    [InlineData("fr-FR", 0, PluralCategory.One)]
    [InlineData("fr-FR", 1, PluralCategory.One)]
    [InlineData("fr-FR", 2, PluralCategory.Other)]
    [InlineData("pt-BR", 0, PluralCategory.One)]
    [InlineData("es-ES", 0, PluralCategory.Other)]
    [InlineData("es-ES", 1, PluralCategory.One)]
    [InlineData("it-IT", 1, PluralCategory.One)]
    [InlineData("it-IT", 1_000_001, PluralCategory.Other)]
    [InlineData("fr-FR", 1_000_000, PluralCategory.Many)]
    [InlineData("es-ES", 3_000_000, PluralCategory.Many)]
    public void Romance_languages_have_many_for_exact_millions(string name, long count, PluralCategory expected)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo(name);

        // Act
        var category = PluralRules.Select(culture, count);

        // Assert
        category.Should().Be(expected);
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    [InlineData("zh-CN")]
    public void East_asian_languages_have_only_other(string name)
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo(name);

        // Act
        var categories = PluralRules.CategoriesFor(culture);

        // Assert
        categories.Should().Equal(PluralCategory.Other);
    }

    [Fact]
    public void Language_without_a_rule_uses_the_english_rule()
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo("nl-NL");

        // Act
        var categories = PluralRules.CategoriesFor(culture);

        // Assert
        categories.Should().Equal(PluralCategory.One, PluralCategory.Other);
    }

    [Theory]
    [InlineData("Library_GameCount_One", "Library_GameCount")]
    [InlineData("Library_GameCount_Few", "Library_GameCount")]
    [InlineData("Library_GameCount", "Library_GameCount")]
    [InlineData("Library_Title", "Library_Title")]
    public void Base_key_drops_the_plural_suffix(string key, string expected)
    {
        // Arrange
        var input = key;

        // Act
        var baseKey = PluralRules.BaseKey(input);

        // Assert
        baseKey.Should().Be(expected);
    }
}
