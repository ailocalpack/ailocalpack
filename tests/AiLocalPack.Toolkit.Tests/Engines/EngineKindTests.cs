using AiLocalPack.Toolkit.Engines;

namespace AiLocalPack.Toolkit.Tests.Engines;

public sealed class EngineKindTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("unity")]
    [InlineData("unity:mono")]
    [InlineData("unity:il2cpp")]
    [InlineData("unreal:4")]
    [InlineData("unreal:5")]
    [InlineData("rpgmaker:mv")]
    [InlineData("rpgmaker:mz")]
    [InlineData("renpy")]
    [InlineData("godot:3")]
    [InlineData("godot:4")]
    public void Id_round_trips(string id)
    {
        // Arrange
        EngineKind.TryParse(id, out var kind);

        // Act
        var text = kind?.ToString();

        // Assert
        text.Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cryengine")]
    [InlineData("Unity")]
    [InlineData("unity:")]
    [InlineData("unity:5")]
    [InlineData("renpy:8")]
    [InlineData("godot:04")]
    [InlineData("godot:0")]
    [InlineData("unknown:mono")]
    public void Invalid_id_is_rejected(string? id)
    {
        // Arrange
        var text = id;

        // Act
        var parsed = EngineKind.TryParse(text, out _);

        // Assert
        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData("unity:il2cpp", "Unity (IL2CPP)")]
    [InlineData("unity", "Unity")]
    [InlineData("unreal:5", "Unreal Engine 5")]
    [InlineData("rpgmaker:mv", "RPG Maker MV")]
    [InlineData("renpy", "Ren'Py")]
    [InlineData("godot:4", "Godot 4")]
    [InlineData("unknown", "Unknown")]
    public void Display_name_reads_like_the_engine_brand(string id, string name)
    {
        // Arrange
        EngineKind.TryParse(id, out var kind);

        // Act
        var display = kind!.DisplayName;

        // Assert
        display.Should().Be(name);
    }

    [Fact]
    public void Variant_of_another_engine_is_rejected()
    {
        // Arrange
        var act = () => new EngineKind(GameEngine.Unreal, EngineVariants.Mono);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Kinds_with_same_engine_and_variant_are_equal()
    {
        // Arrange
        var a = new EngineKind(GameEngine.Unity, EngineVariants.Mono);

        // Act
        var b = new EngineKind(GameEngine.Unity, EngineVariants.Mono);

        // Assert
        a.Should().Be(b);
    }
}
