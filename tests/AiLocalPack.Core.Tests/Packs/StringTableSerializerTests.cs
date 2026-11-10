using System.Text;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Packs;

public sealed class StringTableSerializerTests
{
    private static readonly StringTable Sample = new(
        "ru",
        new Dictionary<string, string> { ["Map001/ev1/p0"] = "Привет, {0}!", ["System/title"] = "Игра", ["empty"] = "" },
        new Dictionary<string, string> { ["System/title"] = "Main menu caption" })
    {
        Originals = new Dictionary<string, string> { ["System/title"] = OriginalHash.Of("Game"), ["empty"] = OriginalHash.Of("") },
    };

    [Fact]
    public void Compressed_table_reads_back_the_same()
    {
        // Arrange
        using var stream = new MemoryStream();
        StringTableSerializer.Write(Sample, stream);
        stream.Position = 0;

        // Act
        var result = StringTableSerializer.Read(stream);

        // Assert
        result.Value.Should().BeEquivalentTo(Sample);
    }

    [Fact]
    public void Written_table_is_gzip()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Act
        StringTableSerializer.Write(Sample, stream);

        // Assert
        stream.ToArray()[..2].Should().Equal(0x1F, 0x8B);
    }

    [Fact]
    public void Plain_json_table_is_read()
    {
        // Arrange
        var json = """{ "schemaVersion": 1, "language": "de", "strings": { "a": "Hallo" } }""";

        // Act
        var result = StringTableSerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        result.Value!.Strings.Should().Equal(new Dictionary<string, string> { ["a"] = "Hallo" });
    }

    [Fact]
    public void Written_table_passes_the_schema()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Act
        StringTableSerializer.Write(Sample, stream, compress: false);

        // Assert
        PackFixtures.SchemaErrors(PackFixtures.StringTableSchema, Encoding.UTF8.GetString(stream.ToArray())).Should().BeEmpty();
    }

    [Fact]
    public void The_original_hashes_read_back()
    {
        // Arrange
        using var stream = new MemoryStream();
        StringTableSerializer.Write(Sample, stream);
        stream.Position = 0;

        // Act
        var result = StringTableSerializer.Read(stream);

        // Assert
        result.Value!.Originals.Should().BeEquivalentTo(Sample.Originals);
    }

    [Fact]
    public void A_table_without_original_hashes_has_none()
    {
        // Arrange
        var json = """{ "schemaVersion": 1, "language": "de", "strings": { "a": "Hallo" } }""";

        // Act
        var result = StringTableSerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        result.Value!.Originals.Should().BeEmpty();
    }

    [Fact]
    public void Plain_table_keeps_non_ascii_text_readable()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Act
        StringTableSerializer.Write(Sample, stream, compress: false);

        // Assert
        Encoding.UTF8.GetString(stream.ToArray()).Should().Contain("Привет, {0}!");
    }

    [Fact]
    public void Unknown_future_fields_are_ignored()
    {
        // Arrange
        var json = """{ "schemaVersion": 1, "language": "de", "strings": { "a": "Hallo" }, "lengthLimits": { "a": 10 } }""";

        // Act
        var result = StringTableSerializer.Read(Encoding.UTF8.GetBytes(json));

        // Assert
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "strings": {} }""", "language", "is missing")]
    [InlineData("""{ "schemaVersion": 1, "language": "xx", "strings": {} }""", "language", "'xx' is not a supported language")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru" }""", "strings", "is missing")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "": "x" } }""", "strings", "has an empty key")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "a": null } }""", "strings[\"a\"]", "must be text")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "a": 5 } }""", "strings.a", "has the wrong type")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "a": "x" }, "context": { "b": "note" } }""", "context[\"b\"]", "has no translation")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "a": "x", "a": "y" } }""", "strings", "listed twice")]
    [InlineData("""{ "schemaVersion": 2, "language": "ru", "strings": {} }""", "schemaVersion", "newer version of the app")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "a": "x" }, "originals": { "b": "0123456789abcdef" } }""", "originals[\"b\"]", "has no translation")]
    [InlineData("""{ "schemaVersion": 1, "language": "ru", "strings": { "a": "x" }, "originals": { "a": "0123456789ABCDEF" } }""", "originals[\"a\"]", "16 lowercase hex digits")]
    public void Invalid_table_fails_with_the_expected_error(string json, string field, string message)
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes(json);

        // Act
        var result = StringTableSerializer.Read(data);

        // Assert
        result.Errors.Should().ContainSingle(e => e.Field.StartsWith(field, StringComparison.Ordinal) && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Broken_gzip_is_reported()
    {
        // Arrange
        byte[] data = [0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02, 0x03];

        // Act
        var result = StringTableSerializer.Read(data);

        // Assert
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("gzip");
    }
}
