using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Generation;

/// <summary>Reference packs (decided 2026-11-16): which registry packs a new translation learns from, and what they give.</summary>
public sealed class ReferencePacksTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly GameId Game = GameId.Steam(620);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static RegistryPack Listed(string language, string id = "full", PackStatus status = PackStatus.Alpha, int works = 0, int doesNotWork = 0, string[]? builds = null) => new(
        id,
        Game,
        language,
        "Full",
        new PackAuthor("alice"),
        status,
        1,
        T0,
        new PackCoverage(10, 10),
        builds ?? ["1000"],
        1,
        $"games/steam-620/{language}/{id}/manifest.json",
        100,
        new RegistryRating(0, works, doesNotWork));

    private static RegistryIndex Index(params RegistryPack[] packs) => new(
        T0,
        new Dictionary<GameId, RegistryGame>
        {
            [Game] = new(Game, false, false, packs.GroupBy(p => p.Language).ToDictionary(g => g.Key, IReadOnlyList<RegistryPack> (g) => [.. g])),
        });

    private static PackManifest Manifest(string language, string id = "full", string? buildId = "1000", bool patch = false, IReadOnlyList<string>? fonts = null) => new(
        id,
        Game,
        language,
        "Full",
        [new PackAuthor("alice")],
        [new PackRevision(1, T0, "alice")],
        PackStatus.Verified,
        null,
        [new PackBuild(buildId, new Dictionary<string, string> { ["data/Map001.json"] = new('a', 64) })],
        patch ? null : new PackRecipe("rpgmaker", "1.0.0"),
        patch ? null : new PackStringsFile(PackStringsFile.DefaultPath, new string('e', 64), 10),
        patch ? new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.org/p.zip"), new string('c', 64), 10, new Dictionary<string, string> { ["data/Map001.json"] = new('d', 64) }) : null,
        [],
        fonts ?? [],
        new PackCoverage(1, 1),
        T0,
        T0);

    private static StringTable Table(string language, string text, string? madeFrom) => new(language, new Dictionary<string, string> { ["k"] = text })
    {
        Originals = madeFrom is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["k"] = OriginalHash.Of(madeFrom) },
    };

    [Fact]
    public void Only_packs_players_confirmed_are_references()
    {
        // Arrange
        var index = Index(Listed("de", works: 0), Listed("fr", works: 2, doesNotWork: 2), Listed("it", works: 1), Listed("es-ES", status: PackStatus.Verified));

        // Act
        var selected = ReferenceSelection.Select(index, Game, "ru", "1000");

        // Assert
        selected.Select(p => p.Language).Should().BeEquivalentTo("it", "es-ES");
    }

    [Fact]
    public void English_comes_first_then_the_closest_language()
    {
        // Arrange
        var index = Index(Listed("de", works: 5), Listed("uk", works: 1), Listed("en", works: 1));

        // Act
        var selected = ReferenceSelection.Select(index, Game, "ru", "1000");

        // Assert
        selected.Select(p => p.Language).Should().Equal("en", "uk");
    }

    [Fact]
    public void A_language_gives_its_pack_made_for_the_build_first()
    {
        // Arrange
        var index = Index(Listed("en", "old", PackStatus.Verified, builds: ["900"]), Listed("en", "new", works: 1, builds: ["1000"]));

        // Act
        var selected = ReferenceSelection.Select(index, Game, "ru", "1000");

        // Assert
        selected.Single().Id.Should().Be("new");
    }

    [Fact]
    public void A_game_the_registry_lacks_has_no_references()
    {
        // Arrange
        var index = Index();

        // Act
        var selected = ReferenceSelection.Select(index, GameId.Steam(10), "ru", null);

        // Assert
        selected.Should().BeEmpty();
    }

    [Fact]
    public void A_reference_made_from_the_same_original_is_given()
    {
        // Arrange
        var set = new ReferenceSet([new ReferencePack(Manifest("en"), Table("en", "Fire!", "Feuer!"), SameBuild: false)]);

        // Act
        var found = set.For("k", "Feuer!");

        // Assert
        found.Should().Equal(new Dictionary<string, string> { ["en"] = "Fire!" });
    }

    [Fact]
    public void A_reference_made_from_another_original_is_left_out()
    {
        // Arrange
        var set = new ReferenceSet([new ReferencePack(Manifest("en"), Table("en", "Fire!", "Feuer"), SameBuild: true)]);

        // Act
        var found = set.For("k", "Feuer!");

        // Assert
        found.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Without_an_original_hash_only_a_pack_for_the_same_build_is_given(bool sameBuild, int expected)
    {
        // Arrange
        var set = new ReferenceSet([new ReferencePack(Manifest("en"), Table("en", "Fire!", null), sameBuild)]);

        // Act
        var found = set.For("k", "Feuer!");

        // Assert
        found.Should().HaveCount(expected);
    }

    [Fact]
    public void Fonts_come_from_references_in_the_same_script()
    {
        // Arrange
        var set = new ReferenceSet(
        [
            new ReferencePack(Manifest("en", fonts: ["noto-sans"]), null, true),
            new ReferencePack(Manifest("uk", fonts: ["pt-sans"]), null, true),
        ]);

        // Act
        var fonts = set.FontsFor("ru");

        // Assert
        fonts.Should().Equal("pt-sans");
    }

    [Fact]
    public void A_reference_with_a_recipe_names_the_tool()
    {
        // Arrange
        var set = new ReferenceSet([new ReferencePack(Manifest("en", patch: true), null, true), new ReferencePack(Manifest("de"), null, true)]);

        // Act
        var recipe = set.Recipe;

        // Assert
        recipe!.Tool.Should().Be("rpgmaker");
    }

    [Theory]
    [InlineData("1000", true)]
    [InlineData("1001", false)]
    public void A_steam_reference_is_for_the_same_build_when_it_lists_the_build_id(string buildId, bool expected)
    {
        // Arrange
        var manifest = Manifest("en");

        // Act
        var same = ReferencePack.IsSameBuild(manifest, GameVersion.FromBuildId(buildId));

        // Assert
        same.Should().Be(expected);
    }

    [Theory]
    [InlineData("es-ES", "es-419", 7)]
    [InlineData("ru", "uk", 3)]
    [InlineData("ru", "pl", 1)]
    [InlineData("ru", "ja", 0)]
    public void Closeness_counts_language_script_and_family(string a, string b, int expected)
    {
        // Arrange
        var pair = (a, b);

        // Act
        var closeness = LanguageKinship.Closeness(pair.a, pair.b);

        // Assert
        closeness.Should().Be(expected);
    }

    [Fact]
    public void The_job_folder_keeps_its_references_in_order()
    {
        // Arrange
        var files = new GenerationFiles(_dir.Combine("job"));
        ReferencePack[] packs = [new(Manifest("en"), Table("en", "Fire!", "Feuer!"), true), new(Manifest("uk", patch: true), null, false)];

        // Act
        files.WriteReferences(packs);

        // Assert
        var read = files.ReadReferences();
        read.Packs.Select(p => (p.Manifest.Language, p.Strings?.Strings.Count, p.SameBuild)).Should().Equal(("en", (int?)1, true), ("uk", (int?)null, false));
    }

    [Fact]
    public void A_job_without_references_reads_none()
    {
        // Arrange
        var files = new GenerationFiles(_dir.Combine("job"));

        // Act
        var read = files.ReadReferences();

        // Assert
        read.IsEmpty.Should().BeTrue();
    }
}
