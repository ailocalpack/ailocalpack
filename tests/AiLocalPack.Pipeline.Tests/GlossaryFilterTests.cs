using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The glossary entries a chunk gets: the ones its originals mention (task 50).</summary>
public sealed class GlossaryFilterTests
{
    private static readonly Glossary Terms = new(
        "ru",
        "informal",
        [new GlossaryEntry("Potion", "Зелье"), new GlossaryEntry("Harold", "Гарольд"), new GlossaryEntry("HP", "HP", KeepAsIs: true)]);

    [Theory]
    [InlineData("Drink a potion.", "Potion", true)]
    [InlineData("Three Potions left", "Potion", true)]
    [InlineData("Harold's sword", "Harold", true)]
    [InlineData("A Hipotion", "Potion", false)]
    [InlineData("Shop", "HP", false)]
    [InlineData("回復薬を飲む", "回復薬", true)]
    [InlineData("この回復薬", "回復薬", true)]
    [InlineData("", "Potion", false)]
    public void A_term_counts_when_it_starts_a_word_or_sits_in_unspaced_text(string text, string term, bool expected)
    {
        // Arrange
        var original = text;

        // Act
        var mentions = GlossaryFilter.Mentions(original, term);

        // Assert
        mentions.Should().Be(expected);
    }

    [Fact]
    public void Only_entries_the_originals_mention_are_kept_in_glossary_order()
    {
        // Arrange
        string[] originals = ["HP restored by 50.", "Harold drinks a Potion."];

        // Act
        var entries = GlossaryFilter.Relevant(Terms, originals);

        // Assert
        entries.Select(e => e.Term).Should().Equal("Potion", "Harold", "HP");
    }

    [Fact]
    public void Originals_mentioning_no_term_get_no_entries()
    {
        // Arrange
        string[] originals = ["Save the game?"];

        // Act
        var entries = GlossaryFilter.Relevant(Terms, originals);

        // Assert
        entries.Should().BeEmpty();
    }

    [Fact]
    public void Without_a_glossary_there_are_no_entries()
    {
        // Arrange
        string[] originals = ["Potion"];

        // Act
        var entries = GlossaryFilter.Relevant(null, originals);

        // Assert
        entries.Should().BeEmpty();
    }
}
