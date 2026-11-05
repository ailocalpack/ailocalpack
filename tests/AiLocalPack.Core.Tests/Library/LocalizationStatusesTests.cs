using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Library;

public sealed class LocalizationStatusesTests
{
    private static readonly GameId Id = GameId.Steam(620);

    private static Game Game(string buildId = "1000") =>
        new(Id, "Portal 2", GameSource.Steam, "/steam/steamapps/common/Portal 2", "/steam", GameVersion.FromBuildId(buildId), "unknown", "none", null, true);

    private static RegistryPack Pack(
        string id,
        PackStatus status = PackStatus.Alpha,
        int likes = 0,
        string[]? buildIds = null,
        int works = 0,
        int doesNotWork = 0,
        string language = "ru",
        DateTimeOffset? updated = null) =>
        new(
            id,
            Id,
            language,
            id,
            new PackAuthor("alice"),
            status,
            1,
            updated ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new PackCoverage(10, 10),
            buildIds ?? ["2000"],
            1,
            $"games/steam-620/{language}/{id}/manifest.json",
            100,
            new RegistryRating(likes, works, doesNotWork));

    private static RegistryIndex Index(bool optOut = false, bool antiCheatAllowed = false, params RegistryPack[] packs) =>
        new(
            DateTimeOffset.UnixEpoch,
            new Dictionary<GameId, RegistryGame>
            {
                [Id] = new(
                    Id,
                    optOut,
                    antiCheatAllowed,
                    packs.GroupBy(p => p.Language).ToDictionary(g => g.Key, g => (IReadOnlyList<RegistryPack>)[.. g])),
            });

    private static readonly AntiCheatVerdict NoAntiCheat = AntiCheatVerdict.None;
    private static readonly AntiCheatVerdict Eac = new("eac", "Easy Anti-Cheat", false);
    private static readonly AntiCheatVerdict EacAllowedByPolicy = new("eac", "Easy Anti-Cheat", true);

    public enum IndexCase
    {
        None,
        Empty,
        PackInLanguage,
        PackInOtherLanguage,
        OptOut,
        AntiCheatAllowed,
    }

    public enum AntiCheatCase
    {
        None,
        Blocking,
        AllowedByPolicy,
    }

    public enum LocalCase
    {
        Nothing,
        Installed,
        InstalledBroken,
        Generating,
        Paused,
        InstalledAndGenerating,
    }

    public static TheoryData<IndexCase, AntiCheatCase, LocalCase, LocalizationStatusKind, LocalizationBlock> Combinations()
    {
        var data = new TheoryData<IndexCase, AntiCheatCase, LocalCase, LocalizationStatusKind, LocalizationBlock>();
        foreach (var index in Enum.GetValues<IndexCase>())
        {
            foreach (var antiCheat in Enum.GetValues<AntiCheatCase>())
            {
                foreach (var local in Enum.GetValues<LocalCase>())
                {
                    var (kind, block) = Expected(index, antiCheat, local);
                    data.Add(index, antiCheat, local, kind, block);
                }
            }
        }

        return data;
    }

    // The rules, written out independently of the implementation's code path.
    private static (LocalizationStatusKind, LocalizationBlock) Expected(IndexCase index, AntiCheatCase antiCheat, LocalCase local)
    {
        if (index == IndexCase.OptOut)
        {
            return (LocalizationStatusKind.Blocked, LocalizationBlock.OptOut);
        }

        if (antiCheat == AntiCheatCase.Blocking && index != IndexCase.AntiCheatAllowed)
        {
            return (LocalizationStatusKind.Blocked, LocalizationBlock.AntiCheat);
        }

        var kind = local switch
        {
            LocalCase.Generating or LocalCase.InstalledAndGenerating => LocalizationStatusKind.Generating,
            LocalCase.Paused => LocalizationStatusKind.Paused,
            LocalCase.InstalledBroken => LocalizationStatusKind.NeedsUpdate,
            LocalCase.Installed => LocalizationStatusKind.Installed,
            _ => index switch
            {
                IndexCase.None => LocalizationStatusKind.Unknown,
                IndexCase.PackInLanguage => LocalizationStatusKind.PackAvailable,
                _ => LocalizationStatusKind.NoPack,
            },
        };
        return (kind, LocalizationBlock.None);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_combination_gives_its_status(
        IndexCase indexCase,
        AntiCheatCase antiCheatCase,
        LocalCase localCase,
        LocalizationStatusKind expectedKind,
        LocalizationBlock expectedBlock)
    {
        // Arrange
        var index = indexCase switch
        {
            IndexCase.None => null,
            IndexCase.Empty => new RegistryIndex(DateTimeOffset.UnixEpoch, new Dictionary<GameId, RegistryGame>()),
            IndexCase.PackInLanguage => Index(packs: Pack("full")),
            IndexCase.PackInOtherLanguage => Index(packs: Pack("komplett", language: "de")),
            IndexCase.OptOut => Index(optOut: true, packs: Pack("full")),
            _ => Index(antiCheatAllowed: true),
        };
        var antiCheat = antiCheatCase switch
        {
            AntiCheatCase.Blocking => Eac,
            AntiCheatCase.AllowedByPolicy => EacAllowedByPolicy,
            _ => NoAntiCheat,
        };
        var installed = localCase switch
        {
            LocalCase.Installed or LocalCase.InstalledAndGenerating => new InstalledPackState("full", false),
            LocalCase.InstalledBroken => new InstalledPackState("full", true),
            _ => null,
        };
        var job = localCase switch
        {
            LocalCase.Generating or LocalCase.InstalledAndGenerating => new GenerationJobState(42, false),
            LocalCase.Paused => new GenerationJobState(68, true),
            _ => null,
        };

        // Act
        var status = LocalizationStatuses.Resolve(Game(), "ru", antiCheat, index, installed, job);

        // Assert
        (status.Kind, status.Block).Should().Be((expectedKind, expectedBlock));
    }

    [Fact]
    public void Blocking_anti_cheat_carries_its_name()
    {
        // Arrange
        var index = Index(packs: Pack("full"));

        // Act
        var status = LocalizationStatuses.Resolve(Game(), "ru", Eac, index);

        // Assert
        status.AntiCheatName.Should().Be("Easy Anti-Cheat");
    }

    [Fact]
    public void Packs_of_the_language_only_are_listed()
    {
        // Arrange
        var index = Index(packs: [Pack("full"), Pack("komplett", language: "de")]);

        // Act
        var status = LocalizationStatuses.Resolve(Game(), "de", NoAntiCheat, index);

        // Assert
        status.Packs.Select(p => p.Id).Should().Equal("komplett");
    }

    [Fact]
    public void Language_tag_must_match_exactly()
    {
        // Arrange
        var index = Index(packs: Pack("br", language: "pt-BR"));

        // Act
        var status = LocalizationStatuses.Resolve(Game(), "pt-PT", NoAntiCheat, index);

        // Assert
        status.Kind.Should().Be(LocalizationStatusKind.NoPack);
    }

    [Fact]
    public void Best_pack_is_the_one_made_for_the_game_build()
    {
        // Arrange
        var packs = new[] { Pack("popular", PackStatus.Verified, likes: 50), Pack("exact", buildIds: ["1000"]) };

        // Act
        var ranked = LocalizationStatuses.Rank(packs, Game("1000"));

        // Assert
        ranked.Select(p => p.Id).Should().Equal("exact", "popular");
    }

    [Fact]
    public void Verified_packs_rank_before_alpha_ones()
    {
        // Arrange
        var packs = new[] { Pack("liked", likes: 50), Pack("verified", PackStatus.Verified) };

        // Act
        var ranked = LocalizationStatuses.Rank(packs, Game());

        // Assert
        ranked.Select(p => p.Id).Should().Equal("verified", "liked");
    }

    [Fact]
    public void More_likes_rank_first_among_equal_status()
    {
        // Arrange
        var packs = new[] { Pack("few", likes: 2), Pack("many", likes: 9) };

        // Act
        var ranked = LocalizationStatuses.Rank(packs, Game());

        // Assert
        ranked.Select(p => p.Id).Should().Equal("many", "few");
    }

    [Fact]
    public void Reports_break_a_likes_tie()
    {
        // Arrange
        var packs = new[] { Pack("broken", likes: 3, works: 1, doesNotWork: 4), Pack("working", likes: 3, works: 3) };

        // Act
        var ranked = LocalizationStatuses.Rank(packs, Game());

        // Assert
        ranked.Select(p => p.Id).Should().Equal("working", "broken");
    }

    [Fact]
    public void Recently_updated_breaks_the_remaining_tie()
    {
        // Arrange
        var packs = new[]
        {
            Pack("old", updated: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Pack("new", updated: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        // Act
        var ranked = LocalizationStatuses.Rank(packs, Game());

        // Assert
        ranked.Select(p => p.Id).Should().Equal("new", "old");
    }

    [Fact]
    public void Importance_follows_the_design_order()
    {
        // Arrange
        LocalizationStatusKind[] designOrder =
        [
            LocalizationStatusKind.NeedsUpdate, LocalizationStatusKind.Paused, LocalizationStatusKind.Generating,
            LocalizationStatusKind.PackAvailable, LocalizationStatusKind.Installed, LocalizationStatusKind.NoPack,
            LocalizationStatusKind.Blocked,
        ];

        // Act
        var ranks = designOrder.Select(LocalizationStatuses.Importance).ToList();

        // Assert
        ranks.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Unknown_ranks_with_no_pack()
    {
        // Act
        var rank = LocalizationStatuses.Importance(LocalizationStatusKind.Unknown);

        // Assert
        rank.Should().Be(LocalizationStatuses.Importance(LocalizationStatusKind.NoPack));
    }
}
