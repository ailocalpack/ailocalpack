using AiLocalPack.Agents.Codex;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Codex;

/// <summary>Codex keeps the adapter contract (tasks 40, 42) on its own recorded transcripts.</summary>
public sealed class CodexAdapterContractTests : AgentAdapterContractTests
{
    protected override IAgentAdapter CreateAdapter(IProcessRunner runner) =>
        new CodexAdapter(runner, new OsEnvironment(OsKind.Linux, "/home/player", _ => null), systemFolders: [], clock: new FakeClock());

    protected override ScriptedOutput Transcript(AgentContractScenario scenario) => scenario switch
    {
        AgentContractScenario.Success => CodexTranscripts.Output("success"),
        AgentContractScenario.ToolCall => CodexTranscripts.Output("mcp-tool-call"),
        AgentContractScenario.LimitReached => CodexTranscripts.Output("limit-reached", exitCode: 1),
        AgentContractScenario.SignedOut => CodexTranscripts.Output("signed-out", exitCode: 1),
        AgentContractScenario.MalformedLine => ScriptedOutput.Of(
            [.. CodexTranscripts.Lines("success").Take(2), "thread 'main' panicked", .. CodexTranscripts.Lines("success").Skip(2)]),
        AgentContractScenario.CrashWithoutResult => new ScriptedOutput(
            [
                new ProcessLine(ProcessStream.StandardOutput, CodexTranscripts.Lines("success")[0]),
                new ProcessLine(ProcessStream.StandardError, "Error: unexpected argument '--ignore-rules' found"),
                new ProcessLine(ProcessStream.StandardError, "For more information, try '--help'."),
            ],
            ExitCode: 2),
        AgentContractScenario.Hang => ScriptedOutput.Of(CodexTranscripts.Lines("success")[0]) with { Hangs = true },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
