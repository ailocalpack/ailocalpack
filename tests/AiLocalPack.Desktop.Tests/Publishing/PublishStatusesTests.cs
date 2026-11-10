using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Publishing;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>The publish strip's state of a local pack (task 61, design §7): sending, failed, CI running, published.</summary>
public sealed class PublishStatusesTests
{
    private static readonly GameId Game = GameId.Steam(10);
    private static readonly DateTimeOffset Date = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static LocalPack Pack(bool tried = true, LocalPackPublication? published = null)
    {
        var manifest = new PackManifest(
            "claude-code", Game, "ru", "Курьер", [new PackAuthor(LocalPackStore.PlaceholderAuthor)], [new PackRevision(1, Date, LocalPackStore.PlaceholderAuthor)],
            PackStatus.Alpha, new PackAgent("claude-code"), [new PackBuild("1000", new Dictionary<string, string>())], new PackRecipe("rpgmaker", "1.0.0"),
            null, null, [], [], new PackCoverage(10, 10), Date, Date);
        var report = new LocalPackReport("job", Date, TestRunResult.Passed(TimeSpan.FromSeconds(15)), LocalPackFonts.NotChecked, [], [], 0,
            Tried: tried ? Date : null, Published: published);
        return new LocalPack("/packs/claude-code", manifest, report);
    }

    private static LocalPackPublication Publication(string id = "claude-code", int revision = 1) =>
        new("ailocalpack/registry", id, revision, 7, new Uri("https://github.com/ailocalpack/registry/pull/7"), "octocat/registry", "pack/steam-10/ru/claude-code/rev-1", Date);

    private static RegistryIndex Index(string id, int revision)
    {
        var pack = new RegistryPack(id, Game, "ru", id, new PackAuthor("octocat"), PackStatus.Alpha, revision, Date, new PackCoverage(10, 10), ["1000"], 1,
            $"games/steam-10/ru/{id}/manifest.json", 100, new RegistryRating(0, 1, 0));
        return new RegistryIndex(Date, new Dictionary<GameId, RegistryGame>
        {
            [Game] = new RegistryGame(Game, false, false, new Dictionary<string, IReadOnlyList<RegistryPack>> { ["ru"] = [pack] }),
        });
    }

    private static readonly PublishResult.Failed Offline = new(PublishStep.Fork, PublishProblem.Network, "offline");

    [Fact]
    public void Never_sent_is_not_published()
    {
        // Arrange
        var pack = Pack();

        // Act
        var status = PublishStatuses.Of(pack, null, null, null);

        // Assert
        status.Phase.Should().Be(PublishPhase.NotPublished);
    }

    [Fact]
    public void The_running_publish_of_the_pack_is_sending_at_its_step()
    {
        // Arrange
        var pack = Pack();
        var run = new PublishRun(PublishStatuses.KeyOf(pack), PublishStep.Commit, "Курьер", null);

        // Act
        var status = PublishStatuses.Of(pack, null, run, null);

        // Assert
        (status.Phase, status.Run!.Step).Should().Be((PublishPhase.Sending, PublishStep.Commit));
    }

    [Fact]
    public void Another_packs_publish_leaves_the_pack_as_it_is()
    {
        // Arrange
        var pack = Pack();
        var run = new PublishRun("steam:10/ru/other", PublishStep.Commit, "Other", null);

        // Act
        var status = PublishStatuses.Of(pack, null, run, null);

        // Assert
        status.Phase.Should().Be(PublishPhase.NotPublished);
    }

    [Fact]
    public void A_failed_try_is_failed_with_its_reason()
    {
        // Arrange
        var pack = Pack();

        // Act
        var status = PublishStatuses.Of(pack, null, null, Offline);

        // Assert
        (status.Phase, status.Failure!.Problem).Should().Be((PublishPhase.Failed, PublishProblem.Network));
    }

    [Fact]
    public void A_sent_pack_the_index_doesnt_list_is_checked_by_CI()
    {
        // Arrange
        var pack = Pack(published: Publication());

        // Act
        var status = PublishStatuses.Of(pack, Index("other", 1), null, null);

        // Assert
        (status.Phase, status.Publication!.PullRequest).Should().Be((PublishPhase.Checking, 7));
    }

    [Fact]
    public void A_sent_revision_the_index_lists_at_an_older_revision_is_still_checked()
    {
        // Arrange
        var pack = Pack(published: Publication("neon", 4));

        // Act
        var status = PublishStatuses.Of(pack, Index("neon", 3), null, null);

        // Assert
        status.Phase.Should().Be(PublishPhase.Checking);
    }

    [Fact]
    public void A_sent_revision_the_index_lists_is_published()
    {
        // Arrange
        var pack = Pack(published: Publication("neon", 4));

        // Act
        var status = PublishStatuses.Of(pack, Index("neon", 4), null, null);

        // Assert
        (status.Phase, status.Live!.Revision).Should().Be((PublishPhase.Published, 4));
    }

    [Fact]
    public void Publish_is_not_offered_before_the_pack_was_tried_in_game()
    {
        // Arrange
        var pack = Pack(tried: false);

        // Act
        var can = PublishStatuses.CanPublish(pack, PublishStatuses.Of(pack, null, null, null));

        // Assert
        can.Should().BeFalse();
    }

    [Fact]
    public void Publish_is_offered_for_a_tried_pack_never_sent()
    {
        // Arrange
        var pack = Pack();

        // Act
        var can = PublishStatuses.CanPublish(pack, PublishStatuses.Of(pack, null, null, null));

        // Assert
        can.Should().BeTrue();
    }

    [Fact]
    public void Publish_is_offered_again_after_a_failed_try()
    {
        // Arrange
        var pack = Pack();

        // Act
        var can = PublishStatuses.CanPublish(pack, PublishStatuses.Of(pack, null, null, Offline));

        // Assert
        can.Should().BeTrue();
    }

    [Fact]
    public void Publish_is_not_offered_while_CI_checks_the_pack()
    {
        // Arrange
        var pack = Pack(published: Publication());

        // Act
        var can = PublishStatuses.CanPublish(pack, PublishStatuses.Of(pack, null, null, null));

        // Assert
        can.Should().BeFalse();
    }
}
