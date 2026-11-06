using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Library;

public sealed class BestPackTests
{
    private static readonly GameId Id = GameId.Steam(620);

    private static RegistryPack Pack(string id) =>
        new(id, Id, "ru", id, new PackAuthor("alice"), PackStatus.Verified, 1, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new PackCoverage(10, 10), ["1000"], 1, $"games/steam-620/ru/{id}/manifest.json", 100, new RegistryRating(0, 0, 0));

    private static PackCompatibility Verdict(PackCompatibilityVerdict verdict) =>
        new(verdict, PackCompatibilityReason.FilesChanged, [], null, null, 1, [], []);

    /// <summary>A check answering from <paramref name="verdicts"/> (absent = manifest unreadable) and recording what it checked.</summary>
    private static Func<RegistryPack, CancellationToken, Task<PackCompatibility?>> Check(
        Dictionary<string, PackCompatibilityVerdict> verdicts, List<string> checkedIds) =>
        (pack, _) =>
        {
            checkedIds.Add(pack.Id);
            return Task.FromResult(verdicts.TryGetValue(pack.Id, out var verdict) ? Verdict(verdict) : null);
        };

    [Fact]
    public async Task First_compatible_pack_is_chosen()
    {
        // Arrange
        var verdicts = new Dictionary<string, PackCompatibilityVerdict> { ["a"] = PackCompatibilityVerdict.Compatible, ["b"] = PackCompatibilityVerdict.Compatible };

        // Act
        var choice = await BestPack.PickAsync([Pack("a"), Pack("b")], Check(verdicts, []), TestContext.Current.CancellationToken);

        // Assert
        choice!.Pack.Id.Should().Be("a");
    }

    [Fact]
    public async Task Checking_stops_at_the_first_compatible_pack()
    {
        // Arrange
        var verdicts = new Dictionary<string, PackCompatibilityVerdict> { ["a"] = PackCompatibilityVerdict.Compatible, ["b"] = PackCompatibilityVerdict.Compatible };
        var checkedIds = new List<string>();

        // Act
        await BestPack.PickAsync([Pack("a"), Pack("b")], Check(verdicts, checkedIds), TestContext.Current.CancellationToken);

        // Assert
        checkedIds.Should().Equal("a");
    }

    [Fact]
    public async Task Compatible_pack_lower_in_rank_beats_a_probably_compatible_one()
    {
        // Arrange
        var verdicts = new Dictionary<string, PackCompatibilityVerdict> { ["a"] = PackCompatibilityVerdict.ProbablyCompatible, ["b"] = PackCompatibilityVerdict.Compatible };

        // Act
        var choice = await BestPack.PickAsync([Pack("a"), Pack("b")], Check(verdicts, []), TestContext.Current.CancellationToken);

        // Assert
        choice!.Pack.Id.Should().Be("b");
    }

    [Fact]
    public async Task Without_a_compatible_pack_the_first_probably_compatible_one_is_chosen()
    {
        // Arrange
        var verdicts = new Dictionary<string, PackCompatibilityVerdict>
        {
            ["a"] = PackCompatibilityVerdict.Incompatible,
            ["b"] = PackCompatibilityVerdict.ProbablyCompatible,
            ["c"] = PackCompatibilityVerdict.ProbablyCompatible,
        };

        // Act
        var choice = await BestPack.PickAsync([Pack("a"), Pack("b"), Pack("c")], Check(verdicts, []), TestContext.Current.CancellationToken);

        // Assert
        (choice!.Pack.Id, choice.Compatibility.Verdict).Should().Be(("b", PackCompatibilityVerdict.ProbablyCompatible));
    }

    [Fact]
    public async Task Unreadable_packs_are_skipped()
    {
        // Arrange
        var verdicts = new Dictionary<string, PackCompatibilityVerdict> { ["b"] = PackCompatibilityVerdict.Compatible };

        // Act
        var choice = await BestPack.PickAsync([Pack("a"), Pack("b")], Check(verdicts, []), TestContext.Current.CancellationToken);

        // Assert
        choice!.Pack.Id.Should().Be("b");
    }

    [Fact]
    public async Task Only_incompatible_or_unreadable_packs_give_no_choice()
    {
        // Arrange
        var verdicts = new Dictionary<string, PackCompatibilityVerdict> { ["a"] = PackCompatibilityVerdict.Incompatible };

        // Act
        var choice = await BestPack.PickAsync([Pack("a"), Pack("b")], Check(verdicts, []), TestContext.Current.CancellationToken);

        // Assert
        choice.Should().BeNull();
    }

    [Fact]
    public async Task No_packs_give_no_choice()
    {
        // Act
        var choice = await BestPack.PickAsync([], Check([], []), TestContext.Current.CancellationToken);

        // Assert
        choice.Should().BeNull();
    }
}
