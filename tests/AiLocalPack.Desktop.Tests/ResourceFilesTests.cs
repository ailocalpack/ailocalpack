using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.Tests;

/// <summary>Guards every resource file: same keys, same placeholders, complete plural forms in every UI language.</summary>
public sealed partial class ResourceFilesTests
{
    public static TheoryData<string> Translations => [.. UiLanguages.Supported.Where(l => l != UiLanguages.Fallback)];

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    /// <summary>Only the strings in <paramref name="language"/>'s own file, without fallback to English.</summary>
    private static Dictionary<string, string> Read(string language)
    {
        var culture = language == UiLanguages.Fallback ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resource file for '{language}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static HashSet<string> Placeholders(string value) =>
        [.. Placeholder().Matches(value).Select(m => m.Groups[1].Value)];

    private static IEnumerable<string> PlainKeys(Dictionary<string, string> strings) =>
        strings.Keys.Where(k => PluralRules.CategoryOf(k) is null);

    private static IEnumerable<string> PluralBases(Dictionary<string, string> strings) =>
        strings.Keys.Where(k => PluralRules.CategoryOf(k) is not null).Select(PluralRules.BaseKey).Distinct();

    [Theory]
    [MemberData(nameof(Translations))]
    public void Translation_has_exactly_the_english_keys(string language)
    {
        // Arrange
        var english = Read(UiLanguages.Fallback);

        // Act
        var translation = Read(language);

        // Assert
        PlainKeys(translation).Should().BeEquivalentTo(PlainKeys(english));
        PluralBases(translation).Should().BeEquivalentTo(PluralBases(english));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Translation_keeps_the_english_placeholders(string language)
    {
        // Arrange
        var english = Read(UiLanguages.Fallback);

        // Act
        var translation = Read(language);

        // Assert
        foreach (var (key, value) in translation)
        {
            var englishKey = PluralRules.CategoryOf(key) is null ? key : $"{PluralRules.BaseKey(key)}_{PluralCategory.Other}";
            english.Should().ContainKey(englishKey);
            Placeholders(value).Should().BeEquivalentTo(Placeholders(english[englishKey]), $"'{key}' in '{language}' must keep the placeholders");
        }
    }

    [Theory]
    [MemberData(nameof(UiLanguagesData))]
    public void Plural_keys_have_exactly_the_language_forms(string language)
    {
        // Arrange
        var expected = PluralRules.CategoriesFor(CultureInfo.GetCultureInfo(language));

        // Act
        var strings = Read(language);

        // Assert
        foreach (var baseKey in PluralBases(strings))
        {
            var forms = strings.Keys.Where(k => PluralRules.BaseKey(k) == baseKey && PluralRules.CategoryOf(k) is not null)
                .Select(k => PluralRules.CategoryOf(k)!.Value);
            forms.Should().BeEquivalentTo(expected, $"'{baseKey}' in '{language}' needs every plural form of the language");
        }
    }

    [Fact]
    public void English_plural_forms_share_placeholders()
    {
        // Arrange
        var english = Read(UiLanguages.Fallback);

        // Act
        var plurals = english.Where(e => PluralRules.CategoryOf(e.Key) is not null).ToList();

        // Assert
        foreach (var (key, value) in plurals)
        {
            Placeholders(value).Should().BeEquivalentTo(
                Placeholders(english[$"{PluralRules.BaseKey(key)}_{PluralCategory.Other}"]), $"'{key}' must match its other form");
        }
    }

    [Fact]
    public void Every_shipped_resource_file_is_a_supported_language()
    {
        // Arrange
        var output = Path.GetDirectoryName(typeof(Strings).Assembly.Location)!;

        // Act
        var shipped = Directory.GetDirectories(output)
            .Where(d => File.Exists(Path.Combine(d, "AiLocalPack.resources.dll")))
            .Select(Path.GetFileName);

        // Assert
        shipped.Should().BeEquivalentTo(UiLanguages.Supported.Where(l => l != UiLanguages.Fallback));
    }

    public static TheoryData<string> UiLanguagesData => [.. UiLanguages.Supported];
}
