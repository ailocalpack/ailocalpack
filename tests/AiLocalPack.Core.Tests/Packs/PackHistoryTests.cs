using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Tests.Publishing;

namespace AiLocalPack.Core.Tests.Packs;

/// <summary>Registry CI's check that a pack PR only adds to a pack's history (<c>pack validate --base</c>).</summary>
public sealed class PackHistoryTests
{
    private static readonly PackManifest Merged = PublishBed.LocalManifest() with
    {
        Authors = [new PackAuthor("alice", "Alice")],
        Revisions = [new PackRevision(1, PublishBed.Now.AddDays(-10), "alice", "First")],
    };

    private static PackManifest Next(string by = "bob") => Merged with
    {
        Authors = [.. Merged.Authors, new PackAuthor(by)],
        Revisions = [.. Merged.Revisions, new PackRevision(2, PublishBed.Now, by)],
    };

    [Fact]
    public void A_revision_adding_one_to_the_merged_history_passes()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next());

        // Assert
        problems.Should().BeEmpty();
    }

    [Fact]
    public void An_author_login_in_another_case_is_the_same_author()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next() with { Authors = [new PackAuthor("ALICE"), new PackAuthor("bob")] });

        // Assert
        problems.Should().BeEmpty();
    }

    [Fact]
    public void A_revision_taking_the_original_author_off_is_refused()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next() with { Authors = [new PackAuthor("bob")] });

        // Assert
        problems.Should().ContainSingle(p => p.Field == PackHistory.ProblemField && p.Message.Contains("must stay first", StringComparison.Ordinal));
    }

    [Fact]
    public void A_revision_putting_itself_before_the_original_author_is_refused()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next() with { Authors = [new PackAuthor("bob"), new PackAuthor("alice")] });

        // Assert
        problems.Should().ContainSingle(p => p.Message.Contains("must stay first", StringComparison.Ordinal));
    }

    [Fact]
    public void A_revision_rewriting_an_earlier_one_is_refused()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next() with { Revisions = [new PackRevision(1, Merged.Revisions[0].Date, "bob"), new PackRevision(2, PublishBed.Now, "bob")] });

        // Assert
        problems.Should().ContainSingle(p => p.Message.Contains("must stay as the registry has them", StringComparison.Ordinal));
    }

    [Fact]
    public void A_revision_skipping_a_number_is_refused()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next() with { Revisions = [.. Merged.Revisions, new PackRevision(2, PublishBed.Now, "bob"), new PackRevision(3, PublishBed.Now, "bob")] });

        // Assert
        problems.Should().ContainSingle(p => p.Message.Contains("so the next one is 2, not 3", StringComparison.Ordinal));
    }

    [Fact]
    public void A_revision_changing_the_created_date_is_refused()
    {
        // Act
        var problems = PackHistory.Problems(Merged, Next() with { Created = PublishBed.Now });

        // Assert
        problems.Should().ContainSingle(p => p.Message.Contains("created", StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_pack_starts_at_revision_one()
    {
        // Act
        var problems = PackHistory.Problems(null, Next());

        // Assert
        problems.Should().ContainSingle(p => p.Message == "a new pack starts at revision 1, not 2.");
    }
}
