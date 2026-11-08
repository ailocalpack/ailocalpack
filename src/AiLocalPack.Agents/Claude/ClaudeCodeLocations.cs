using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Claude;

/// <summary>
/// Where Claude Code may be installed (task 41), in the order detection tries them (<see cref="AgentPrograms.Candidates"/>):
/// <c>PATH</c>, the native installer's <c>~/.local/bin</c>, on Unix the old local install <c>~/.claude/local</c>, Homebrew
/// and npm's global folder, on Windows npm's <c>%APPDATA%\npm</c>; last the app-managed folder (task 43).
/// </summary>
public static class ClaudeCodeLocations
{
    public static AgentProgramLayout Layout { get; } = new("claude", [".local/bin"], [".claude/local"]);

    /// <inheritdoc cref="AgentPrograms.Candidates"/>
    public static IReadOnlyList<(string Path, AgentSource Source)> Candidates(
        OsEnvironment environment,
        string? managedDirectory,
        IReadOnlyList<string>? systemFolders = null) =>
        AgentPrograms.Candidates(Layout, environment, managedDirectory, systemFolders);
}
