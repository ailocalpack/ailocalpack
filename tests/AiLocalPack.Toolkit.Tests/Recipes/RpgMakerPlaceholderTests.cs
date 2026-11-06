using AiLocalPack.Toolkit.Recipes.RpgMaker;

namespace AiLocalPack.Toolkit.Tests.Recipes;

public sealed class RpgMakerPlaceholderTests
{
    [Theory]
    [InlineData(@"\C[2]Red\C[0]", new[] { @"\C[2]", @"\C[0]" })]
    [InlineData(@"Hi, \N[1] and \P[2]!", new[] { @"\N[1]", @"\P[2]" })]
    [InlineData(@"You have \V[10]\G.", new[] { @"\V[10]", @"\G" })]
    [InlineData(@"\I[64]Sword", new[] { @"\I[64]" })]
    [InlineData(@"\{Big\} \.\|\!\>fast\<\^", new[] { @"\{", @"\}", @"\.", @"\|", @"\!", @"\>", @"\<", @"\^" })]
    [InlineData(@"Gold\$ and a \\ backslash", new[] { @"\$", @"\\" })]
    [InlineData(@"\PX[10]\PY[4]\FS[28]text", new[] { @"\PX[10]", @"\PY[4]", @"\FS[28]" })]
    [InlineData(@"\pop<1>plugin code", new[] { @"\pop<1>" })]
    [InlineData("%1 takes %2 damage!", new[] { "%1", "%2" })]
    [InlineData("100% sure", new string[0])]
    [InlineData("Plain text", new string[0])]
    public void Escape_codes_and_format_fields_are_placeholders(string text, string[] expected)
    {
        // Arrange
        var input = text;

        // Act
        var placeholders = RpgMakerStrings.Placeholders(input);

        // Assert
        placeholders.Should().Equal(expected);
    }
}
