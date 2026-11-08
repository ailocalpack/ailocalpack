using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using static AiLocalPack.Agents.Tests.FakeAgentProgram;

namespace AiLocalPack.Agents.Tests;

/// <summary>The fake agent keeps the adapter contract (task 40), its transcripts in <see cref="AgentEventJson"/> lines.</summary>
public sealed class FakeAgentAdapterContractTests : AgentAdapterContractTests
{
    protected override IAgentAdapter CreateAdapter(IProcessRunner runner) =>
        new FakeAgentAdapter(runner, new FakeAgentReplayer("fake-agent", []), "recording.jsonl");

    protected override ScriptedOutput Transcript(AgentContractScenario scenario) => scenario switch
    {
        AgentContractScenario.Success => ScriptedOutput.Of(
            Event(new AgentSessionStarted("s1", "fake-1")),
            Event(new AgentMessage("Extracting the strings.")),
            Event(new AgentUsage(120, 40)),
            Done("28 strings translated")),
        AgentContractScenario.ToolCall => ScriptedOutput.Of(
            Event(new AgentToolCall("t1", "mcp__toolkit__extract", """{"path":"."}""")),
            Event(new AgentToolResult("t1", "28 strings", false)),
            Done()),
        AgentContractScenario.LimitReached => ScriptedOutput.Of(
            Event(new AgentMessage("Working.")),
            Event(new AgentLimitReached(new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero), "5-hour limit reached")),
            Event(new AgentDone(AgentOutcome.Failed, Message: "limit"))) with { ExitCode = 1 },
        AgentContractScenario.SignedOut => ScriptedOutput.Of(
            Event(new AgentError(AgentErrorKind.SignedOut, "Invalid API key · Please run /login"))) with { ExitCode = 1 },
        AgentContractScenario.MalformedLine => ScriptedOutput.Of(
            Event(new AgentMessage("Working.")),
            "{\"type\":",
            Done()),
        AgentContractScenario.CrashWithoutResult => new ScriptedOutput(
            [
                new ProcessLine(ProcessStream.StandardOutput, Event(new AgentMessage("Working."))),
                new ProcessLine(ProcessStream.StandardError, "Error: something broke"),
                new ProcessLine(ProcessStream.StandardError, "    at main (agent.js:1:1)"),
            ],
            ExitCode: 3),
        AgentContractScenario.Hang => ScriptedOutput.Of(Event(new AgentMessage("Working."))) with { Hangs = true },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
