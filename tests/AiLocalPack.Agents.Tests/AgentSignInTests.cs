using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests;

/// <summary>Running an agent's own sign-in and waiting for it by polling the status (task 43).</summary>
public sealed class AgentSignInTests
{
    private static readonly AgentSignInOptions Fast = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20));

    private static readonly ProcessSpec Login = new("claude", ["auth", "login"]);

    private static readonly AgentAuthStatus SignedOut = new(AgentAuthState.SignedOut);

    private static readonly AgentAuthStatus SignedIn = new(AgentAuthState.SignedIn, "player@example.com");

    /// <summary>Answers the given states in turn, the last one from then on.</summary>
    private static Func<CancellationToken, Task<AgentAuthStatus>> States(params AgentAuthStatus[] states)
    {
        var next = 0;
        return _ => Task.FromResult(states[Math.Min(next++, states.Length - 1)]);
    }

    [Fact]
    public async Task Already_signed_in_starts_no_login()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of());

        // Act
        var result = await AgentSignIn.RunAsync(runner, Login, States(SignedIn), options: Fast, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Outcome, runner.Started).Should().Be((AgentSignInOutcome.AlreadySignedIn, (ProcessSpec?)null));
    }

    [Fact]
    public async Task Signing_in_in_the_browser_ends_the_wait()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of("Opening browser to sign in…") with { Hangs = true });

        // Act
        var result = await AgentSignIn.RunAsync(runner, Login, States(SignedOut, SignedOut, SignedIn), options: Fast, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AgentSignInResult(AgentSignInOutcome.SignedIn, SignedIn));
    }

    [Fact]
    public async Task A_login_still_running_after_signing_in_is_stopped()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of() with { Hangs = true });

        // Act
        await AgentSignIn.RunAsync(runner, Login, States(SignedOut, SignedIn), options: Fast, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        runner.Process!.Killed.Should().BeTrue();
    }

    [Fact]
    public async Task The_logins_lines_are_passed_on()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of("If the browser didn't open, visit: https://example.com/oauth") with { Hangs = true });
        var seen = new List<string>();

        // Act
        await AgentSignIn.RunAsync(runner, Login, States(SignedOut, SignedOut, SignedOut, SignedIn), line => { lock (seen) { seen.Add(line.Text); } }, Fast, TestContext.Current.CancellationToken);

        // Assert
        seen.Should().Equal("If the browser didn't open, visit: https://example.com/oauth");
    }

    [Fact]
    public async Task A_login_ending_without_signing_in_says_how_it_ended()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(new ScriptedOutput([new ProcessLine(ProcessStream.StandardError, "OAuth error: access denied")], ExitCode: 1));

        // Act
        var result = await AgentSignIn.RunAsync(runner, Login, States(SignedOut), options: Fast, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AgentSignInResult(AgentSignInOutcome.Exited, SignedOut, "The login exited with code 1 without signing in: OAuth error: access denied"));
    }

    [Fact]
    public async Task A_login_past_the_time_limit_is_stopped()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of() with { Hangs = true });

        // Act
        var result = await AgentSignIn.RunAsync(runner, Login, States(SignedOut), options: Fast with { Timeout = TimeSpan.FromMilliseconds(50) }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Outcome, runner.Process!.Killed).Should().Be((AgentSignInOutcome.TimedOut, true));
    }

    [Fact]
    public async Task Cancelling_stops_the_login()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of() with { Hangs = true });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        var result = await AgentSignIn.RunAsync(runner, Login, States(SignedOut), options: Fast, cancellationToken: cancel.Token);

        // Assert
        (result.Outcome, runner.Process!.Killed).Should().Be((AgentSignInOutcome.Cancelled, true));
    }

    [Fact]
    public async Task A_login_that_cant_start_says_why()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of()) { StartError = new ProcessStartException("claude: no such file") };

        // Act
        var result = await AgentSignIn.RunAsync(runner, Login, States(SignedOut), options: Fast, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        (result.Outcome, result.Detail).Should().Be((AgentSignInOutcome.StartFailed, "claude: no such file"));
    }
}
