using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary>The recorded Claude Code stream-json transcripts in <c>tests/fixtures/agents/claude</c> (see its README).</summary>
internal static class ClaudeTranscripts
{
    public static IReadOnlyList<string> Lines(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "agents", "claude", name + ".jsonl"))
            .Where(l => l.Length > 0)
            .ToList();

    public static ScriptedOutput Output(string name, int exitCode = 0) => ScriptedOutput.Of([.. Lines(name)]) with { ExitCode = exitCode };

    /// <summary>A machine of the current OS with a temp home and the given <c>PATH</c>.</summary>
    public static OsEnvironment Environment(string home, string? path = null, IReadOnlyDictionary<string, string>? variables = null) =>
        new(
            OsEnvironment.Current.Os,
            home,
            name => name == "PATH" ? path : variables?.GetValueOrDefault(name));

    public static string ProgramName => OperatingSystem.IsWindows() ? "claude.exe" : "claude";
}

/// <summary>An <see cref="IProcessRunner"/> answering each start by its <see cref="ProcessSpec"/>, recording every start.</summary>
public sealed class RoutingProcessRunner(Func<ProcessSpec, ScriptedOutput> answer) : IProcessRunner
{
    public List<ProcessSpec> Started { get; } = [];

    public IRunningProcess Start(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        Started.Add(spec);
        return new ScriptedProcess(answer(spec), cancellationToken);
    }
}
