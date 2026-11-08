using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using static AiLocalPack.Agents.Tests.FakeAgentProgram;

namespace AiLocalPack.Agents.Tests;

/// <summary>How <see cref="ProcessAgentAdapter"/> turns a process's output and exit into the final event (task 40).</summary>
public sealed class ProcessAgentAdapterTests
{
    private static readonly AgentInstallation Installation = new("fake", "fake-agent", "replay", AgentSource.Configured);

    private static async Task<List<AgentEvent>> RunAsync(ScriptedOutput script, CancellationToken? cancellationToken = null)
    {
        var adapter = new FakeAgentAdapter(new ScriptedProcessRunner(script), new FakeAgentReplayer("fake-agent", []), "r.jsonl");
        var events = new List<AgentEvent>();
        var request = new AgentRunRequest("Go.", Path.GetTempPath());
        await foreach (var e in adapter.RunAsync(Installation, request, cancellationToken ?? TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task A_success_verdict_with_a_non_zero_exit_code_fails()
    {
        // Arrange
        var script = ScriptedOutput.Of(Done("ok")) with { ExitCode = 2 };

        // Act
        var events = await RunAsync(script);

        // Assert
        events[^1].Should().Be(new AgentDone(AgentOutcome.Failed, "ok", "The agent exited with code 2.", 2));
    }

    [Fact]
    public async Task The_agents_own_failure_keeps_its_message()
    {
        // Arrange
        var script = ScriptedOutput.Of(Event(new AgentDone(AgentOutcome.Failed, Message: "API error 500"))) with { ExitCode = 1 };

        // Act
        var events = await RunAsync(script);

        // Assert
        events[^1].Should().Be(new AgentDone(AgentOutcome.Failed, null, "API error 500", 1));
    }

    [Fact]
    public async Task A_run_without_a_result_fails_even_with_exit_code_zero()
    {
        // Arrange
        var script = ScriptedOutput.Of(Event(new AgentMessage("Hi.")));

        // Act
        var events = await RunAsync(script);

        // Assert
        events[^1].Should().Be(new AgentDone(AgentOutcome.Failed, null, "The agent ended without a result.", 0));
    }

    [Fact]
    public async Task Only_the_first_verdict_counts()
    {
        // Arrange
        var script = ScriptedOutput.Of(Done("first"), Done("second"));

        // Act
        var events = await RunAsync(script);

        // Assert
        events.Should().Equal(new AgentDone(AgentOutcome.Succeeded, "first", null, 0));
    }

    [Fact]
    public async Task Standard_error_lines_become_diagnostics()
    {
        // Arrange
        var script = new ScriptedOutput([new ProcessLine(ProcessStream.StandardError, "warning: slow"), new ProcessLine(ProcessStream.StandardOutput, Done())]);

        // Act
        var events = await RunAsync(script);

        // Assert
        events[0].Should().Be(new AgentDiagnostic("warning: slow"));
    }

    [Fact]
    public async Task A_cut_line_is_malformed_and_not_parsed()
    {
        // Arrange
        var script = new ScriptedOutput([new ProcessLine(ProcessStream.StandardOutput, "{\"type\":\"mess", Truncated: true), new ProcessLine(ProcessStream.StandardOutput, Done())]);

        // Act
        var events = await RunAsync(script);

        // Assert
        events[0].Should().BeOfType<AgentError>().Which.Kind.Should().Be(AgentErrorKind.Malformed);
    }

    [Fact]
    public async Task A_run_cancelled_before_it_starts_starts_nothing()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of(Done()));
        var adapter = new FakeAgentAdapter(runner, new FakeAgentReplayer("fake-agent", []), "r.jsonl");
        var events = new List<AgentEvent>();

        // Act
        await foreach (var e in adapter.RunAsync(Installation, new AgentRunRequest("Go.", "."), new CancellationToken(canceled: true)))
        {
            events.Add(e);
        }

        // Assert
        (runner.Started, events.Single()).Should().Be((null, new AgentDone(AgentOutcome.Cancelled, Message: "Cancelled before the agent started.")));
    }

    [Fact]
    public async Task A_cancelled_run_wins_over_a_reached_limit()
    {
        // Arrange
        var limit = Event(new AgentLimitReached(null, "limit"));
        using var cancel = new CancellationTokenSource();
        var adapter = new FakeAgentAdapter(new ScriptedProcessRunner(ScriptedOutput.Of(limit) with { Hangs = true }), new FakeAgentReplayer("f", []), "r");
        AgentEvent? last = null;

        // Act
        await foreach (var e in adapter.RunAsync(Installation, new AgentRunRequest("Go.", "."), cancel.Token))
        {
            last = e;
            await cancel.CancelAsync();
        }

        // Assert
        last.Should().BeOfType<AgentDone>().Which.Outcome.Should().Be(AgentOutcome.Cancelled);
    }

    [Fact]
    public async Task The_end_of_a_long_standard_error_is_kept()
    {
        // Arrange
        var lines = Enumerable.Range(1, 30).Select(i => new ProcessLine(ProcessStream.StandardError, $"line {i}")).ToList();

        // Act
        var events = await RunAsync(new ScriptedOutput(lines, ExitCode: 1));

        // Assert
        events[^1].Should().BeOfType<AgentDone>().Which.Message.Should().Be(
            "The agent exited with code 1 without a result.\n" + string.Join('\n', Enumerable.Range(11, 20).Select(i => $"line {i}")));
    }
}
