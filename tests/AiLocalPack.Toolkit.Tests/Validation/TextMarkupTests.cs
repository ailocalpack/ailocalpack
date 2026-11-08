using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Validation;

namespace AiLocalPack.Toolkit.Tests.Validation;

public sealed class TextMarkupTests
{
    private static TextMarkup Markup(string tool) => RecipeTools.Builtin.Find(tool)!.Markup;

    [Theory]
    [InlineData("renpy", "{b}", MarkupTokenKind.Open, "b")]
    [InlineData("renpy", "{/b}", MarkupTokenKind.Close, "b")]
    [InlineData("renpy", "{color=#f00}", MarkupTokenKind.Open, "color")]
    [InlineData("renpy", "{=red}", MarkupTokenKind.Open, "=red")]
    [InlineData("renpy", "{/=red}", MarkupTokenKind.Close, "=red")]
    [InlineData("renpy", "{{", MarkupTokenKind.Placeholder, null)]
    [InlineData("renpy", "[player]", MarkupTokenKind.Placeholder, null)]
    [InlineData("godot", "[color=red]", MarkupTokenKind.Open, "color")]
    [InlineData("godot", "[/color]", MarkupTokenKind.Close, "color")]
    [InlineData("godot", "{name}", MarkupTokenKind.Placeholder, null)]
    [InlineData("unity", "<B>", MarkupTokenKind.Open, "b")]
    [InlineData("unity", "</b>", MarkupTokenKind.Close, "b")]
    [InlineData("unity", "<br/>", MarkupTokenKind.Placeholder, null)]
    [InlineData("unity", "[ff0000]", MarkupTokenKind.Open, "#color")]
    [InlineData("unity", "[-]", MarkupTokenKind.Close, "#color")]
    [InlineData("unity", "{0}", MarkupTokenKind.Placeholder, null)]
    [InlineData("unreal", "</>", MarkupTokenKind.Close, TextMarkup.AnyTag)]
    [InlineData("unreal", "<RichText.Bold>", MarkupTokenKind.Open, "richtext.bold")]
    [InlineData("rpgmaker", @"\C[2]", MarkupTokenKind.Placeholder, null)]
    public void Tokens_are_classified_by_the_engine_brackets(string tool, string token, MarkupTokenKind kind, string? name)
    {
        // Act
        var classified = Markup(tool).Classify(token);

        // Assert
        (classified.Kind, classified.Name).Should().Be((kind, name));
    }

    [Fact]
    public void Visible_text_leaves_markup_out()
    {
        // Act
        var visible = Markup("unity").Visible("<b>Gold</b>: {0}");

        // Assert
        visible.Should().Be("Gold: ");
    }

    [Fact]
    public void No_markup_finds_no_placeholders()
    {
        // Act
        var tokens = TextMarkup.None.Placeholders("{0} %s <b>");

        // Assert
        tokens.Should().BeEmpty();
    }
}
