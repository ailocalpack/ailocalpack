using AiLocalPack.Core.Games;

namespace AiLocalPack.Core.Tests.Games;

public sealed class GameIdTests
{
    private static readonly string Hash = new('a', 64);

    [Theory]
    [InlineData("steam:1091500")]
    [InlineData("steam:4294967295")]
    [InlineData("fp:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Parse_then_format_round_trips(string text)
    {
        // Arrange
        var id = GameId.Parse(text);

        // Act
        var formatted = id.ToString();

        // Assert
        formatted.Should().Be(text);
    }

    [Fact]
    public void Steam_id_formats_with_the_steam_prefix()
    {
        // Arrange
        var id = GameId.Steam(413150);

        // Act
        var text = id.ToString();

        // Assert
        text.Should().Be("steam:413150");
    }

    [Fact]
    public void Steam_id_exposes_the_app_id()
    {
        // Arrange
        var id = GameId.Parse("steam:413150");

        // Act
        var appId = id.SteamAppId;

        // Assert
        appId.Should().Be(413150u);
    }

    [Fact]
    public void Fingerprint_id_has_no_app_id()
    {
        // Arrange
        var id = GameId.Fingerprint(Hash);

        // Act
        var appId = id.SteamAppId;

        // Assert
        appId.Should().BeNull();
    }

    [Fact]
    public void Parsed_and_built_ids_are_equal()
    {
        // Arrange
        var built = GameId.Steam(20);

        // Act
        var parsed = GameId.Parse("steam:20");

        // Assert
        parsed.Should().Be(built);
    }

    [Theory]
    [InlineData("")]
    [InlineData("413150")]
    [InlineData("steam:")]
    [InlineData("steam:0")]
    [InlineData("steam:0413150")]
    [InlineData("steam:-1")]
    [InlineData("steam:+5")]
    [InlineData("steam: 5")]
    [InlineData("steam:4294967296")]
    [InlineData("Steam:413150")]
    [InlineData("epic:413150")]
    [InlineData("fp:")]
    [InlineData("fp:abc")]
    [InlineData("fp:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("fp:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("fp:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void Invalid_text_does_not_parse(string text)
    {
        // Arrange
        // Act
        var parsed = GameId.TryParse(text, out _);

        // Assert
        parsed.Should().BeFalse();
    }

    [Fact]
    public void Parse_of_invalid_text_throws_format_exception()
    {
        // Arrange
        var act = () => GameId.Parse("steam:abc");

        // Act
        // Assert
        act.Should().Throw<FormatException>().WithMessage("*steam:abc*");
    }

    [Fact]
    public void Null_does_not_parse()
    {
        // Arrange
        // Act
        var parsed = GameId.TryParse(null, out var id);

        // Assert
        id.Should().BeNull();
        parsed.Should().BeFalse();
    }

    [Fact]
    public void Steam_app_id_zero_is_rejected()
    {
        // Arrange
        var act = () => GameId.Steam(0);

        // Act
        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Fingerprint_with_uppercase_hex_is_rejected()
    {
        // Arrange
        var act = () => GameId.Fingerprint(new string('A', 64));

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
