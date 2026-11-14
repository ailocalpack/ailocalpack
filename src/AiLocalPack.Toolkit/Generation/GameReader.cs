using AiLocalPack.Core.Generation;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Generation;

/// <summary>
/// What reads a game's strings for a generation: the toolkit's recipe tool, or the agent's own code. The player picks it
/// on the start form (<see cref="GenerationOptions.Reader"/>: a tool's ID or <see cref="GenerationOptions.AgentReader"/>);
/// without a choice it is the tool of the detected engine, else the agent. The estimate, the Extract stage and the
/// toolkit MCP server all resolve it here, so they read the game the same way.
/// </summary>
/// <param name="Kind">The engine the game is read as: the detected one, or the chosen tool's engine when it differs.</param>
/// <param name="Tool">The recipe tool, when <paramref name="Source"/> is <see cref="TextSizeSource.Toolkit"/>.</param>
/// <param name="Source">Why there's no tool, or <see cref="TextSizeSource.Toolkit"/>.</param>
public sealed record GameReader(EngineKind Kind, IRecipeTool? Tool, TextSizeSource Source)
{
    /// <param name="detected">The engine detected in the game folder (or stored in the catalog).</param>
    /// <param name="reader">The player's choice; <c>null</c> or a tool this app doesn't have = the detected engine's tool.</param>
    public static GameReader Resolve(RecipeTools tools, EngineKind detected, string? reader)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(detected);
        if (reader == GenerationOptions.AgentReader)
        {
            return new(detected, null, TextSizeSource.AgentChosen);
        }

        var kind = reader is not null && tools.Find(reader) is { } chosen && chosen.Engine != detected.Engine
            ? new EngineKind(chosen.Engine)
            : detected;
        return kind.Engine == GameEngine.Unknown ? new(kind, null, TextSizeSource.UnknownEngine)
            : tools.IsDisabled(kind) ? new(kind, null, TextSizeSource.EngineDisabled)
            : tools.For(kind) is { } tool ? new(kind, tool, TextSizeSource.Toolkit)
            : new(kind, null, TextSizeSource.NoTool);
    }

    /// <summary>Why the toolkit doesn't read the game, in plain English (logs, the agent's prompt), or <c>null</c>.</summary>
    public string? Problem => Source switch
    {
        TextSizeSource.Toolkit => null,
        TextSizeSource.AgentChosen => "The player chose to have the game read with your own code instead of the toolkit.",
        TextSizeSource.UnknownEngine => "The game's engine wasn't recognized, so the toolkit can't read its strings.",
        TextSizeSource.EngineDisabled => $"The toolkit doesn't handle {Kind.DisplayName} games on this system for now.",
        _ => $"The toolkit can't read {Kind.DisplayName} games yet.",
    };
}
