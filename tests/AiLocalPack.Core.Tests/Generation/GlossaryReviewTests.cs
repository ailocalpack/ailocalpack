using System.Text;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>The player's edits of a glossary in the review (task 54).</summary>
public sealed class GlossaryReviewTests
{
    private static readonly Glossary Drafted = new("ru", "на ты, игриво", [
        new GlossaryEntry("Harold", "Гарольд", "the hero"),
        new GlossaryEntry("Potion", "Зелье"),
        new GlossaryEntry("Ash", "Ash", KeepAsIs: true),
    ]);

    [Fact]
    public void Edited_entry_and_form_of_address_read_back_the_same()
    {
        // Arrange
        var glossary = Drafted with
        {
            Entries = [new GlossaryEntry("Potion", "Эликсир", Edited: true)],
            FormOfAddress = FormOfAddress.Formal,
        };

        // Act
        var (read, _) = GlossarySerializer.Read(GlossarySerializer.Write(glossary));

        // Assert
        read.Should().BeEquivalentTo(glossary);
    }

    [Fact]
    public void Unknown_form_of_address_reads_as_none()
    {
        // Arrange
        var json = """{"schemaVersion": 1, "language": "ru", "formOfAddress": "royal", "entries": []}""";

        // Act
        var (read, _) = GlossarySerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        read!.FormOfAddress.Should().BeNull();
    }

    [Fact]
    public void A_changed_translation_marks_the_entry_edited()
    {
        // Act
        var glossary = GlossaryReview.SetTranslation(Drafted, "potion", "  Эликсир ");

        // Assert
        glossary.Entries[1].Should().Be(new GlossaryEntry("Potion", "Эликсир", Edited: true));
    }

    [Fact]
    public void The_same_translation_changes_nothing()
    {
        // Act
        var glossary = GlossaryReview.SetTranslation(Drafted, "Potion", "Зелье");

        // Assert
        glossary.Should().BeSameAs(Drafted);
    }

    [Fact]
    public void A_translation_of_a_kept_term_turns_keep_as_is_off()
    {
        // Act
        var glossary = GlossaryReview.SetTranslation(Drafted, "Ash", "Эш");

        // Assert
        glossary.Entries[2].Should().Be(new GlossaryEntry("Ash", "Эш", Edited: true));
    }

    [Fact]
    public void A_translation_equal_to_the_term_keeps_it_as_is()
    {
        // Act
        var glossary = GlossaryReview.SetTranslation(Drafted, "Harold", "Harold");

        // Assert
        glossary.Entries[0].Should().Be(new GlossaryEntry("Harold", "Harold", "the hero", KeepAsIs: true, Edited: true));
    }

    [Fact]
    public void A_blank_translation_is_refused()
    {
        // Act
        var act = () => GlossaryReview.SetTranslation(Drafted, "Harold", "  ");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Regenerate_keeps_only_the_edited_entries_and_the_players_form_of_address()
    {
        // Arrange
        var glossary = GlossaryReview.SetFormOfAddress(GlossaryReview.SetTranslation(Drafted, "Potion", "Эликсир"), FormOfAddress.Formal);

        // Act
        var left = GlossaryReview.ForRegenerate(glossary);

        // Assert
        left.Should().BeEquivalentTo(new Glossary("ru", null, [new GlossaryEntry("Potion", "Эликсир", Edited: true)], FormOfAddress.Formal));
    }

    [Fact]
    public void Only_edited_entries_are_not_the_agents_part()
    {
        // Arrange
        var glossary = new Glossary("ru", null, [new GlossaryEntry("Potion", "Эликсир", Edited: true)]);

        // Act
        var has = GlossaryReview.HasAgentPart(glossary);

        // Assert
        has.Should().BeFalse();
    }

    [Fact]
    public void The_prompt_tone_adds_the_players_form_of_address_with_its_pronoun()
    {
        // Arrange
        var glossary = GlossaryReview.SetFormOfAddress(Drafted, FormOfAddress.Formal);

        // Act
        var tone = GlossaryReview.ToneForPrompt(glossary);

        // Assert
        tone.Should().Be("на ты, игриво. Form of address chosen by the player: formal (вы); it overrides anything above.");
    }

    [Fact]
    public void The_prompt_tone_is_the_agents_without_a_choice()
    {
        // Act
        var tone = GlossaryReview.ToneForPrompt(Drafted);

        // Assert
        tone.Should().Be("на ты, игриво");
    }

    [Fact]
    public void A_language_without_the_pair_names_the_form_only()
    {
        // Arrange
        var glossary = new Glossary("ja", null, [], FormOfAddress.Informal);

        // Act
        var tone = GlossaryReview.ToneForPrompt(glossary);

        // Assert
        tone.Should().Be("Form of address chosen by the player: informal; it overrides anything above.");
    }
}
