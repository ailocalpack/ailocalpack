using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>What a game update changed against the base pack's string table (task 56).</summary>
public sealed class UpdateDiffTests
{
    private static StringTable Base { get; } = new StringTable("ru", new Dictionary<string, string>
    {
        ["same"] = "Зелье",
        ["changed"] = "Старый текст",
        ["gone"] = "Пропало",
        ["empty"] = "",
        ["nohash"] = "Без хэша",
    })
    {
        Originals = new Dictionary<string, string>
        {
            ["same"] = OriginalHash.Of("Potion"),
            ["changed"] = OriginalHash.Of("Old text"),
            ["gone"] = OriginalHash.Of("Lost"),
        },
    };

    private static IEnumerable<KeyValuePair<string, string>> Current(params (string Key, string Original)[] strings) =>
        strings.Select(s => KeyValuePair.Create(s.Key, s.Original));

    private static UpdateDiff Diff() => UpdateDiff.Of(
        Current(("new", "Ether"), ("same", "Potion"), ("changed", "New text"), ("empty", "Blank"), ("nohash", "Anything")),
        Base);

    [Fact]
    public void A_key_the_base_lacks_is_added()
    {
        // Act
        var diff = Diff();

        // Assert
        diff.Added.Should().Equal("new", "empty");
    }

    [Fact]
    public void A_key_whose_original_hash_differs_is_changed()
    {
        // Act
        var diff = Diff();

        // Assert
        diff.Changed.Should().Equal("changed");
    }

    [Fact]
    public void A_key_the_game_no_longer_has_is_removed()
    {
        // Act
        var diff = Diff();

        // Assert
        diff.Removed.Should().Equal("gone");
    }

    [Fact]
    public void A_key_with_the_same_original_or_no_hash_is_unchanged()
    {
        // Act
        var diff = Diff();

        // Assert
        diff.Unchanged.Should().Equal("same", "nohash");
    }

    [Fact]
    public void Only_added_and_changed_keys_are_translated()
    {
        // Act
        var diff = Diff();

        // Assert
        diff.ToTranslate.Should().BeEquivalentTo(["new", "empty", "changed"]);
    }

    [Fact]
    public void Seeding_adds_the_base_translations_of_unchanged_keys_only()
    {
        // Arrange
        var diff = Diff();

        // Act
        var seeded = diff.Seed(null, Base, "ru");

        // Assert
        seeded.Strings.Should().BeEquivalentTo(new Dictionary<string, string> { ["same"] = "Зелье", ["nohash"] = "Без хэша" });
    }

    [Fact]
    public void Seeding_keeps_what_the_store_already_has()
    {
        // Arrange
        var diff = Diff();
        var stored = new StringTable("ru", new Dictionary<string, string> { ["same"] = "Эликсир", ["new"] = "Эфир" });

        // Act
        var seeded = diff.Seed(stored, Base, "ru");

        // Assert
        seeded.Strings.Should().BeEquivalentTo(new Dictionary<string, string> { ["same"] = "Эликсир", ["new"] = "Эфир", ["nohash"] = "Без хэша" });
    }

    [Fact]
    public void Seeding_carries_the_base_notes_of_the_kept_keys()
    {
        // Arrange
        var withNotes = Base with { Context = new Dictionary<string, string> { ["same"] = "an item", ["changed"] = "stale" } };
        var diff = UpdateDiff.Of(Current(("same", "Potion"), ("changed", "New text")), withNotes);

        // Act
        var seeded = diff.Seed(null, withNotes, "ru");

        // Assert
        seeded.Context.Should().BeEquivalentTo(new Dictionary<string, string> { ["same"] = "an item" });
    }

    [Fact]
    public void The_original_hash_is_16_lowercase_hex_digits_of_the_sha256()
    {
        // Act
        var hash = OriginalHash.Of("Potion");

        // Assert
        new { hash, Valid = OriginalHash.IsValid(hash) }.Should().Be(new { hash = "41e4ae984c221ad6", Valid = true });
    }
}
