using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary><c>claude auth status --json</c> read into a sign-in state (task 41).</summary>
public sealed class ClaudeCodeAuthTests
{
    [Fact]
    public void A_subscription_sign_in_names_its_account()
    {
        // Arrange
        var result = new ProcessResult(0, """{"loggedIn": true, "authMethod": "claude.ai", "email": "player@example.com", "subscriptionType": "pro"}""", string.Empty);

        // Act
        var status = ClaudeCodeAuth.Read(result);

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedIn, "player@example.com"));
    }

    [Fact]
    public void An_api_key_sign_in_says_so()
    {
        // Arrange
        var result = new ProcessResult(0, """{"loggedIn": true, "authMethod": "api_key"}""", string.Empty);

        // Act
        var status = ClaudeCodeAuth.Read(result);

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedIn, "API key"));
    }

    [Fact]
    public void Not_logged_in_is_signed_out()
    {
        // Arrange
        var result = new ProcessResult(1, """{"loggedIn": false, "authMethod": "none", "loginExpiresAt": null}""", string.Empty);

        // Act
        var status = ClaudeCodeAuth.Read(result);

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedOut));
    }

    [Fact]
    public void An_older_claude_code_without_the_command_is_unknown_in_its_words()
    {
        // Arrange
        var result = new ProcessResult(1, string.Empty, "error: unknown command 'auth'\n");

        // Act
        var status = ClaudeCodeAuth.Read(result);

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.Unknown, Detail: "The sign-in check exited with code 1: error: unknown command 'auth'"));
    }

    [Fact]
    public void A_probe_that_timed_out_is_unknown()
    {
        // Arrange
        var result = new ProcessResult(null, string.Empty, string.Empty);

        // Act
        var status = ClaudeCodeAuth.Read(result);

        // Assert
        status.State.Should().Be(AgentAuthState.Unknown);
    }
}
