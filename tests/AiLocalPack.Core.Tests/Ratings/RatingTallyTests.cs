using AiLocalPack.Core.Games;
using AiLocalPack.Core.Ratings;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Ratings;

/// <summary>The anti-abuse rules of task 64 on fixture Discussion data.</summary>
public sealed class RatingTallyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly RegistryPackKey Key = new(GameId.Steam(620), "ru", "full");
    private static readonly RatedPack Pack = new(Key, "Full", 2, ["alice", "bob"], new HashSet<string> { "1250000" });

    private int _next;

    private static DiscussionUser User(string login, int ageDays = 365, bool bot = false) =>
        new(login, bot, bot ? null : Now.AddDays(-ageDays));

    private DiscussionComment Report(
        DiscussionUser? author,
        PackVerdict verdict = PackVerdict.Works,
        int revision = 2,
        string? build = "1250000",
        string pack = "full",
        DateTimeOffset? at = null) =>
        new(
            $"C{_next++}",
            author,
            at ?? Now,
            null,
            PackReportComment.Format(new PackReport(verdict, GameId.Steam(620), "ru", pack, revision, build)));

    private static PackTally Count(RatedPack pack, params DiscussionComment[] comments) =>
        RatingTally.Count(pack, new CollectedDiscussion("D1", 1, new Uri("https://github.com/ailocalpack/registry/discussions/1"), Key, [], comments));

    private static PackTally Count(params DiscussionComment[] comments) => Count(Pack, comments);

    [Fact]
    public void Independent_reports_are_counted()
    {
        // Act
        var tally = Count(Report(User("p1")), Report(User("p2")), Report(User("p3"), PackVerdict.DoesNotWork));

        // Assert
        tally.Rating.Should().Be(new RegistryRating(0, 2, 1));
    }

    [Fact]
    public void Author_vote_is_ignored()
    {
        // Act
        var tally = Count(Report(User("p1")), Report(User("Bob")));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.Author]).Should().Be((1, 1));
    }

    [Fact]
    public void Duplicate_votes_of_one_account_count_once()
    {
        // Act
        var tally = Count(Report(User("p1"), at: Now.AddHours(-2)), Report(User("P1"), at: Now.AddHours(-1)));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.Replaced]).Should().Be((1, 1));
    }

    [Fact]
    public void The_latest_report_of_an_account_wins()
    {
        // Act
        var tally = Count(Report(User("p1"), PackVerdict.DoesNotWork, at: Now.AddHours(-1)), Report(User("p1"), PackVerdict.Works, at: Now.AddHours(-2)));

        // Assert
        tally.Rating.Should().Be(new RegistryRating(0, 0, 1));
    }

    [Fact]
    public void An_edited_report_counts_with_its_edit_time()
    {
        // Arrange
        var edited = Report(User("p1"), PackVerdict.DoesNotWork, at: Now.AddDays(-3)) with { LastEditedAt = Now };

        // Act
        var tally = Count(edited, Report(User("p1"), PackVerdict.Works, at: Now.AddDays(-1)));

        // Assert
        tally.Rating.Should().Be(new RegistryRating(0, 0, 1));
    }

    [Fact]
    public void Young_account_is_ignored()
    {
        // Act
        var tally = Count(Report(User("fresh", ageDays: 29)), Report(User("old", ageDays: 30)));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.YoungAccount]).Should().Be((1, 1));
    }

    [Fact]
    public void Account_age_is_taken_when_the_report_was_posted()
    {
        // Arrange
        var user = new DiscussionUser("p1", false, Now.AddDays(-40));

        // Act
        var tally = Count(Report(user, at: Now.AddDays(-20)));

        // Assert
        tally.Ignored.Should().ContainKey(IgnoredReport.YoungAccount);
    }

    [Fact]
    public void Bot_is_ignored()
    {
        // Act
        var tally = Count(Report(User("helper[bot]", bot: true)));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.Bot]).Should().Be((0, 1));
    }

    [Fact]
    public void Deleted_account_is_ignored()
    {
        // Act
        var tally = Count(Report(null));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.NoAccount]).Should().Be((0, 1));
    }

    [Fact]
    public void Report_of_an_older_revision_is_ignored()
    {
        // Act
        var tally = Count(Report(User("p1"), revision: 1));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.OtherRevision]).Should().Be((0, 1));
    }

    [Fact]
    public void Report_about_another_pack_is_ignored()
    {
        // Act
        var tally = Count(Report(User("p1"), pack: "menus"));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.OtherPack]).Should().Be((0, 1));
    }

    [Theory]
    [InlineData("1234567")]
    [InlineData(null)]
    public void Steam_report_from_a_build_the_pack_doesnt_list_is_ignored(string? build)
    {
        // Act
        var tally = Count(Report(User("p1"), build: build));

        // Assert
        (tally.Rating.Works, tally.Ignored[IgnoredReport.OtherBuild]).Should().Be((0, 1));
    }

    [Fact]
    public void Report_of_a_game_without_store_builds_counts_whatever_its_build()
    {
        // Arrange
        var pack = Pack with { BuildIds = null };

        // Act
        var tally = Count(pack, Report(User("p1"), build: "files:0a1b2c3d"));

        // Assert
        tally.Rating.Works.Should().Be(1);
    }

    [Fact]
    public void Plain_comments_are_not_reports()
    {
        // Arrange
        var comment = new DiscussionComment("C9", User("p1"), Now, null, "Works for me ✅");

        // Act
        var tally = Count(comment);

        // Assert
        (tally.Rating, tally.Ignored.Count).Should().Be((RegistryRating.None, 0));
    }

    [Fact]
    public void Likes_leave_out_authors_and_bots()
    {
        // Arrange
        var discussion = new CollectedDiscussion("D1", 1, new Uri("https://github.com/ailocalpack/registry/discussions/1"), Key,
            [User("p1"), User("ALICE"), User("p2"), User("ci[bot]", bot: true)], []);

        // Act
        var tally = RatingTally.Count(Pack, discussion);

        // Assert
        tally.Rating.Likes.Should().Be(2);
    }

    [Fact]
    public void Pack_without_a_discussion_has_no_rating()
    {
        // Act
        var tally = RatingTally.Count(Pack, null);

        // Assert
        tally.Rating.Should().Be(RegistryRating.None);
    }

    [Fact]
    public void Three_counted_confirmations_make_it_verified()
    {
        // Act
        var confirmed = RegistryVerification.IsConfirmed(Count(Report(User("p1")), Report(User("p2")), Report(User("p3"))).Rating);

        // Assert
        confirmed.Should().BeTrue();
    }

    [Fact]
    public void Three_confirmations_with_one_from_an_author_are_not_enough()
    {
        // Act
        var confirmed = RegistryVerification.IsConfirmed(Count(Report(User("p1")), Report(User("p2")), Report(User("alice"))).Rating);

        // Assert
        confirmed.Should().BeFalse();
    }

    [Theory]
    [InlineData("<!-- ailocalpack-pack steam:620/ru/full -->\nHello", true)]
    [InlineData("  <!--ailocalpack-pack steam:620/ru/full-->", true)]
    [InlineData("Hello\n<!-- ailocalpack-pack steam:620/ru/full -->", false)]
    [InlineData("<!-- ailocalpack-pack steam:620/xx/full -->", false)]
    [InlineData(null, false)]
    public void Discussion_marker_is_read_only_at_the_start(string? body, bool found)
    {
        // Act
        var read = PackDiscussionMarker.TryParse(body, out _);

        // Assert
        read.Should().Be(found);
    }

    [Fact]
    public void Discussion_marker_round_trips()
    {
        // Act
        PackDiscussionMarker.TryParse(PackDiscussionMarker.Format(Key) + "\nbody", out var read);

        // Assert
        read.Should().Be(Key);
    }
}
