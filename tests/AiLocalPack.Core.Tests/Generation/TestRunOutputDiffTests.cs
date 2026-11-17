using AiLocalPack.Core.Generation;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Where a crashed translated game's output parted from the untranslated game's.</summary>
public sealed class TestRunOutputDiffTests
{
    private static readonly string[] Original =
    [
        "[20-50-01] Data path is ./",
        "[20-50-06] Barony version: v5.0.2",
        "[20-50-06] read json file .//data/status_effects.json",
        "[20-50-06] read json file .//data/follower_wheel.json",
        "[20-50-06] read json file .//data/monster_data.json",
        "[20-50-07] loading spells...",
    ];

    [Fact]
    public void The_translation_stopped_after_the_last_line_both_printed()
    {
        // Arrange
        string[] translated = ["[20-57-00] Data path is ./", "[20-57-06] Barony version: v5.0.2", "[20-57-06] read json file .//data/status_effects.json", "[20-57-06] read json file .//data/follower_wheel.json"];

        // Act
        var diff = TestRunOutputDiff.Compare(Original, translated);

        // Assert
        new { diff!.LastShared, diff.OriginalNext }.Should().BeEquivalentTo(new
        {
            LastShared = "[20-57-06] read json file .//data/follower_wheel.json",
            OriginalNext = new[] { "[20-50-06] read json file .//data/monster_data.json", "[20-50-07] loading spells..." },
        });
    }

    [Fact]
    public void Lines_only_the_translation_printed_are_kept()
    {
        // Arrange
        string[] translated = ["[20-57-00] Data path is ./", "[20-57-06] error: string too long in data/follower_wheel.json"];

        // Act
        var diff = TestRunOutputDiff.Compare(Original, translated);

        // Assert
        diff!.OnlyTranslated.Should().Equal("[20-57-06] error: string too long in data/follower_wheel.json");
    }

    [Fact]
    public void The_description_names_what_the_untranslated_game_did_next()
    {
        // Arrange
        string[] translated = ["[20-57-06] read json file .//data/follower_wheel.json"];

        // Act
        var text = TestRunOutputDiff.Compare(Original, translated)!.Describe();

        // Assert
        text.Should().Contain("went on with \"[20-50-06] read json file .//data/monster_data.json\"");
    }

    [Fact]
    public void Nothing_printed_by_the_untranslated_game_is_nothing_to_compare()
    {
        // Arrange
        string[] translated = ["something"];

        // Act
        var diff = TestRunOutputDiff.Compare([], translated);

        // Assert
        diff.Should().BeNull();
    }
}
