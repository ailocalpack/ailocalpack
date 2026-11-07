using AiLocalPack.Toolkit.Recipes.Godot;

namespace AiLocalPack.Toolkit.Tests.Recipes.Godot;

/// <summary><see cref="GodotTextScene"/>: texts of <c>.tscn</c> scenes (task 35).</summary>
public sealed class GodotTextSceneTests
{
    private const string Scene = """
        [gd_scene load_steps=2 format=2]

        [ext_resource path="res://theme.tres" type="Theme" id=1]

        [sub_resource type="StyleBoxFlat" id=1]
        text = "Not a node"

        [node name="Root" type="Control"]
        text = "Root text"

        [node name="Play" type="Button" parent="."]
        text = "Play \"now\"\nor \\later \u00e9"
        rect_min_size = Vector2( 10, 20 )
        hint_tooltip = "Line one
        line two"

        [node name="Panel" parent="." instance=ExtResource( 1 )]
        meta = {
        "x": "text = \"inside\""
        }
        text = "Instanced"

        [node name="Label" type="Label" parent="Panel/Box"]
        text = "123"
        name_text = "Not shown"
        placeholder_text = &"A name, not text"

        """;

    [Fact]
    public void Read_gives_the_texts_of_node_sections_with_their_paths()
    {
        // Act
        var texts = GodotTextScene.Read(Scene);

        // Assert
        texts.Select(t => (t.Node, t.Type, t.Property, t.Value)).Should().Equal(
            (".", "Control", "text", "Root text"),
            ("Play", "Button", "text", "Play \"now\"\nor \\later é"),
            ("Play", "Button", "hint_tooltip", "Line one\nline two"),
            ("Panel", null, "text", "Instanced"));
    }

    [Fact]
    public void Replace_writes_only_the_chosen_literals()
    {
        // Arrange
        var texts = GodotTextScene.Read(Scene);

        // Act
        var replaced = GodotTextScene.Replace(Scene, [(texts[1], "Играть \"сейчас\"\\потом"), (texts[3], "Вставка")]);

        // Assert
        replaced.Should().Be(Scene
            .Replace("text = \"Play \\\"now\\\"\\nor \\\\later \\u00e9\"", "text = \"Играть \\\"сейчас\\\"\\\\потом\"", StringComparison.Ordinal)
            .Replace("text = \"Instanced\"", "text = \"Вставка\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Replaced_scene_reads_back_the_new_texts()
    {
        // Arrange
        var texts = GodotTextScene.Read(Scene);

        // Act
        var reread = GodotTextScene.Read(GodotTextScene.Replace(Scene, [(texts[2], "Строка\nдва \"с кавычками\"")]));

        // Assert
        reread[2].Value.Should().Be("Строка\nдва \"с кавычками\"");
    }

    [Fact]
    public void Quote_escapes_backslashes_and_quotes_and_keeps_line_breaks()
    {
        // Act
        var literal = GodotTextScene.Quote("a\\b \"c\"\nd");

        // Assert
        literal.Should().Be("\"a\\\\b \\\"c\\\"\nd\"");
    }
}
