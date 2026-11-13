using AiLocalPack.Core.Generation;

namespace AiLocalPack.Agents;

/// <summary>The model and reasoning effort of a run.</summary>
/// <param name="Model">The model (Claude Code's alias for the latest of a family); <c>null</c> = the agent's default.</param>
/// <param name="Effort">The reasoning effort; <c>null</c> = the agent's default for the model.</param>
public sealed record AgentModelChoice(string? Model, AgentEffort? Effort);

/// <summary>
/// Which model runs the stages after Extract for each <see cref="GenerationQuality"/> (decided 2026-11-13).
/// <list type="bullet">
/// <item><b>Claude Code</b>: its aliases for the latest model of a family, so nothing goes stale: <c>haiku</c> (Quick),
/// <c>sonnet</c> (Balanced), <c>opus</c> (Thorough); the agent's default effort.</item>
/// <item><b>Codex</b>: Codex names no stable aliases and its models change often, so the model stays Codex's default
/// and only the reasoning effort differs: low (Quick), medium (Balanced), high (Thorough).</item>
/// <item>Anything else (the fake agent): the agent's defaults.</item>
/// </list>
/// The agent's own extractor (unknown engines) runs with the Balanced choice whatever the quality.
/// </summary>
public static class AgentModels
{
    public static AgentModelChoice For(string agentId, GenerationQuality quality) => agentId switch
    {
        "claude" => new AgentModelChoice(
            quality switch
            {
                GenerationQuality.Quick => "haiku",
                GenerationQuality.Thorough => "opus",
                _ => "sonnet",
            },
            null),
        "codex" => new AgentModelChoice(
            null,
            quality switch
            {
                GenerationQuality.Quick => AgentEffort.Low,
                GenerationQuality.Thorough => AgentEffort.High,
                _ => AgentEffort.Medium,
            }),
        _ => new AgentModelChoice(null, null),
    };

    /// <summary>The model of the agent's own extractor and rebuild scripts (unknown engines): the Balanced one.</summary>
    public static AgentModelChoice ForExtraction(string agentId) => For(agentId, GenerationQuality.Balanced);
}
