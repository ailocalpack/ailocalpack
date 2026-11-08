using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Codex;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Agents.Tests.Claude;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests;

/// <summary>The agents' own sign-in and sign-out commands, and the app's copy of Claude Code not updating itself (task 43).</summary>
public sealed class AgentAccountTests
{
    private static readonly OsEnvironment Machine = new(OsKind.Linux, "/home/p", name => name == "PATH" ? "/usr/bin" : null);

    private static readonly AgentSignInOptions Fast = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10));

    private static readonly AgentInstallation Claude = new("claude", "/home/p/.local/bin/claude", "2.1.311", AgentSource.Path);

    private static readonly AgentInstallation Codex = new("codex", "/home/p/.local/bin/codex", "0.167.0", AgentSource.Path);

    private static bool Is(ProcessSpec spec, params string[] arguments) => spec.Arguments.SequenceEqual(arguments);

    /// <summary>Claude Code: signed out until its login has been started, then signed in; the login itself hangs.</summary>
    private static RoutingProcessRunner ClaudeRunner()
    {
        var loggedIn = false;
        return new RoutingProcessRunner(spec =>
        {
            if (Is(spec, "auth", "login"))
            {
                loggedIn = true;
                return ScriptedOutput.Of("Opening browser to sign in…") with { Hangs = true };
            }

            if (Is(spec, "auth", "logout"))
            {
                loggedIn = false;
                return ScriptedOutput.Of("Successfully logged out");
            }

            return loggedIn
                ? ScriptedOutput.Of("""{"loggedIn": true, "email": "player@example.com"}""")
                : ScriptedOutput.Of("""{"loggedIn": false}""") with { ExitCode = 1 };
        });
    }

    [Fact]
    public async Task Claude_code_signs_in_with_its_own_auth_login()
    {
        // Arrange
        var runner = ClaudeRunner();
        var adapter = new ClaudeCodeAdapter(runner, Machine) { SignInOptions = Fast };

        // Act
        var result = await adapter.SignInAsync(Claude, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Outcome, result.Status?.Account, runner.Started.Count(s => Is(s, "auth", "login"))).Should().Be((AgentSignInOutcome.SignedIn, "player@example.com", 1));
    }

    [Fact]
    public async Task Claude_code_signs_out_with_its_own_auth_logout()
    {
        // Arrange
        var runner = ClaudeRunner();
        var adapter = new ClaudeCodeAdapter(runner, Machine) { SignInOptions = Fast };
        await adapter.SignInAsync(Claude, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var status = await adapter.SignOutAsync(Claude, TestContext.Current.CancellationToken);

        // Assert
        status.State.Should().Be(AgentAuthState.SignedOut);
    }

    [Fact]
    public async Task The_apps_copy_of_claude_code_never_updates_itself()
    {
        // Arrange
        var runner = ClaudeRunner();
        var adapter = new ClaudeCodeAdapter(runner, Machine);

        // Act
        await adapter.GetAuthStatusAsync(Claude with { Source = AgentSource.Managed }, TestContext.Current.CancellationToken);

        // Assert
        runner.Started[0].Environment.GetValueOrDefault(ClaudeCodeAdapter.DisableAutoUpdaterVariable).Should().Be("1");
    }

    [Fact]
    public async Task The_players_own_claude_code_keeps_its_updater()
    {
        // Arrange
        var runner = ClaudeRunner();
        var adapter = new ClaudeCodeAdapter(runner, Machine);

        // Act
        await adapter.GetAuthStatusAsync(Claude, TestContext.Current.CancellationToken);

        // Assert
        runner.Started[0].Environment.ContainsKey(ClaudeCodeAdapter.DisableAutoUpdaterVariable).Should().BeFalse();
    }

    [Fact]
    public async Task Codex_signs_in_with_its_own_login()
    {
        // Arrange
        var loggedIn = false;
        var runner = new RoutingProcessRunner(spec =>
        {
            if (Is(spec, "login"))
            {
                loggedIn = true;
                return ScriptedOutput.Of("Starting local login server on http://localhost:1455.") with { Hangs = true };
            }

            return loggedIn
                ? new ScriptedOutput([new ProcessLine(ProcessStream.StandardError, "Logged in using ChatGPT")])
                : new ScriptedOutput([new ProcessLine(ProcessStream.StandardError, "Not logged in")], ExitCode: 1);
        });
        var adapter = new CodexAdapter(runner, Machine) { SignInOptions = Fast };

        // Act
        var result = await adapter.SignInAsync(Codex, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Outcome, result.Status?.Account).Should().Be((AgentSignInOutcome.SignedIn, "ChatGPT"));
    }

    [Fact]
    public async Task Codex_signs_out_with_its_own_logout()
    {
        // Arrange
        var runner = new RoutingProcessRunner(spec => Is(spec, "logout")
            ? ScriptedOutput.Of("Successfully logged out")
            : new ScriptedOutput([new ProcessLine(ProcessStream.StandardError, "Not logged in")], ExitCode: 1));
        var adapter = new CodexAdapter(runner, Machine);

        // Act
        var status = await adapter.SignOutAsync(Codex, TestContext.Current.CancellationToken);

        // Assert
        (status.State, runner.Started.Any(s => Is(s, "logout"))).Should().Be((AgentAuthState.SignedOut, true));
    }
}
