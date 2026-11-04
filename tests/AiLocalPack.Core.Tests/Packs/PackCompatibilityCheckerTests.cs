using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Packs;

public sealed class PackCompatibilityCheckerTests : IDisposable
{
    private const string Map = "www/data/Map001.json";
    private const string System = "www/data/System.json";

    private static readonly GameId SteamGame = GameId.Steam(620);
    private static readonly GameId ManualGame = GameId.Fingerprint(new string('3', 64));
    private static readonly DateTimeOffset Date = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static PackBuild Build(string? buildId, params (string Path, string Content)[] files) =>
        new(buildId, files.ToDictionary(f => f.Path, f => Sha256(f.Content), StringComparer.Ordinal));

    private static PackManifest Pack(GameId game, params PackBuild[] builds) =>
        new(
            "full",
            game,
            "ru",
            "Full translation",
            [new PackAuthor("alice")],
            [new PackRevision(1, Date, "alice")],
            PackStatus.Verified,
            null,
            builds,
            new PackRecipe("rpgmaker", "1.0.0"),
            new PackStringsFile(PackStringsFile.DefaultPath, new string('a', 64), 100),
            null,
            [],
            [],
            new PackCoverage(10, 10),
            Date,
            Date);

    private static PackManifest PatchPack(PackBuild build) =>
        Pack(SteamGame, build) with
        {
            Recipe = null,
            Strings = null,
            Patch = new PackPatch(
                PackPatch.VcdiffFormat,
                new Uri("https://example.com/patch.vcdiff"),
                new string('b', 64),
                100,
                build.Files.ToDictionary(f => f.Key, _ => new string('c', 64))),
        };

    private Game Game(GameId id, string? buildId = null, bool available = true) =>
        new(
            id,
            "Game",
            id.Kind == GameIdKind.Steam ? GameSource.Steam : GameSource.Manual,
            _dir.Combine("game"),
            null,
            buildId is null ? null : GameVersion.FromBuildId(buildId),
            "rpgmaker:mv",
            "none",
            null,
            available);

    private void GameFile(string path, string content) => _dir.File("game/" + path, content);

    [Fact]
    public void A_listed_build_id_is_compatible_without_comparing_files()
    {
        // Arrange
        GameFile(Map, "translated by an older pack");
        GameFile(System, "system");
        var pack = Pack(SteamGame, Build("100", (Map, "map v1"), (System, "system")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Compatible,
            Reason = PackCompatibilityReason.BuildListed,
            GameBuildId = "100",
            ReferenceBuildId = "100",
            TouchedFileCount = 2,
            ChangedFiles = Array.Empty<string>(),
        });
    }

    [Fact]
    public void A_listed_build_with_a_touched_file_missing_is_incompatible()
    {
        // Arrange
        GameFile(System, "system");
        var pack = Pack(SteamGame, Build("100", (Map, "map v1"), (System, "system")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Incompatible,
            Reason = PackCompatibilityReason.FilesMissing,
            MissingFiles = new[] { Map },
        });
    }

    [Fact]
    public void An_unlisted_build_id_whose_files_match_a_listed_build_is_compatible()
    {
        // Arrange
        GameFile(Map, "map v2");
        GameFile(System, "system");
        var pack = Pack(SteamGame, Build("100", (Map, "map v1"), (System, "system")), Build("200", (Map, "map v2"), (System, "system")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "300"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Compatible,
            Reason = PackCompatibilityReason.FilesMatch,
            PackBuildIds = new[] { "100", "200" },
            GameBuildId = "300",
            ReferenceBuildId = "200",
        });
    }

    [Fact]
    public void A_manual_game_whose_files_match_is_compatible()
    {
        // Arrange
        GameFile(Map, "map v1");
        var pack = Pack(ManualGame, Build(null, (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(ManualGame), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Compatible,
            Reason = PackCompatibilityReason.FilesMatch,
            PackBuildIds = Array.Empty<string>(),
            GameBuildId = (string?)null,
            ReferenceBuildId = (string?)null,
            TouchedFileCount = 1,
        });
    }

    [Fact]
    public void Changed_files_of_a_recipe_pack_are_probably_compatible_against_the_closest_build()
    {
        // Arrange
        GameFile(Map, "map v3");
        GameFile(System, "system v2");
        GameFile("www/data/Items.json", "items");
        var pack = Pack(
            SteamGame,
            Build("100", (Map, "map v1"), (System, "system v1"), ("www/data/Items.json", "old items")),
            Build("200", (Map, "map v2"), (System, "system v2"), ("www/data/Items.json", "items")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "300"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.ProbablyCompatible,
            Reason = PackCompatibilityReason.FilesChanged,
            ReferenceBuildId = "200",
            TouchedFileCount = 3,
            ChangedFiles = new[] { Map },
        });
    }

    [Fact]
    public void A_manual_game_with_changed_files_is_probably_compatible()
    {
        // Arrange
        GameFile(Map, "map v2");
        var pack = Pack(ManualGame, Build(null, (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(ManualGame), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.ProbablyCompatible,
            ChangedFiles = new[] { Map },
        });
    }

    [Fact]
    public void Changed_files_of_a_binary_patch_are_incompatible()
    {
        // Arrange
        GameFile(Map, "map v2");
        var pack = PatchPack(Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "300"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Incompatible,
            Reason = PackCompatibilityReason.PatchFilesChanged,
            ChangedFiles = new[] { Map },
        });
    }

    [Fact]
    public void A_binary_patch_over_the_exact_files_is_compatible()
    {
        // Arrange
        GameFile(Map, "map v1");
        var pack = PatchPack(Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "300"), Ct);

        // Assert
        result.Verdict.Should().Be(PackCompatibilityVerdict.Compatible);
    }

    [Fact]
    public void Missing_touched_files_in_every_build_are_incompatible_against_the_closest_build()
    {
        // Arrange
        GameFile(System, "system");
        var pack = Pack(
            SteamGame,
            Build("100", (Map, "map v1"), (System, "system"), ("www/data/Items.json", "items")),
            Build("200", (Map, "map v2"), (System, "system")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "300"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Incompatible,
            Reason = PackCompatibilityReason.FilesMissing,
            ReferenceBuildId = "200",
            TouchedFileCount = 2,
            MissingFiles = new[] { Map },
        });
    }

    [Fact]
    public void A_build_with_files_missing_is_skipped_when_another_build_is_complete()
    {
        // Arrange
        GameFile(Map, "map v2");
        var pack = Pack(ManualGame, Build(null, (Map, "map v1"), (System, "system")), Build(null, (Map, "map v2")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(ManualGame), Ct);

        // Assert
        result.Reason.Should().Be(PackCompatibilityReason.FilesMatch);
    }

    [Fact]
    public void A_pack_for_another_game_is_incompatible()
    {
        // Arrange
        GameFile(Map, "map v1");
        var pack = Pack(GameId.Steam(400), Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100"), Ct);

        // Assert
        result.Reason.Should().Be(PackCompatibilityReason.WrongGame);
    }

    [Fact]
    public void An_unavailable_game_is_incompatible()
    {
        // Arrange
        GameFile(Map, "map v1");
        var pack = Pack(SteamGame, Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100", available: false), Ct);

        // Assert
        result.Reason.Should().Be(PackCompatibilityReason.GameNotAvailable);
    }

    [Fact]
    public void A_game_folder_that_is_gone_is_incompatible()
    {
        // Arrange
        var pack = Pack(SteamGame, Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100"), Ct);

        // Assert
        result.Reason.Should().Be(PackCompatibilityReason.GameNotAvailable);
    }

    [Theory]
    [InlineData(RecipeToolSupport.UnknownTool, PackCompatibilityReason.RecipeToolUnknown)]
    [InlineData(RecipeToolSupport.TooNew, PackCompatibilityReason.RecipeToolTooNew)]
    public void A_recipe_the_toolkit_cannot_run_is_incompatible(RecipeToolSupport support, PackCompatibilityReason expected)
    {
        // Arrange
        GameFile(Map, "map v1");
        var pack = Pack(SteamGame, Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache(), new FakeRecipeTools(support));

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100"), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Incompatible,
            Reason = expected,
            RecipeTool = new { Tool = "rpgmaker", Version = "1.0.0" },
        });
    }

    [Fact]
    public void A_supported_recipe_is_checked_further()
    {
        // Arrange
        GameFile(Map, "map v1");
        var pack = Pack(SteamGame, Build("100", (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache(), new FakeRecipeTools(RecipeToolSupport.Supported));

        // Act
        var result = checker.Check(pack, Game(SteamGame, "100"), Ct);

        // Assert
        result.Reason.Should().Be(PackCompatibilityReason.BuildListed);
    }

    [Fact]
    public void An_unreadable_touched_file_is_incompatible()
    {
        // Arrange
        GameFile(Map, "map v1");
        var path = _dir.Combine("game/" + Map);
        var pack = Pack(ManualGame, Build(null, (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());
        using var lockStream = OperatingSystem.IsWindows() ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        PackCompatibility result;
        try
        {
            // Act
            result = checker.Check(pack, Game(ManualGame), Ct);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.Incompatible,
            Reason = PackCompatibilityReason.FileUnreadable,
            UnreadableFile = Map,
        });
    }

    [Fact]
    public void Touched_files_are_hashed_through_the_cache()
    {
        // Arrange
        GameFile(Map, "map v1");
        var path = _dir.Combine("game/" + Map);
        var pack = Pack(ManualGame, Build(null, (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());
        checker.Check(pack, Game(ManualGame), Ct);
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "map v2");
        File.SetLastWriteTimeUtc(path, time);

        // Act
        var result = checker.Check(pack, Game(ManualGame), Ct);

        // Assert
        result.Verdict.Should().Be(PackCompatibilityVerdict.Compatible);
    }

    [Fact]
    public void A_touched_file_with_a_new_write_time_is_hashed_again()
    {
        // Arrange
        GameFile(Map, "map v1");
        var path = _dir.Combine("game/" + Map);
        var pack = Pack(ManualGame, Build(null, (Map, "map v1")));
        var checker = new PackCompatibilityChecker(new FileHashCache());
        checker.Check(pack, Game(ManualGame), Ct);
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "map v2");
        File.SetLastWriteTimeUtc(path, time.AddMinutes(1));

        // Act
        var result = checker.Check(pack, Game(ManualGame), Ct);

        // Assert
        result.Should().BeEquivalentTo(new
        {
            Verdict = PackCompatibilityVerdict.ProbablyCompatible,
            ChangedFiles = new[] { Map },
        });
    }

    [Fact]
    public void Only_the_touched_files_are_hashed()
    {
        // Arrange
        GameFile(Map, "map v1");
        GameFile("www/data/Other.json", "not touched");
        var pack = Pack(ManualGame, Build(null, (Map, "map v1")));
        var cache = new FileHashCache();
        var checker = new PackCompatibilityChecker(cache);

        // Act
        checker.Check(pack, Game(ManualGame), Ct);

        // Assert
        cache.Count.Should().Be(1);
    }

    private sealed class FakeRecipeTools(RecipeToolSupport support) : IRecipeTools
    {
        public RecipeToolSupport Check(PackRecipe recipe) => support;
    }
}
