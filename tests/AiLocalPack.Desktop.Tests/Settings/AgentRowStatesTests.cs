using AiLocalPack.Agents;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class AgentRowStatesTests
{
    [Fact]
    public void No_program_is_not_installed()
    {
        // Act
        var state = AgentRowStates.From(AgentProbe.Missing, wasSignedIn: true);

        // Assert
        state.Should().Be(AgentRowState.NotInstalled);
    }

    [Fact]
    public void A_version_the_app_cant_drive_is_too_old_whatever_the_sign_in()
    {
        // Arrange
        var probe = FakeAgentHost.SignedIn("claude", version: "2.0.1") with { IsSupported = false };

        // Act
        var state = AgentRowStates.From(probe, wasSignedIn: false);

        // Assert
        state.Should().Be(AgentRowState.TooOld);
    }

    [Fact]
    public void Signed_in_is_ready()
    {
        // Act
        var state = AgentRowStates.From(FakeAgentHost.SignedIn("claude"), wasSignedIn: false);

        // Assert
        state.Should().Be(AgentRowState.Ready);
    }

    [Fact]
    public void Signed_out_and_never_seen_signed_in_is_not_signed_in()
    {
        // Act
        var state = AgentRowStates.From(FakeAgentHost.SignedOut("claude"), wasSignedIn: false);

        // Assert
        state.Should().Be(AgentRowState.NotSignedIn);
    }

    [Fact]
    public void Signed_out_after_being_seen_signed_in_is_an_expired_session()
    {
        // Act
        var state = AgentRowStates.From(FakeAgentHost.SignedOut("claude"), wasSignedIn: true);

        // Assert
        state.Should().Be(AgentRowState.SessionExpired);
    }

    [Fact]
    public void A_sign_in_that_couldnt_be_checked_is_unknown()
    {
        // Arrange
        var probe = new AgentProbe(FakeAgentHost.Program("codex"), true, new AgentAuthStatus(AgentAuthState.Unknown, Detail: "timed out"));

        // Act
        var state = AgentRowStates.From(probe, wasSignedIn: true);

        // Assert
        state.Should().Be(AgentRowState.SignInUnknown);
    }

    [Theory]
    [InlineData(AgentRowState.SessionExpired, true)]
    [InlineData(AgentRowState.NotInstalled, false)]
    [InlineData(AgentRowState.NotSignedIn, false)]
    [InlineData(AgentRowState.TooOld, false)]
    [InlineData(AgentRowState.SignInUnknown, false)]
    [InlineData(AgentRowState.Ready, false)]
    public void Only_an_expired_session_is_a_warning(AgentRowState state, bool warning)
    {
        // Act
        var isWarning = AgentRowStates.IsWarning(state);

        // Assert
        isWarning.Should().Be(warning);
    }

    [Theory]
    [InlineData("2.1.320", "2.1.319", true)]
    [InlineData("2.1.319", "2.1.319", false)]
    [InlineData("2.1.300", "2.1.319", false)]
    [InlineData("0.168.0", "0.167.0-alpha.1", true)]
    [InlineData(null, "0.167.0", false)]
    public void A_release_is_an_update_only_when_its_version_is_later(string? candidate, string installed, bool newer)
    {
        // Act
        var isNewer = AgentVersions.IsNewer(candidate, installed);

        // Assert
        isNewer.Should().Be(newer);
    }

    [Theory]
    [InlineData("claude", "2.1.259", true)]
    [InlineData("claude", "2.1.258", false)]
    [InlineData("codex", "0.1.0", true)]
    public void Claude_code_needs_its_minimum_version(string agent, string version, bool supported)
    {
        // Act
        var isSupported = AgentVersions.IsSupported(agent, version);

        // Assert
        isSupported.Should().Be(supported);
    }
}
