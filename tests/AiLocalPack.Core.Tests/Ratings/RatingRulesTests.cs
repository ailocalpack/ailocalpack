using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Ratings;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary>When the rating block and prompt show (task 63).</summary>
public sealed class RatingRulesTests
{
    private static readonly GameId Game = GameId.Steam(620);
    private static readonly DateTimeOffset Installed = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstalledPackState State = new("full", Revision: 3);
    private static readonly PackRating Played = new(Game, "ru", "full", 3, TriedAt: Installed.AddHours(1), PlayedAt: Installed.AddHours(1));

    private static InstalledPack Record(bool local = false) =>
        new(Game, "ru", "full", 3, Installed, "/games/620", "/backups/620", [], []) { Local = local };

    // ---------- Played ----------

    [Fact]
    public void A_session_that_started_after_the_install_counts_as_played()
    {
        // Act
        var counts = RatingRules.CountsAsPlayed(Record(), new GameSessionEnded(Game, Installed.AddMinutes(5), Installed.AddMinutes(40)));

        // Assert
        counts.Should().BeTrue();
    }

    [Fact]
    public void A_session_that_started_before_the_install_does_not_count()
    {
        // Act
        var counts = RatingRules.CountsAsPlayed(Record(), new GameSessionEnded(Game, Installed.AddMinutes(-5), Installed.AddMinutes(40)));

        // Assert
        counts.Should().BeFalse();
    }

    [Fact]
    public void A_local_pack_is_never_rated()
    {
        // Act
        var counts = RatingRules.CountsAsPlayed(Record(local: true), new GameSessionEnded(Game, Installed.AddMinutes(5), Installed.AddMinutes(40)));

        // Assert
        counts.Should().BeFalse();
    }

    // ---------- Rating block ----------

    [Fact]
    public void The_block_shows_once_the_installed_revision_was_played()
    {
        // Act
        var show = RatingRules.ShowBlock(State, Played);

        // Assert
        show.Should().BeTrue();
    }

    [Fact]
    public void The_block_is_hidden_right_after_installing()
    {
        // Act
        var show = RatingRules.ShowBlock(State, null);

        // Assert
        show.Should().BeFalse();
    }

    [Fact]
    public void Playing_an_earlier_revision_does_not_show_the_block_for_the_new_one()
    {
        // Act
        var show = RatingRules.ShowBlock(State with { Revision = 4 }, Played);

        // Assert
        show.Should().BeFalse();
    }

    [Fact]
    public void The_block_is_hidden_while_a_game_update_broke_the_pack()
    {
        // Act
        var show = RatingRules.ShowBlock(State with { Problem = InstalledPackProblem.Incompatible }, Played);

        // Assert
        show.Should().BeFalse();
    }

    // ---------- Prompt ----------

    [Fact]
    public void The_prompt_shows_after_the_first_session()
    {
        // Act
        var show = RatingRules.ShowPrompt(State, Played, hasDiscussion: true, isAuthor: false);

        // Assert
        show.Should().BeTrue();
    }

    [Fact]
    public void The_prompt_is_not_shown_to_an_author()
    {
        // Act
        var show = RatingRules.ShowPrompt(State, Played, hasDiscussion: true, isAuthor: true);

        // Assert
        show.Should().BeFalse();
    }

    [Fact]
    public void The_prompt_needs_a_discussion_to_post_to()
    {
        // Act
        var show = RatingRules.ShowPrompt(State, Played, hasDiscussion: false, isAuthor: false);

        // Assert
        show.Should().BeFalse();
    }

    [Fact]
    public void A_reported_revision_is_not_asked_about_again()
    {
        // Act
        var show = RatingRules.ShowPrompt(State, Played with { Reported = PackVerdict.Works }, hasDiscussion: true, isAuthor: false);

        // Assert
        show.Should().BeFalse();
    }

    [Fact]
    public void Later_hides_the_prompt_until_the_next_session_ends()
    {
        // Act
        var show = RatingRules.ShowPrompt(State, Played with { LaterAt = Installed.AddHours(2), LaterCount = 1 }, hasDiscussion: true, isAuthor: false);

        // Assert
        show.Should().BeFalse();
    }

    [Fact]
    public void After_Later_the_next_session_brings_the_prompt_back()
    {
        // Arrange
        var rating = Played with { LaterAt = Installed.AddHours(2), LaterCount = 1, PlayedAt = Installed.AddHours(5) };

        // Act
        var show = RatingRules.ShowPrompt(State, rating, hasDiscussion: true, isAuthor: false);

        // Assert
        show.Should().BeTrue();
    }

    [Fact]
    public void After_Later_twice_the_prompt_stops_asking()
    {
        // Arrange
        var rating = Played with { LaterAt = Installed.AddHours(2), LaterCount = RatingRules.MaxLater, PlayedAt = Installed.AddHours(5) };

        // Act
        var show = RatingRules.ShowPrompt(State, rating, hasDiscussion: true, isAuthor: false);

        // Assert
        show.Should().BeFalse();
    }

    // ---------- Authors ----------

    [Fact]
    public void A_player_credited_in_the_manifest_is_an_author_whatever_the_case()
    {
        // Act
        var author = RatingRules.IsAuthor("Bob", new PackAuthor("alice"), [new PackAuthor("alice"), new PackAuthor("bob")]);

        // Assert
        author.Should().BeTrue();
    }

    [Fact]
    public void Another_player_is_not_an_author()
    {
        // Act
        var author = RatingRules.IsAuthor("carol", new PackAuthor("alice"), [new PackAuthor("alice"), new PackAuthor("bob")]);

        // Assert
        author.Should().BeFalse();
    }

    [Fact]
    public void A_signed_out_player_is_nobodys_author()
    {
        // Act
        var author = RatingRules.IsAuthor(null, new PackAuthor("alice"), null);

        // Assert
        author.Should().BeFalse();
    }
}
