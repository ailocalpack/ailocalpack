using AiLocalPack.Core.Games;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary>The player's marks on installed packs, kept in <c>pack-ratings.json</c> (task 63).</summary>
public sealed class PackRatingStoreTests : IDisposable
{
    private static readonly GameId Game = GameId.Steam(620);
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _home = new();
    private readonly AppPaths _paths;

    public PackRatingStoreTests() => _paths = AppPaths.UnderHome(_home.Path);

    public void Dispose() => _home.Dispose();

    private PackRatingStore Store() => new(_paths, new FakeClock(Now));

    [Fact]
    public void The_first_session_marks_the_revision_tried()
    {
        // Act
        var rating = Store().Played(Game, "ru", "full", 3, Now);

        // Assert
        rating.Should().BeEquivalentTo(new { Revision = 3, TriedAt = Now, PlayedAt = Now, IsTried = true });
    }

    [Fact]
    public void A_later_session_keeps_the_first_time_and_moves_the_last_one()
    {
        // Arrange
        var store = Store();
        store.Played(Game, "ru", "full", 3, Now);

        // Act
        var rating = store.Played(Game, "ru", "full", 3, Now.AddDays(1));

        // Assert
        rating.Should().BeEquivalentTo(new { TriedAt = Now, PlayedAt = Now.AddDays(1) });
    }

    [Fact]
    public void Marks_survive_a_restart()
    {
        // Arrange
        var store = Store();
        store.Played(Game, "ru", "full", 3, Now);
        store.SetLiked(Game, "ru", "full", 3, true);
        store.Later(Game, "ru", "full", 3, Now.AddHours(1));
        store.Reported(Game, "ru", "full", 3, PackVerdict.DoesNotWork, Now.AddHours(2), new Uri("https://github.com/a/b/discussions/1#discussioncomment-2"));

        // Act
        var rating = Store().Find(Game, "ru", "full");

        // Assert
        rating.Should().Be(new PackRating(Game, "ru", "full", 3, Now, Now, Now.AddHours(1), 1, true, PackVerdict.DoesNotWork, Now.AddHours(2),
            new Uri("https://github.com/a/b/discussions/1#discussioncomment-2")));
    }

    [Fact]
    public void Another_revision_starts_over_but_keeps_the_like()
    {
        // Arrange
        var store = Store();
        store.Played(Game, "ru", "full", 3, Now);
        store.SetLiked(Game, "ru", "full", 3, true);
        store.Reported(Game, "ru", "full", 3, PackVerdict.Works, Now, null);

        // Act
        var rating = store.Played(Game, "ru", "full", 4, Now.AddDays(1));

        // Assert
        rating.Should().Be(new PackRating(Game, "ru", "full", 4, Now.AddDays(1), Now.AddDays(1), Liked: true));
    }

    [Fact]
    public void A_change_is_announced_for_the_game()
    {
        // Arrange
        var store = Store();
        var changed = new List<GameId>();
        store.Changed += (_, game) => changed.Add(game);

        // Act
        store.Played(Game, "ru", "full", 3, Now);

        // Assert
        changed.Should().Equal(Game);
    }

    [Fact]
    public void A_damaged_file_is_moved_aside_and_read_as_empty()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.PackRatingsFile, "{ not json");

        // Act
        var rating = Store().Find(Game, "ru", "full");

        // Assert
        rating.Should().BeNull();
        Directory.GetFiles(_paths.Data, "pack-ratings.json.*.bak").Should().ContainSingle();
    }

    [Fact]
    public void A_newer_format_is_never_overwritten()
    {
        // Arrange
        Directory.CreateDirectory(_paths.Data);
        File.WriteAllText(_paths.PackRatingsFile, """{"schemaVersion": 99, "packs": []}""");

        // Act
        Store().Played(Game, "ru", "full", 3, Now);

        // Assert
        File.ReadAllText(_paths.PackRatingsFile).Should().Contain("99");
    }
}
