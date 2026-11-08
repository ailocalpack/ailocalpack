using System.Text;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Generation;

public sealed class GlossaryTests
{
    [Fact]
    public void Written_glossary_reads_back_the_same()
    {
        // Arrange
        var glossary = new Glossary("ru", "на ты", [
            new GlossaryEntry("Harold", "Гарольд", "the hero", "noun", "masculine"),
            new GlossaryEntry("Ash", "Ash", KeepAsIs: true),
        ]);

        // Act
        var (read, _) = GlossarySerializer.Read(GlossarySerializer.Write(glossary));

        // Assert
        read.Should().BeEquivalentTo(glossary);
    }

    [Fact]
    public void Written_glossary_keeps_non_ascii_unescaped()
    {
        // Arrange
        var glossary = new Glossary("ru", null, [new GlossaryEntry("Potion", "Зелье")]);

        // Act
        var text = Encoding.UTF8.GetString(GlossarySerializer.Write(glossary));

        // Assert
        text.Should().Contain("\"Зелье\"");
    }

    [Fact]
    public void Keep_as_is_entry_without_translation_reads_as_the_term()
    {
        // Arrange
        var json = """{"schemaVersion": 1, "language": "ru", "entries": [{"term": "Ash", "keepAsIs": true}]}""";

        // Act
        var (read, _) = GlossarySerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        read!.Entries.Should().Equal(new GlossaryEntry("Ash", "Ash", KeepAsIs: true));
    }

    [Theory]
    [InlineData("""{"schemaVersion": 2, "language": "ru", "entries": []}""", "schemaVersion 2 is newer than this app reads; update the app")]
    [InlineData("""{"schemaVersion": 1, "language": "xx", "entries": []}""", "language 'xx' is not a supported language tag")]
    [InlineData("""{"schemaVersion": 1, "language": "ru", "entries": [{"translation": "x"}]}""", "an entry has no term")]
    [InlineData("""{"schemaVersion": 1, "language": "ru", "entries": [{"term": "A", "translation": "x"}, {"term": "a", "translation": "y"}]}""", "term 'a' is listed twice")]
    [InlineData("""{"schemaVersion": 1, "language": "ru", "entries": [{"term": "A"}]}""", "term 'A' has no translation")]
    [InlineData("not json", null)]
    public void Invalid_glossary_is_refused_with_the_reason(string json, string? reason)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(json);

        // Act
        var (read, error) = GlossarySerializer.Read(data);

        // Assert
        (read, error is not null && (reason is null || error == reason)).Should().Be((null, true));
    }

    [Fact]
    public void Files_without_a_store_read_as_nothing()
    {
        // Arrange
        using var dir = new TempDir();
        var files = new GenerationFiles(dir.Path);

        // Act
        var strings = files.ReadStrings();

        // Assert
        strings.Should().BeNull();
    }

    [Fact]
    public void Files_store_strings_as_plain_json()
    {
        // Arrange
        using var dir = new TempDir();
        var files = new GenerationFiles(dir.Path);

        // Act
        files.WriteStrings(new StringTable("ru", new Dictionary<string, string> { ["k"] = "Зелье" }));

        // Assert
        File.ReadAllText(files.StringsPath).Should().Contain("\"k\": \"Зелье\"");
    }

    [Fact]
    public void Files_with_a_broken_glossary_throw_naming_the_file()
    {
        // Arrange
        using var dir = new TempDir().File("glossary.json", "{");
        var files = new GenerationFiles(dir.Path);

        // Act
        var act = () => files.ReadGlossary();

        // Assert
        act.Should().Throw<GenerationFileException>().WithMessage("glossary.json: not valid JSON*");
    }

    [Fact]
    public void Files_with_a_broken_store_throw_naming_the_file()
    {
        // Arrange
        using var dir = new TempDir().File("strings.json", """{"schemaVersion": 1}""");
        var files = new GenerationFiles(dir.Path);

        // Act
        var act = () => files.ReadStrings();

        // Assert
        act.Should().Throw<GenerationFileException>().WithMessage("strings.json: *");
    }
}
