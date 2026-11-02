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

    [Fact]
    public void Language_without_a_rule_uses_the_english_rule()
    {
        // Arrange
        var culture = CultureInfo.GetCultureInfo("de-DE");

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
