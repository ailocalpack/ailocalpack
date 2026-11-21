using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>The duplicate check before publishing: packs of the same game + language, which one to revise (task 60).</summary>
public sealed class SimilarPacksTests
{
    private static RegistryPack Pack(string id, string language = "ru", string[]? builds = null, int translated = 2, int likes = 0, OsKind? os = null) => new(
        id,
        PublishBed.Game,
        language,
        "Full translation",
        new PackAuthor("alice"),
        PackStatus.Alpha,
        1,
        PublishBed.Now,
        new PackCoverage(translated, 2),
        builds ?? ["1000"],
        1,
        $"games/steam-620/{language}/{id}/manifest.json",
        100,
        new RegistryRating(likes, 0, 0),
        BuildOs: os is { } platform ? (builds ?? ["1000"]).ToDictionary(b => b, _ => (IReadOnlyList<OsKind>)[platform]) : null);

    private static RegistryIndex Index(params RegistryPack[] packs) => new(PublishBed.Now, new Dictionary<GameId, RegistryGame>
    {
        [PublishBed.Game] = new(PublishBed.Game, false, false, packs.GroupBy(p => p.Language).ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryPack>)[.. g])),
    });

    private static LocalPack Local(LocalPackUpdate? update = null) =>
        new("folder", PublishBed.LocalManifest(), PublishBed.Report() with { Update = update });

    private static LocalPack LocalOn(OsKind os, bool patch = false)
    {
        var manifest = PublishBed.LocalManifestOn(os);
        if (patch)
        {
            manifest = manifest with
            {
                Recipe = null,
                Strings = null,
                Patch = new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/p.zip"), new string('d', 64), 10, new Dictionary<string, string> { ["data/Items.json"] = new('e', 64) }),
            };
        }

        return new LocalPack("folder", manifest, PublishBed.Report());
    }

    [Fact]
    public void Only_packs_of_the_same_language_are_similar()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full"), Pack("de-full", "de")), Local());

        // Assert
        similar.Select(s => s.Pack.Id).Should().Equal("full");
    }

    [Fact]
    public void A_pack_for_the_same_build_and_coverage_is_not_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full")), Local());

        // Assert
        similar.Single().RevisionRecommended.Should().BeFalse();
    }

    [Fact]
    public void A_pack_made_for_another_build_is_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", builds: ["900"])), Local());

        // Assert
        similar.Single().RevisionRecommended.Should().BeTrue();
    }

    [Fact]
    public void A_pack_translating_less_is_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", translated: 1)), Local());

        // Assert
        similar.Single().RevisionRecommended.Should().BeTrue();
    }

    [Fact]
    public void The_pack_an_update_was_made_from_comes_first_and_is_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("popular", likes: 50), Pack("claude-code")), Local(new LocalPackUpdate(1, 2, 0, 0)));

        // Assert
        similar[0].Should().BeEquivalentTo(new { IsBase = true, RevisionRecommended = true }, o => o.ExcludingMissingMembers());
        similar[0].Pack.Id.Should().Be("claude-code");
    }

    [Fact]
    public void The_same_build_made_on_another_platform_is_not_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", os: OsKind.Windows)), LocalOn(OsKind.MacOS));

        // Assert
        similar.Single().RevisionRecommended.Should().BeFalse();
    }

    [Fact]
    public void Another_build_on_the_same_platform_is_still_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", builds: ["900"], os: OsKind.MacOS)), LocalOn(OsKind.MacOS));

        // Assert
        similar.Single().RevisionRecommended.Should().BeTrue();
    }

    [Fact]
    public void A_pack_translating_less_on_another_platform_is_not_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", translated: 1, os: OsKind.Windows)), LocalOn(OsKind.MacOS));

        // Assert
        similar.Single().RevisionRecommended.Should().BeFalse();
    }

    [Fact]
    public void A_recipe_revision_is_expected_to_keep_the_other_platforms()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", os: OsKind.Windows)), LocalOn(OsKind.MacOS));

        // Assert
        similar.Single().Loss.Should().BeNull();
    }

    [Fact]
    public void A_patch_revision_made_on_another_platform_loses_the_old_one()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", os: OsKind.Windows)), LocalOn(OsKind.MacOS, patch: true));

        // Assert
        similar.Single().Loss.Should().BeEquivalentTo(new PlatformLoss(1, [OsKind.Windows], [OsKind.MacOS], [OsKind.Windows]));
    }

    [Fact]
    public void A_revision_that_loses_a_platform_is_never_recommended()
    {
        // Act
        var similar = SimilarPacks.Find(Index(Pack("full", builds: ["900"], os: OsKind.Windows)), LocalOn(OsKind.MacOS, patch: true));

        // Assert
        similar.Single().RevisionRecommended.Should().BeFalse();
    }

    [Fact]
    public void A_loss_the_publish_client_found_is_shown_for_the_same_pack()
    {
        // Arrange
        var similar = SimilarPacks.Find(Index(Pack("full", builds: ["900"], os: OsKind.Windows)), LocalOn(OsKind.MacOS)).Single();
        var loss = new PlatformLoss(1, [OsKind.Windows], [OsKind.MacOS], [OsKind.Windows]);

        // Act
        var shown = SimilarPacks.WithLoss(similar, "full", loss);

        // Assert
        shown.Should().BeEquivalentTo(new { RevisionRecommended = false, Loss = loss }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public void A_loss_found_for_another_pack_is_not_shown()
    {
        // Arrange
        var similar = SimilarPacks.Find(Index(Pack("full", os: OsKind.Windows)), LocalOn(OsKind.MacOS)).Single();

        // Act
        var shown = SimilarPacks.WithLoss(similar, "other", new PlatformLoss(1, [OsKind.Windows], [OsKind.MacOS], [OsKind.Windows]));

        // Assert
        shown.Should().Be(similar);
    }
}
