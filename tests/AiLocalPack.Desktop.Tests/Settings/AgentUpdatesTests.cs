using AiLocalPack.Agents;
using AiLocalPack.Core.Activity;
using AiLocalPack.Desktop.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

/// <summary>Updates of the agents the app installed itself: offers, the Activity event, the background check.</summary>
public sealed class AgentUpdatesTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose() => _services.Dispose();

    private AgentUpdates Updates => _services.GetRequiredService<AgentUpdates>();

    private ActivityStore Activity => _services.GetRequiredService<ActivityStore>();

    private FakeAgentHost Host => _services.GetRequiredService<FakeAgentHost>();

    private static AgentInstallation Managed(string version = "2.1.311") => FakeAgentHost.Program("claude", AgentSource.Managed, version);

    [Fact]
    public void A_newer_release_of_the_apps_copy_is_offered()
    {
        // Act
        Updates.Report("claude", Managed(), "2.1.319");

        // Assert
        Updates.OfferFor("claude").Should().Be(new AgentUpdateOffer("claude", "2.1.311", "2.1.319"));
    }

    [Fact]
    public void An_offer_is_one_activity_event_with_the_agent_and_version()
    {
        // Act
        Updates.Report("claude", Managed(), "2.1.319");

        // Assert
        Activity.Feed.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = ActivityEventKind.AgentUpdateAvailable,
            Agent = "claude",
            Version = "2.1.319",
            IsDecision = false,
            Game = (Core.Games.GameId?)null,
        });
    }

    [Fact]
    public void The_same_release_found_again_adds_no_second_event()
    {
        // Arrange
        Updates.Report("claude", Managed(), "2.1.319");
        Updates.Report("claude", Managed("2.1.319"), "2.1.319");

        // Act
        Updates.Report("claude", Managed(), "2.1.319");

        // Assert
        Activity.Feed.Should().ContainSingle();
    }

    [Fact]
    public void The_players_own_copy_gets_no_offer()
    {
        // Act
        Updates.Report("claude", FakeAgentHost.Program("claude", AgentSource.Path, "2.1.311"), "2.1.319");

        // Assert
        (Updates.Offers.Count, Activity.Feed.Count).Should().Be((0, 0));
    }

    [Fact]
    public void An_updated_copy_drops_the_offer()
    {
        // Arrange
        Updates.Report("claude", Managed(), "2.1.319");

        // Act
        Updates.Report("claude", Managed("2.1.319"), "2.1.319");

        // Assert
        Updates.OfferFor("claude").Should().BeNull();
    }

    [Fact]
    public void An_unknown_release_keeps_the_offer()
    {
        // Arrange
        Updates.Report("claude", Managed(), "2.1.319");

        // Act
        Updates.Report("claude", Managed(), null);

        // Assert
        Updates.OfferFor("claude").Should().NotBeNull();
    }

    [Fact]
    public async Task The_background_check_finds_an_update_of_the_apps_copy()
    {
        // Arrange
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex", AgentSource.Managed, "0.167.0");
        Host.Releases["codex"] = FakeAgentHost.Release("codex", "0.170.1");

        // Act
        await Updates.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        Updates.Offers.Should().Equal(new AgentUpdateOffer("codex", "0.167.0", "0.170.1"));
    }

    [Fact]
    public async Task The_background_check_asks_no_release_for_the_players_own_copy()
    {
        // Arrange
        Host.Probes["codex"] = FakeAgentHost.SignedIn("codex", AgentSource.Path, "0.167.0");
        Host.Releases["codex"] = FakeAgentHost.Release("codex", "0.170.1");

        // Act
        await Updates.CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        (Updates.Offers.Count, Host.Calls.Contains("release codex")).Should().Be((0, false));
    }

    [Fact]
    public void A_running_generation_holds_updates_back()
    {
        // Arrange
        Activity.SetTask(new ActivityTask("job", ActivityTaskKind.Generation, null, null, "Generating", null, 0.4, DateTimeOffset.UnixEpoch));

        // Act
        var busy = Updates.IsBusy;

        // Assert
        busy.Should().BeTrue();
    }
}
