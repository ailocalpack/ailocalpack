using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests;

/// <summary>The runs every adapter's transcripts must cover (task 40).</summary>
public enum AgentContractScenario
{
    /// <summary>Session, a message, usage and a successful result.</summary>
    Success,

    /// <summary>A tool call and its result, then a successful result.</summary>
    ToolCall,

    /// <summary>The usage limit is reached, with a reset time.</summary>
    LimitReached,

    /// <summary>The agent isn't signed in.</summary>
    SignedOut,

    /// <summary>A successful run with a line in the middle that isn't the agent's format.</summary>
    MalformedLine,

    /// <summary>The process writes to standard error and exits with a non-zero code, no result.</summary>
    CrashWithoutResult,

    /// <summary>Some output, then the process waits forever.</summary>
    Hang,
}

/// <summary>
/// The normalized event contract shared by all adapters (task 40): a subclass gives the adapter and its own transcript of
/// each <see cref="AgentContractScenario"/>; the tests here check the events are the same whatever the agent.
/// </summary>
public abstract class AgentAdapterContractTests
{
    /// <summary>The adapter under test, starting its processes through <paramref name="runner"/>.</summary>
    protected abstract IAgentAdapter CreateAdapter(IProcessRunner runner);

    /// <summary>The agent's own output for <paramref name="scenario"/>.</summary>
    protected abstract ScriptedOutput Transcript(AgentContractScenario scenario);

    protected static AgentInstallation Installation => new("test", "/opt/agent", "1.0", AgentSource.Configured);

    protected static AgentRunRequest Request => new("Translate the strings.", Path.GetTempPath());

    private async Task<(List<AgentEvent> Events, ScriptedProcessRunner Runner)> RunAsync(AgentContractScenario scenario)
    {
        var runner = new ScriptedProcessRunner(Transcript(scenario));
        var events = new List<AgentEvent>();
        using var cancel = new CancellationTokenSource();
        await foreach (var e in CreateAdapter(runner).RunAsync(Installation, Request, cancel.Token))
        {
            events.Add(e);
            if (scenario == AgentContractScenario.Hang)
            {
                await cancel.CancelAsync();
            }
        }

        return (events, runner);
    }

    private static AgentDone Done(List<AgentEvent> events) => events.OfType<AgentDone>().Single();

    [Theory]
    [InlineData(AgentContractScenario.Success)]
    [InlineData(AgentContractScenario.ToolCall)]
    [InlineData(AgentContractScenario.LimitReached)]
    [InlineData(AgentContractScenario.SignedOut)]
    [InlineData(AgentContractScenario.MalformedLine)]
    [InlineData(AgentContractScenario.CrashWithoutResult)]
    [InlineData(AgentContractScenario.Hang)]
    public async Task Every_run_ends_with_exactly_one_done_event(AgentContractScenario scenario)
    {
        // Act
        var (events, _) = await RunAsync(scenario);

        // Assert
        events.Where(e => e is AgentDone).Should().ContainSingle().Which.Should().BeSameAs(events[^1]);
    }

    [Fact]
    public async Task A_successful_run_ends_succeeded_with_its_result()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.Success);

        // Assert
        Done(events).Should().Match<AgentDone>(d => d.Outcome == AgentOutcome.Succeeded && !string.IsNullOrEmpty(d.Result));
    }

    [Fact]
    public async Task A_successful_run_reports_the_agents_text()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.Success);

        // Assert
        events.OfType<AgentMessage>().Should().Contain(m => m.Text.Length > 0);
    }

    [Fact]
    public async Task A_successful_run_reports_token_usage()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.Success);

        // Assert
        events.OfType<AgentUsage>().Should().Contain(u => u.InputTokens > 0 && u.OutputTokens > 0);
    }

    [Fact]
    public async Task A_tool_result_answers_an_earlier_tool_call()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.ToolCall);

        // Assert
        var call = events.OfType<AgentToolCall>().Should().ContainSingle().Subject;
        events.Skip(events.IndexOf(call)).OfType<AgentToolResult>().Should().ContainSingle(r => r.CallId == call.Id);
    }

    [Fact]
    public async Task A_reached_limit_names_its_reset_time()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.LimitReached);

        // Assert
        events.OfType<AgentLimitReached>().Should().ContainSingle().Which.ResetsAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_reached_limit_ends_the_run_as_limit_reached()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.LimitReached);

        // Assert
        Done(events).Outcome.Should().Be(AgentOutcome.LimitReached);
    }

    [Fact]
    public async Task A_signed_out_agent_reports_it_as_an_error()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.SignedOut);

        // Assert
        events.OfType<AgentError>().Should().Contain(e => e.Kind == AgentErrorKind.SignedOut);
    }

    [Fact]
    public async Task A_signed_out_agent_ends_the_run_as_signed_out()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.SignedOut);

        // Assert
        Done(events).Outcome.Should().Be(AgentOutcome.SignedOut);
    }

    [Fact]
    public async Task A_malformed_line_is_reported()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.MalformedLine);

        // Assert
        events.OfType<AgentError>().Should().ContainSingle(e => e.Kind == AgentErrorKind.Malformed);
    }

    [Fact]
    public async Task A_malformed_line_does_not_stop_the_run()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.MalformedLine);

        // Assert
        Done(events).Outcome.Should().Be(AgentOutcome.Succeeded);
    }

    [Fact]
    public async Task A_crash_without_result_fails_with_the_exit_code()
    {
        // Arrange
        var exitCode = Transcript(AgentContractScenario.CrashWithoutResult).ExitCode;

        // Act
        var (events, _) = await RunAsync(AgentContractScenario.CrashWithoutResult);

        // Assert
        Done(events).Should().Match<AgentDone>(d => d.Outcome == AgentOutcome.Failed && d.ExitCode == exitCode);
    }

    [Fact]
    public async Task A_crash_without_result_explains_itself_with_the_end_of_standard_error()
    {
        // Arrange
        var lastError = Transcript(AgentContractScenario.CrashWithoutResult).Lines.Last(l => l.Stream == ProcessStream.StandardError).Text;

        // Act
        var (events, _) = await RunAsync(AgentContractScenario.CrashWithoutResult);

        // Assert
        Done(events).Message.Should().Contain(lastError);
    }

    [Fact]
    public async Task Cancelling_kills_the_agent_process()
    {
        // Act
        var (_, runner) = await RunAsync(AgentContractScenario.Hang);

        // Assert
        runner.Process!.Killed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_ends_the_run_as_cancelled()
    {
        // Act
        var (events, _) = await RunAsync(AgentContractScenario.Hang);

        // Assert
        Done(events).Outcome.Should().Be(AgentOutcome.Cancelled);
    }

    [Fact]
    public async Task Stopping_the_enumeration_early_kills_the_agent_process()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(Transcript(AgentContractScenario.Hang));

        // Act
        await foreach (var _ in CreateAdapter(runner).RunAsync(Installation, Request, TestContext.Current.CancellationToken))
        {
            break;
        }

        // Assert
        runner.Process!.Killed.Should().BeTrue();
    }

    [Fact]
    public async Task An_agent_that_cannot_start_ends_the_run_as_failed()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of()) { StartError = new ProcessStartException("agent: not found") };
        var events = new List<AgentEvent>();

        // Act
        await foreach (var e in CreateAdapter(runner).RunAsync(Installation, Request, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        // Assert
        events.Should().Equal(new AgentError(AgentErrorKind.StartFailed, "agent: not found"), new AgentDone(AgentOutcome.Failed, Message: "agent: not found"));
    }
}
