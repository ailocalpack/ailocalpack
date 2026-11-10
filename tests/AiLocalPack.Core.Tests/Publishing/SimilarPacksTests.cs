using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>The duplicate check before publishing: packs of the same game + language, which one to revise (task 60).</summary>
public sealed class SimilarPacksTests
{
    private static RegistryPack Pack(string id, string language = "ru", string[]? builds = null, int translated = 2, int likes = 0) => new(
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
        new RegistryRating(likes, 0, 0));

    private static RegistryIndex Index(params RegistryPack[] packs) => new(PublishBed.Now, new Dictionary<GameId, RegistryGame>
    {
        [PublishBed.Game] = new(PublishBed.Game, false, false, packs.GroupBy(p => p.Language).ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryPack>)[.. g])),
    });

    private static LocalPack Local(LocalPackUpdate? update = null) =>
        new("folder", PublishBed.LocalManifest(), PublishBed.Report() with { Update = update });

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
}
