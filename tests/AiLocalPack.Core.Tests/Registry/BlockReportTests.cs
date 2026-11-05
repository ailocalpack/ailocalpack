using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Registry;

/// <summary>The prefilled registry issue of a blocked game's <c>Report</c> (task 23).</summary>
public sealed class BlockReportTests
{
    private static readonly GameBlock Eac = new(LocalizationBlock.AntiCheat, "eac", "Easy Anti-Cheat");

    private static Game Game(string name = "Some Shooter", GameSource source = GameSource.Steam) =>
        new(GameId.Steam(620), name, source, "/games/shooter", null, null, "unreal:4", "eac", null, true);

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void Issue_opens_in_the_registry_repo()
    {
        // Arrange
        var game = Game();

        // Act
        var uri = BlockReport.IssueUri(game, Eac, "EasyAntiCheat/", "0.4.0");

        // Assert
        uri.GetLeftPart(UriPartial.Path).Should().Be("https://github.com/ailocalpack/registry/issues/new");
    }

    [Fact]
    public void Title_names_the_game_and_its_id()
    {
        // Arrange
        var game = Game();

        // Act
        var uri = BlockReport.IssueUri(game, Eac, "EasyAntiCheat/", "0.4.0");

        // Assert
        Query(uri)["title"].Should().Be("Block report: Some Shooter (steam:620)");
    }

    [Fact]
    public void Anti_cheat_body_has_the_game_id_the_anti_cheat_and_the_detected_file()
    {
        // Arrange
        var game = Game();

        // Act
        var body = BlockReport.Body(game, Eac, "EasyAntiCheat/", "0.4.0");

        // Assert
        body.Should().Be(
            "**Game:** Some Shooter\n" +
            "**Game ID:** `steam:620`\n" +
            "**Store:** Steam\n" +
            "**Blocked because:** anti-cheat Easy Anti-Cheat (`eac`)\n" +
            "**Detected file:** `EasyAntiCheat/`\n" +
            "**App version:** 0.4.0\n\n" +
            "**Why I think this is a mistake** (a link to the studio's statement that mods are allowed helps):\n\n");
    }

    [Fact]
    public void Anti_cheat_listed_by_game_id_says_no_file_was_found()
    {
        // Arrange
        var game = Game();

        // Act
        var body = BlockReport.Body(game, Eac, detectedFile: null, "0.4.0");

        // Assert
        body.Should().Contain("**Detected file:** none, the game is listed in the anti-cheat policy\n");
    }

    [Fact]
    public void Opt_out_body_names_the_flag_and_no_file()
    {
        // Arrange
        var game = Game(source: GameSource.Manual);

        // Act
        var body = BlockReport.Body(game, new GameBlock(LocalizationBlock.OptOut), detectedFile: null, "0.4.0");

        // Assert
        body.Should().Contain("**Store:** added manually\n**Blocked because:** the registry's opt-out flag").And.NotContain("Detected file");
    }

    [Fact]
    public void Query_characters_in_the_name_are_percent_encoded()
    {
        // Arrange
        var game = Game("Tom & Jerry #2 = 100%?");

        // Act
        var uri = BlockReport.IssueUri(game, Eac, "EasyAntiCheat/", "0.4.0");

        // Assert
        uri.AbsoluteUri.Should().Contain("title=Block%20report%3A%20Tom%20%26%20Jerry%20%232%20%3D%20100%25%3F%20%28steam%3A620%29&body=");
    }

    [Fact]
    public void Non_ascii_name_is_utf8_encoded()
    {
        // Arrange
        var game = Game("Фонарщики");

        // Act
        var uri = BlockReport.IssueUri(game, Eac, "EasyAntiCheat/", "0.4.0");

        // Assert
        uri.AbsoluteUri.Should().Contain("%D0%A4%D0%BE%D0%BD%D0%B0%D1%80%D1%89%D0%B8%D0%BA%D0%B8");
    }

    [Fact]
    public void Body_round_trips_through_the_url_with_its_line_breaks()
    {
        // Arrange
        var game = Game("Tom & Jerry");

        // Act
        var uri = BlockReport.IssueUri(game, Eac, "EasyAntiCheat/", "0.4.0");

        // Assert
        Query(uri)["body"].Should().Be(BlockReport.Body(game, Eac, "EasyAntiCheat/", "0.4.0"));
    }
}
