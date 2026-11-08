using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Codex;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents;

/// <summary>The real agents the app drives, by <see cref="IAgentAdapter.Id"/> (Claude Code, Codex).</summary>
public static class AgentAdapters
{
    /// <summary>Every real agent's ID, in the order the app offers them.</summary>
    public static IReadOnlyList<string> Ids { get; } = [ClaudeCodeAdapter.AgentId, CodexAdapter.AgentId];

    /// <summary>The adapter of <paramref name="id"/> (case-insensitive), or <c>null</c> for an unknown agent.</summary>
    /// <param name="id">The agent's ID.</param>
    /// <param name="runner">Starts its processes.</param>
    /// <param name="environment">The OS, home and <c>PATH</c> it is looked for in.</param>
    /// <param name="managedDirectory">The folder the app installs this agent into (task 43), when known.</param>
    public static IAgentAdapter? Create(string id, IProcessRunner runner, OsEnvironment environment, string? managedDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.ToLowerInvariant() switch
        {
            ClaudeCodeAdapter.AgentId => new ClaudeCodeAdapter(runner, environment, managedDirectory),
            CodexAdapter.AgentId => new CodexAdapter(runner, environment, managedDirectory),
            _ => null,
        };
    }
}
