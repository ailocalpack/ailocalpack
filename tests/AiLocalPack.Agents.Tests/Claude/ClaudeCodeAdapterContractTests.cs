using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary>Claude Code keeps the adapter contract (task 41) on its own recorded transcripts.</summary>
public sealed class ClaudeCodeAdapterContractTests : AgentAdapterContractTests
{
    protected override IAgentAdapter CreateAdapter(IProcessRunner runner) =>
        new ClaudeCodeAdapter(runner, new OsEnvironment(OsKind.Linux, "/home/player", _ => null), systemFolders: []);

    protected override ScriptedOutput Transcript(AgentContractScenario scenario) => scenario switch
    {
        AgentContractScenario.Success => ClaudeTranscripts.Output("success"),
        AgentContractScenario.ToolCall => ClaudeTranscripts.Output("mcp-tool-call"),
        AgentContractScenario.LimitReached => ClaudeTranscripts.Output("limit-reached", exitCode: 1),
        AgentContractScenario.SignedOut => ClaudeTranscripts.Output("signed-out", exitCode: 1),
        AgentContractScenario.MalformedLine => ScriptedOutput.Of(
            [.. ClaudeTranscripts.Lines("success").Take(2), "Unexpected token < in JSON", .. ClaudeTranscripts.Lines("success").Skip(2)]),
        AgentContractScenario.CrashWithoutResult => new ScriptedOutput(
            [
                new ProcessLine(ProcessStream.StandardOutput, ClaudeTranscripts.Lines("success")[0]),
                new ProcessLine(ProcessStream.StandardError, "error: unknown option '--restricted'"),
                new ProcessLine(ProcessStream.StandardError, "(Did you mean --resume?)"),
            ],
            ExitCode: 1),
        AgentContractScenario.Hang => ScriptedOutput.Of(ClaudeTranscripts.Lines("success")[0]) with { Hangs = true },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };
}
