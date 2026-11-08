using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Generation;
using static AiLocalPack.Agents.Tests.FakeAgentProgram;

namespace AiLocalPack.Agents.Tests;

/// <summary>An agent run's end as a generation step's result (task 47).</summary>
public sealed class AgentStepRunTests : IDisposable
{
    private static readonly DateTimeOffset Reset = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<AgentStepOutcome> RunAsync(params string[] lines)
    {
        var recording = Write(_dir, "r.jsonl", lines);
        var adapter = new FakeAgentAdapter(new ProcessRunner(), Replayer, recording);
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);
        return await AgentStepRun.RunAsync(adapter, installation!, new AgentRunRequest("Go.", _dir.Path), null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_successful_run_lets_the_stage_decide()
    {
        // Arrange
        string[] lines = [Event(new AgentUsage(10, 5)), Done("ok")];

        // Act
        var outcome = await RunAsync(lines);

        // Assert
        new { outcome.Succeeded, outcome.Usage }.Should().Be(new { Succeeded = true, Usage = (AgentUsage?)new AgentUsage(10, 5) });
    }

    [Fact]
    public async Task A_limit_reports_its_reset_time()
    {
        // Arrange
        string[] lines = [Event(new AgentLimitReached(Reset, "You hit your limit")), Event(new AgentDone(AgentOutcome.LimitReached, Message: "limit"))];

        // Act
        var outcome = await RunAsync(lines);

        // Assert
        outcome.Stop.Should().Be(new GenerationStepResult.LimitReached(Reset, "You hit your limit"));
    }

    [Fact]
    public void Signed_out_fails_the_stage_as_signed_out()
    {
        // Arrange
        var done = new AgentDone(AgentOutcome.SignedOut, Message: "Not logged in");

        // Act
        var result = AgentStepRun.ToResult(done, null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.SignedOut, "Not logged in"));
    }

    [Fact]
    public void A_failed_run_fails_the_stage_with_its_message()
    {
        // Arrange
        var done = new AgentDone(AgentOutcome.Failed, Message: "exited with code 3", ExitCode: 3);

        // Act
        var result = AgentStepRun.ToResult(done, null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.Agent, "exited with code 3"));
    }

    [Fact]
    public void A_program_that_cant_start_is_reported_as_missing()
    {
        // Arrange
        var done = new AgentDone(AgentOutcome.Failed, Message: "No such file");

        // Act
        var result = AgentStepRun.ToResult(done, null, startFailed: true, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new GenerationStepResult.Failed(GenerationFailureKind.AgentMissing, "No such file"));
    }

    [Fact]
    public void A_cancelled_run_throws_so_the_runner_pauses_or_cancels()
    {
        // Arrange
        var done = new AgentDone(AgentOutcome.Cancelled);

        // Act
        var act = () => AgentStepRun.ToResult(done, null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }
}
