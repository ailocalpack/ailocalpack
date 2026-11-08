using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;
using static AiLocalPack.Agents.Tests.FakeAgentProgram;

namespace AiLocalPack.Agents.Tests;

/// <summary>The fake agent adapter end to end, with the real fake agent program (task 40).</summary>
public sealed class FakeAgentAdapterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<List<AgentEvent>> RunAsync(string recording, AgentRunRequest request, CancellationToken cancellationToken)
    {
        var adapter = new FakeAgentAdapter(new ProcessRunner(), Replayer, recording);
        var installation = await adapter.DetectAsync(cancellationToken);
        var events = new List<AgentEvent>();
        await foreach (var e in adapter.RunAsync(installation!, request, cancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task A_recorded_run_streams_its_events()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Event(new AgentSessionStarted("s1", "fake-1")), Event(new AgentUsage(10, 5)), Done("ok"));

        // Act
        var events = await RunAsync(recording, new AgentRunRequest("Go.", _dir.Path), TestContext.Current.CancellationToken);

        // Assert
        events.Should().Equal(new AgentSessionStarted("s1", "fake-1"), new AgentUsage(10, 5), new AgentDone(AgentOutcome.Succeeded, "ok", null, 0));
    }

    [Fact]
    public async Task The_prompt_reaches_the_agent()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("prompt"), Done());

        // Act
        var events = await RunAsync(recording, new AgentRunRequest("Переведи строки.", _dir.Path), TestContext.Current.CancellationToken);

        // Assert
        events[0].Should().Be(new AgentMessage("Переведи строки."));
    }

    [Fact]
    public async Task The_agent_runs_in_the_requests_working_directory()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Control("cwd"), Done());
        var work = _dir.Dir("work").Combine("work");

        // Act
        var events = await RunAsync(recording, new AgentRunRequest("Go.", work), TestContext.Current.CancellationToken);

        // Assert
        events[0].Should().Be(new AgentMessage(PathNames.RealPath(work)));
    }

    [Fact]
    public async Task Cancelling_a_real_run_ends_it_as_cancelled()
    {
        // Arrange
        var recording = Write(_dir, "r.jsonl", Event(new AgentMessage("Working.")), Control("hang"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var adapter = new FakeAgentAdapter(new ProcessRunner(), Replayer, recording);
        var installation = await adapter.DetectAsync(cancel.Token);
        AgentEvent? last = null;

        // Act
        await foreach (var e in adapter.RunAsync(installation!, new AgentRunRequest("Go.", _dir.Path), cancel.Token))
        {
            last = e;
            await cancel.CancelAsync();
        }

        // Assert
        last.Should().BeOfType<AgentDone>().Which.Outcome.Should().Be(AgentOutcome.Cancelled);
    }

    [Fact]
    public async Task The_fake_agent_is_found_as_configured()
    {
        // Arrange
        var adapter = new FakeAgentAdapter(new ProcessRunner(), new FakeAgentReplayer("/bin/fake", ["a.dll"]), "r.jsonl");

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation.Should().BeEquivalentTo(new AgentInstallation("fake", "/bin/fake", "replay", AgentSource.Configured) { Arguments = ["a.dll"] });
    }

    [Fact]
    public async Task The_sign_in_probe_answers_as_configured()
    {
        // Arrange
        var adapter = new FakeAgentAdapter(new ProcessRunner(), Replayer, "r.jsonl", AgentAuthState.SignedOut);
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Act
        var status = await adapter.GetAuthStatusAsync(installation!, TestContext.Current.CancellationToken);

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.SignedOut));
    }
}
