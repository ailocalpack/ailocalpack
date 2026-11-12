using AiLocalPack.Agents;

namespace AiLocalPack.Cli.Tests;

/// <summary><c>agent run --fake</c> (task 40): the CLI replays a recording through itself and prints the normalized events.</summary>
public sealed class AgentRunTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Recording(params AgentEvent[] events)
    {
        _dir.File("r.jsonl", string.Join('\n', events.Select(AgentEventJson.Write)) + "\n");
        return _dir.Combine("r.jsonl");
    }

    [Fact]
    public async Task A_successful_run_prints_its_events_and_exits_zero()
    {
        // Arrange
        var recording = Recording(new AgentSessionStarted("s1", "fake-1"), new AgentUsage(120, 40), new AgentDone(AgentOutcome.Succeeded, "Готово"));

        // Act
        var result = await CliRunner.RunAsync("agent", "run", "--fake", recording, "list files");

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0,
            "session  s1 (model fake-1)\n"
            + "usage    in 120 · out 40 · cache read 0 · cache write 0\n"
            + "done     Succeeded (exit 0): Готово\n"));
    }

    [Fact]
    public async Task Json_output_reads_back_as_the_events()
    {
        // Arrange
        var recording = Recording(new AgentMessage("hi"), new AgentDone(AgentOutcome.Succeeded, "ok"));

        // Act
        var result = await CliRunner.RunAsync("agent", "run", "--fake", recording, "--json");

        // Assert
        result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => AgentEventJson.Read(line.TrimEnd('\r'), out _))
            .Should().Equal(new AgentMessage("hi"), new AgentDone(AgentOutcome.Succeeded, "ok", null, 0));
    }

    [Fact]
    public async Task A_run_that_reached_its_limit_exits_one()
    {
        // Arrange
        var recording = Recording(new AgentLimitReached(new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero), "5-hour limit reached"));

        // Act
        var result = await CliRunner.RunAsync("agent", "run", "--fake", recording);

        // Assert
        (result.ExitCode, result.StdOut.Contains("limit    resets 2026-11-08 18:00:00Z: 5-hour limit reached", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task A_missing_recording_exits_one()
    {
        // Act
        var result = await CliRunner.RunAsync("agent", "run", "--fake", _dir.Combine("missing.jsonl"));

        // Assert
        (result.ExitCode, result.StdErr.Contains("no such recording", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task An_unknown_agent_exits_one()
    {
        // Act
        var result = await CliRunner.RunAsync("agent", "run", "gpt", "hello");

        // Assert
        (result.ExitCode, result.StdErr.Contains("Unknown agent \"gpt\"", StringComparison.Ordinal)).Should().Be((1, true));
    }
}
