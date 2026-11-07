using System.Text;
using AiLocalPack.Toolkit.Recipes.Godot;

namespace AiLocalPack.Toolkit.Tests.Recipes.Godot;

/// <summary><see cref="GodotTranslation"/> and <see cref="GodotResource"/>: plain and compressed translations of Godot 3 and 4 (task 35).</summary>
public sealed class GodotTranslationTests
{
    private static readonly KeyValuePair<string, string>[] Messages =
    [
        new("MENU_PLAY", "Play"),
        new("INTRO", "In the beginning there was the word, and the word was with the hero."),
        new("CAFE", "Café «Ёлка»"),
        new("EMPTY", ""),
    ];

    private static Dictionary<string, string> ByKey(GodotTranslation translation) =>
        translation.Messages.ToDictionary(m => m.Key ?? m.Id, m => m.Text);

    private static IEnumerable<byte[]> Candidates(params string[] texts) => texts.Select(Encoding.UTF8.GetBytes);

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Plain_translation_reads_keys_and_texts(uint major)
    {
        // Arrange
        var file = GodotTranslation.Create(major, "de", Messages, hashed: false);

        // Act
        var translation = GodotTranslation.Read(file);

        // Assert
        (translation.Locale, translation.IsHashed, ByKey(translation)).Should().BeEquivalentTo(("de", false, Messages.ToDictionary()));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Compressed_translation_reads_every_text_compressed_or_not(uint major)
    {
        // Arrange
        var file = GodotTranslation.Create(major, "en", Messages, hashed: true);

        // Act
        var translation = GodotTranslation.Read(file);

        // Assert
        (translation.IsHashed, translation.Messages.Select(m => m.Text)).Should().BeEquivalentTo((true, Messages.Select(m => m.Value)));
    }

    [Fact]
    public void Compressed_translation_knows_no_key_until_recovered()
    {
        // Arrange
        var file = GodotTranslation.Create(3, "en", Messages, hashed: true);

        // Act
        var translation = GodotTranslation.Read(file);

        // Assert
        translation.Messages.Should().AllSatisfy(m => m.Key.Should().BeNull());
    }

    [Fact]
    public void Locale_left_out_means_English()
    {
        // Arrange
        var file = GodotTranslation.Create(3, "en", Messages, hashed: true);

        // Act
        var translation = GodotTranslation.Read(file);

        // Assert
        translation.Locale.Should().Be("en");
    }

    [Fact]
    public void Recover_names_the_messages_whose_keys_are_among_the_candidates()
    {
        // Arrange
        var translation = GodotTranslation.Read(GodotTranslation.Create(4, "en", Messages, hashed: true));

        // Act
        var found = translation.Recover(Candidates("MENU_PLAY", "CAFE", "NOT_A_KEY", "menu_play"));

        // Assert
        (found, translation.Messages.Where(m => m.Key is not null).ToDictionary(m => m.Key!, m => m.Text))
            .Should().BeEquivalentTo((2, new Dictionary<string, string> { ["MENU_PLAY"] = "Play", ["CAFE"] = "Café «Ёлка»" }));
    }

    [Fact]
    public void Recover_finds_a_non_ASCII_key()
    {
        // Arrange
        var translation = GodotTranslation.Read(GodotTranslation.Create(3, "en", [new("ПРИВЕТ", "Hello")], hashed: true));

        // Act
        var found = translation.Recover(Candidates("ПРИВЕТ"));

        // Assert
        (found, translation.Messages.Single().Key).Should().Be((1, "ПРИВЕТ"));
    }

    [Fact]
    public void Find_looks_a_key_up_by_its_hash()
    {
        // Arrange
        var translation = GodotTranslation.Read(GodotTranslation.Create(3, "en", Messages, hashed: true));

        // Act
        var message = translation.Find("INTRO");

        // Assert
        message!.Text.Should().Be(Messages[1].Value);
    }

    [Fact]
    public void Find_of_a_missing_key_gives_null()
    {
        // Arrange
        var translation = GodotTranslation.Read(GodotTranslation.Create(3, "en", Messages, hashed: true));

        // Act
        var message = translation.Find("NOT_THERE");

        // Assert
        message.Should().BeNull();
    }

    [Theory]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    public void Write_replaces_the_chosen_texts_and_keeps_the_rest(uint major, bool hashed)
    {
        // Arrange
        var translation = GodotTranslation.Read(GodotTranslation.Create(major, "en", Messages, hashed));
        var play = translation.Find("MENU_PLAY")!;
        var intro = translation.Find("INTRO")!;

        // Act
        var reread = GodotTranslation.Read(translation.Write(new Dictionary<string, string> { [play.Id] = "Играть", [intro.Id] = "В начале было слово." }));

        // Assert
        (reread.Find("MENU_PLAY")!.Text, reread.Find("INTRO")!.Text, reread.Find("CAFE")!.Text, reread.Find("EMPTY")!.Text, reread.Locale)
            .Should().Be(("Играть", "В начале было слово.", "Café «Ёлка»", string.Empty, "en"));
    }

    [Fact]
    public void Write_keeps_the_dictionary_values_string_names_in_Godot_4()
    {
        // Arrange
        var translation = GodotTranslation.Read(GodotTranslation.Create(4, "en", Messages, hashed: false));

        // Act
        var written = GodotResource.Read(translation.Write(new Dictionary<string, string> { ["MENU_PLAY"] = "Играть" }));

        // Assert
        ((GodotDictionary)written.Main.Property("messages")!.Value).Entries.Select(e => ((GodotString)e.Value).IsName).Should().AllBeEquivalentTo(true);
    }

    [Fact]
    public void A_resource_of_another_class_is_unsupported()
    {
        // Arrange
        var file = GodotResource.Create(3, "Theme", [("default_font_size", [3, 0, 0, 0, 16, 0, 0, 0])]);

        // Act
        var act = () => GodotTranslation.Read(file);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Unsupported);
    }

    [Fact]
    public void Compressed_resource_file_is_unsupported()
    {
        // Arrange
        byte[] file = [.. "RSCC"u8, 0, 0, 0, 0, 0, 16, 0, 0];

        // Act
        var act = () => GodotResource.Read(file);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Unsupported);
    }

    [Fact]
    public void Resource_cut_short_is_corrupt()
    {
        // Arrange
        var file = GodotTranslation.Create(3, "en", Messages, hashed: true);

        // Act
        var act = () => GodotResource.Read(file[..(file.Length / 2)]);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Corrupt);
    }

    [Fact]
    public void Text_pointing_past_the_string_data_is_corrupt()
    {
        // Arrange
        var resource = GodotResource.Read(GodotTranslation.Create(3, "en", Messages, hashed: true));
        var buckets = (GodotInt32Array)resource.Main.Property("bucket_table")!.Value;
        var broken = buckets.Items.ToArray();
        broken[3] = 1_000_000; // first message's offset
        var file = resource.Write(new Dictionary<string, byte[]> { ["bucket_table"] = new GodotInt32Array(broken).Encode() });

        // Act
        var act = () => GodotTranslation.Read(file);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Corrupt);
    }

    [Fact]
    public void Resource_write_without_changes_gives_the_same_bytes()
    {
        // Arrange
        var file = GodotTranslation.Create(4, "fr", Messages, hashed: true);

        // Act
        var written = GodotResource.Read(file).Write(new Dictionary<string, byte[]>());

        // Assert
        written.Should().Equal(file);
    }

    [Fact]
    public void Resource_reads_values_of_other_kinds_it_skips()
    {
        // Arrange
        byte[] vector2 = [10, 0, 0, 0, .. new byte[8]];
        byte[] color = [20, 0, 0, 0, .. new byte[16]];
        byte[] nodePath = [22, 0, 0, 0, 1, 0, 1, 0x80, 0x03, 0, 0, 0x80, (byte)'a', (byte)'b', 0, 0x02, 0, 0, 0x80, (byte)'x', 0];
        byte[] array = [30, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        var file = GodotResource.Create(4, "Translation", [("a", vector2), ("b", color), ("c", nodePath), ("d", array), ("locale", new GodotString("pl").Encode())]);

        // Act
        var resource = GodotResource.Read(file);

        // Assert
        resource.Main.Property("locale")!.Value.Should().Be(new GodotString("pl"));
    }

    [Fact]
    public void Smaz_decodes_codebook_entries_and_verbatim_runs()
    {
        // Arrange
        byte[] compressed = [1, 0, 254, (byte)'Z', 255, 2, (byte)'a', (byte)'b', (byte)'c', 253];

        // Act
        var text = Encoding.ASCII.GetString(Smaz.Decompress(compressed, 64));

        // Assert
        text.Should().Be("the Zabc.com");
    }

    [Fact]
    public void Smaz_output_longer_than_the_stored_size_is_corrupt()
    {
        // Arrange
        byte[] compressed = [1, 1, 1];

        // Act
        var act = () => Smaz.Decompress(compressed, 4);

        // Assert
        act.Should().Throw<GodotFormatException>().Which.Problem.Should().Be(GodotProblem.Corrupt);
    }

    [Fact]
    public void Smaz_compress_round_trips()
    {
        // Arrange
        var text = Encoding.UTF8.GetBytes("There is the word, and it was the end of their story. Ёж\0");

        // Act
        var restored = Smaz.Decompress(Smaz.Compress(text), text.Length);

        // Assert
        restored.Should().Equal(text);
    }
}
