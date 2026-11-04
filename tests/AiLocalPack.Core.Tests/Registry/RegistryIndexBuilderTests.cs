using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Registry;

public sealed class RegistryIndexBuilderTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IReadOnlyDictionary<RegistryPackKey, RegistryPackRating> SampleRatings =>
        RegistryRatings.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "registry-ratings.json"))).Ratings!;

    /// <summary>A copy of the sample registry; returns its root.</summary>
    private string CopyRegistry()
    {
        _dir.CopyFolder(RegistryFixtures.Folder, "registry");
        return _dir.Combine("registry");
    }

    [Fact]
    public void Sample_registry_builds_the_sample_index()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, SampleRatings, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        RegistryIndexSerializer.Write(result.Index!).Should().Be(RegistryFixtures.ReadIndex().ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    [Fact]
    public void Sample_registry_builds_without_problems()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, SampleRatings, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_pack_is_left_out_and_the_rest_kept()
    {
        // Arrange
        var root = CopyRegistry();
        File.WriteAllText(Path.Combine(root, "games/steam-620/ru/menus/manifest.json"), "{");

        // Act
        var result = RegistryIndexBuilder.Build(root, Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Index!.PackCount, result.Problems.Single().File).Should().Be((3, "games/steam-620/ru/menus/manifest.json"));
    }

    [Fact]
    public void Stray_file_in_the_games_tree_is_reported()
    {
        // Arrange
        var root = CopyRegistry();
        _dir.File("registry/games/steam-620/notes.txt", "x");

        // Act
        var result = RegistryIndexBuilder.Build(root, Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.File.Should().Be("games/steam-620/notes.txt");
    }

    [Fact]
    public void Ratings_fill_in_the_pack()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, SampleRatings, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Index!.PacksFor(GameId.Steam(620), "ru").Single(p => p.Id == "full").Should().Match<RegistryPack>(p =>
            p.Rating == new RegistryRating(12, 5, 1) && p.Discussion!.AbsoluteUri == "https://github.com/ailocalpack/registry/discussions/1");
    }

    [Fact]
    public void Pack_without_ratings_has_none()
    {
        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Index!.Packs.Should().OnlyContain(p => p.Rating == RegistryRating.None && p.Discussion == null);
    }

    [Fact]
    public void Ratings_of_a_missing_pack_are_reported()
    {
        // Arrange
        var ratings = new Dictionary<RegistryPackKey, RegistryPackRating>
        {
            [new RegistryPackKey(GameId.Steam(620), "fr", "gone")] = new(new RegistryRating(1, 0, 0), null),
        };

        // Act
        var result = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, ratings, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("steam:620/fr/gone");
    }

    [Fact]
    public void Policy_files_set_the_game_flags()
    {
        // Act
        var index = RegistryIndexBuilder.Build(RegistryFixtures.Folder, Generated, cancellationToken: TestContext.Current.CancellationToken).Index!;

        // Assert
        (index.Find(GameId.Steam(400))!.OptOut, index.Find(GameId.Steam(570))!.AntiCheatAllowed).Should().Be((true, true));
    }

    [Fact]
    public void Broken_opt_out_list_stops_the_build()
    {
        // Arrange
        var root = CopyRegistry();
        _dir.File("registry/policy/opt-out.json", """{"schemaVersion": 1, "games": [{"game": "steam:x"}]}""");

        // Act
        var result = RegistryIndexBuilder.Build(root, Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Index, result.Problems.Single().File).Should().Be((null, "policy/opt-out.json"));
    }

    [Fact]
    public void Broken_anti_cheat_policy_stops_the_build()
    {
        // Arrange
        var root = CopyRegistry();
        _dir.File("registry/policy/anti-cheat.json", "{");

        // Act
        var result = RegistryIndexBuilder.Build(root, Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Index, result.Problems.Single().File).Should().Be((null, "policy/anti-cheat.json"));
    }

    [Fact]
    public void Registry_without_games_builds_an_empty_index()
    {
        // Arrange
        _dir.Dir("empty");

        // Act
        var result = RegistryIndexBuilder.Build(_dir.Combine("empty"), Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Index!.Games.Count, result.Problems.Count).Should().Be((0, 0));
    }

    [Fact]
    public void Missing_registry_folder_builds_nothing()
    {
        // Act
        var result = RegistryIndexBuilder.Build(_dir.Combine("missing"), Generated, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Index.Should().BeNull();
    }
}
