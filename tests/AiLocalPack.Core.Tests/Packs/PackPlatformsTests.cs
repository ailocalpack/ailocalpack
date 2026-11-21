using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Core.Tests.Publishing;

namespace AiLocalPack.Core.Tests.Packs;

/// <summary>Which platforms a pack supports and which ones a new revision would take away (task 87).</summary>
public sealed class PackPlatformsTests
{
    private static PackBuild Build(string id, OsKind? os, char hash = 'a') =>
        new(id, new Dictionary<string, string> { ["data/Items.json"] = new(hash, 64) }, os);

    private static PackManifest With(params PackBuild[] builds) => PublishBed.LocalManifest() with { Builds = builds };

    private static StringTable Table(params string[] keys) => new("ru", keys.ToDictionary(k => k, k => "т " + k, StringComparer.Ordinal));

    [Fact]
    public void A_manifest_supports_the_platforms_of_its_builds()
    {
        // Act
        var platforms = PackPlatforms.Of(With(Build("1000", OsKind.Linux), Build("1000", OsKind.Windows)));

        // Assert
        platforms.Should().Equal(OsKind.Windows, OsKind.Linux);
    }

    [Fact]
    public void A_build_without_a_platform_counts_as_every_platform()
    {
        // Act
        var platforms = PackPlatforms.Of(With(Build("1000", null)));

        // Assert
        platforms.Should().Equal(OsKind.Windows, OsKind.MacOS, OsKind.Linux);
    }

    [Fact]
    public void A_revision_on_another_platform_loses_the_old_one()
    {
        // Act
        var loss = PackPlatforms.Lost(With(Build("1000", OsKind.Windows)), With(Build("1000", OsKind.MacOS)));

        // Assert
        loss.Should().BeEquivalentTo(new PlatformLoss(1, [OsKind.Windows], [OsKind.MacOS], [OsKind.Windows]));
    }

    [Fact]
    public void A_revision_keeping_every_platform_loses_nothing()
    {
        // Act
        var loss = PackPlatforms.Lost(With(Build("1000", OsKind.Windows)), With(Build("1001", OsKind.Windows), Build("1001", OsKind.MacOS)));

        // Assert
        loss.Should().BeNull();
    }

    [Fact]
    public void A_revision_of_a_pack_without_platforms_loses_the_others()
    {
        // Act
        var loss = PackPlatforms.Lost(With(Build("1000", null)), With(Build("1000", OsKind.MacOS)));

        // Assert
        loss!.Lost.Should().Equal(OsKind.Windows, OsKind.Linux);
    }

    [Fact]
    public void The_loss_reads_as_one_sentence()
    {
        // Arrange
        var loss = new PlatformLoss(3, [OsKind.Windows, OsKind.Linux], [OsKind.MacOS], [OsKind.Windows, OsKind.Linux]);

        // Act
        var text = loss.Describe();

        // Assert
        text.Should().Be("rev 3 supports Windows, Linux; this revision would support only macOS: Windows, Linux players would lose a compatible translation.");
    }

    [Fact]
    public void Builds_of_another_platform_are_carried_over()
    {
        // Act
        var kept = PackPlatforms.CarriedOver(With(Build("1000", OsKind.MacOS)), With(Build("1000", OsKind.Windows)), Table("a", "b"), Table("a"));

        // Assert
        kept.Should().BeEquivalentTo([Build("1000", OsKind.Windows)]);
    }

    [Fact]
    public void The_local_build_wins_on_its_own_platform()
    {
        // Act
        var kept = PackPlatforms.CarriedOver(With(Build("1001", OsKind.Windows)), With(Build("1000", OsKind.Windows)), Table("a"), Table("a"));

        // Assert
        kept.Should().BeEmpty();
    }

    [Fact]
    public void Builds_are_not_carried_over_when_the_new_table_misses_a_key()
    {
        // Act
        var kept = PackPlatforms.CarriedOver(With(Build("1000", OsKind.MacOS)), With(Build("1000", OsKind.Windows)), Table("a"), Table("a", "b"));

        // Assert
        kept.Should().BeEmpty();
    }

    [Fact]
    public void Builds_are_not_carried_over_without_the_existing_table()
    {
        // Act
        var kept = PackPlatforms.CarriedOver(With(Build("1000", OsKind.MacOS)), With(Build("1000", OsKind.Windows)), Table("a"), null);

        // Assert
        kept.Should().BeEmpty();
    }

    [Fact]
    public void Builds_are_not_carried_over_for_another_recipe_tool()
    {
        // Arrange
        var existing = With(Build("1000", OsKind.Windows)) with { Recipe = new PackRecipe("renpy", "1.0.0") };

        // Act
        var kept = PackPlatforms.CarriedOver(With(Build("1000", OsKind.MacOS)), existing, Table("a"), Table("a"));

        // Assert
        kept.Should().BeEmpty();
    }

    [Fact]
    public void A_patch_pack_carries_nothing_over()
    {
        // Arrange
        var patch = new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/p.zip"), new string('d', 64), 10, new Dictionary<string, string> { ["data/Items.json"] = new('e', 64) });
        var local = With(Build("1000", OsKind.MacOS)) with { Recipe = null, Strings = null, Patch = patch };

        // Act
        var kept = PackPlatforms.CarriedOver(local, With(Build("1000", OsKind.Windows)), null, Table("a"));

        // Assert
        kept.Should().BeEmpty();
    }

    [Fact]
    public void A_build_without_a_platform_is_kept_beside_the_same_build_on_one()
    {
        // Act
        var kept = PackPlatforms.CarriedOver(With(Build("1000", OsKind.MacOS)), With(Build("1000", null)), Table("a"), Table("a"));

        // Assert
        kept.Select(b => (b.BuildId, b.Os)).Should().Equal(("1000", (OsKind?)null));
    }

    [Fact]
    public void Registry_ci_refuses_a_revision_that_drops_a_platform()
    {
        // Act
        var problem = PackPlatforms.Problem(With(Build("1000", OsKind.Windows)), With(Build("1000", OsKind.MacOS)) with { Revisions = [.. With().Revisions, new PackRevision(2, PublishBed.Now, "octo")] });

        // Assert
        problem.Should().BeEquivalentTo(new { File = "", Field = PackPlatforms.ProblemField }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public void Registry_ci_takes_a_revision_that_keeps_the_platforms()
    {
        // Act
        var problem = PackPlatforms.Problem(With(Build("1000", OsKind.Windows)), With(Build("1000", OsKind.Windows), Build("1000", OsKind.MacOS)));

        // Assert
        problem.Should().BeNull();
    }
}
