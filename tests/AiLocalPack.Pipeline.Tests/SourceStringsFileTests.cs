using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Pipeline.Tests;

/// <summary><c>source.json</c>, the Extract stage's result (task 49).</summary>
public sealed class SourceStringsFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private GenerationFiles Files => new(_dir.Path);

    private static SourceStrings Sample => new(
        new EngineKind(GameEngine.RenPy),
        "renpy",
        new Version(1, 0, 0),
        ["game/script.rpyc"],
        [
            new ExtractedString("say:a", "Hi, [name]!", "Narration", null, ["[name]"]),
            new ExtractedString("string:b", "Start", null, 12, []) { MaxLengthUnit = LengthUnit.DisplayWidth },
        ]);

    [Fact]
    public void Written_strings_read_back_the_same()
    {
        // Arrange
        SourceStringsFile.Write(Files, Sample);

        // Act
        var read = SourceStringsFile.Read(Files)!;

        // Assert
        read.Should().BeEquivalentTo(Sample);
    }

    [Fact]
    public void No_file_reads_as_nothing()
    {
        // Arrange
        var files = Files;

        // Act
        var read = SourceStringsFile.Read(files);

        // Assert
        read.Should().BeNull();
    }

    [Fact]
    public void A_damaged_file_is_refused_with_its_name()
    {
        // Arrange
        _dir.File("source.json", """{"schemaVersion":1,"engine":"renpy","tool":"renpy","toolVersion":"1.0.0","strings":[{"key":"a"}]}""");

        // Act
        var act = () => SourceStringsFile.Read(Files);

        // Assert
        act.Should().Throw<GenerationFileException>().WithMessage("source.json: *");
    }
}
