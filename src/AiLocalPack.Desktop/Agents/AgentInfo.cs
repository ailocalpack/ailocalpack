using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Codex;
using AiLocalPack.Core.Llm;
using AiLocalPack.Desktop.Resources;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Agents;

/// <summary>How the app shows an agent: product and vendor names, its logo tile (brand mark, else a neutral icon, else two letters) and its colours.</summary>
/// <param name="Id">The adapter's ID.</param>
/// <param name="Name">The product name (never translated).</param>
/// <param name="Vendor">Who publishes its official builds.</param>
/// <param name="Mark">Two letters on the logo tile when it has no <paramref name="LogoKey"/>.</param>
/// <param name="ColorFrom">Logo tile gradient start.</param>
/// <param name="ColorTo">Logo tile gradient end.</param>
/// <param name="InstallHelpKey">What <c>Install</c> does, with the download size as <c>{0}</c>.</param>
/// <param name="SignInHelpKey">What <c>Sign in</c> does.</param>
/// <param name="LogoKey">Geometry drawn white on the tile: a filled brand mark (<c>Brand…</c> in <c>Styles/BrandIcons.axaml</c>) or a stroked
/// Lucide icon (<c>Icon…</c>); <see langword="null"/> shows <paramref name="Mark"/>.</param>
public sealed record AgentInfo(
    string Id,
    string Name,
    string Vendor,
    string Mark,
    Color ColorFrom,
    Color ColorTo,
    string InstallHelpKey,
    string SignInHelpKey,
    string? LogoKey = null)
{
    /// <summary>The tile shows a filled brand mark.</summary>
    public bool HasBrandLogo => LogoKey?.StartsWith("Brand", StringComparison.Ordinal) == true;

    /// <summary>The tile shows a stroked neutral icon (a brand whose guidelines don't allow its mark here).</summary>
    public bool HasIconLogo => LogoKey is not null && !HasBrandLogo;

    /// <summary>The tile shows <see cref="Mark"/>.</summary>
    public bool HasMark => LogoKey is null;

    public static AgentInfo Claude { get; } = new(
        ClaudeCodeAdapter.AgentId,
        "Claude Code",
        "Anthropic",
        "Cc",
        Color.Parse("#D97757"),
        Color.Parse("#C15F3C"),
        nameof(Strings.Settings_Agent_InstallHelp_Claude),
        nameof(Strings.Settings_Agent_SignInHelp_Claude),
        "BrandClaude");

    public static AgentInfo Codex { get; } = new(
        CodexAdapter.AgentId,
        "Codex",
        "OpenAI",
        "Cx",
        Color.Parse("#3B3F4A"),
        Color.Parse("#16181D"),
        nameof(Strings.Settings_Agent_InstallHelp_Codex),
        nameof(Strings.Settings_Agent_SignInHelp_Codex),
        "IconTerminal"); // OpenAI's guidelines don't allow its mark here (removed from Simple Icons in 16.0.0)

    /// <summary>
    /// One of the player's own models: its name, the server preset as vendor, the preset's mark (Ollama, LM Studio,
    /// OpenRouter from Simple Icons; OpenAI's guidelines don't allow its mark → a neutral sparkles icon; any other server
    /// a server icon).
    /// </summary>
    public static AgentInfo ForLlm(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var provider = LlmProviders.Of(connection.Provider);
        var (from, to, logo) = provider.Id switch
        {
            "ollama" => ("#2B2D31", "#0B0C0E", "BrandOllama"),
            "lmstudio" => ("#5B5BD6", "#3E3EB0", "BrandLmStudio"),
            "openrouter" => ("#6366F1", "#4338CA", "BrandOpenRouter"),
            "openai" => ("#3B3F4A", "#16181D", "IconSparkles"),
            _ => ("#0F766E", "#115E59", "IconServer"),
        };
        return new AgentInfo(
            connection.Id,
            connection.Name,
            provider.Name.Length > 0 ? provider.Name : connection.Host,
            connection.Name.Length >= 2 ? connection.Name[..2] : "AI",
            Color.Parse(from),
            Color.Parse(to),
            nameof(Strings.Settings_Agent_InstallHelp_Codex),
            nameof(Strings.Settings_Agent_SignInHelp_Codex),
            logo);
    }

    // The player's models by ID, as the settings named them (IDs are random, so graphs in tests never clash; a removed
    // model stays known, so its jobs keep their name).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LlmConnection> KnownLlms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes <see cref="Of"/> name the player's models (the app calls it with the settings at start and on every change).</summary>
    public static void Remember(IEnumerable<LlmConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        foreach (var connection in connections)
        {
            KnownLlms[connection.Id] = connection;
        }
    }

    /// <summary>The player's model of <paramref name="id"/> as last named by the settings, or <c>null</c>.</summary>
    public static LlmConnection? KnownLlm(string? id) => id is not null && KnownLlms.TryGetValue(id, out var llm) ? llm : null;

    /// <summary>The agent's info; an agent the app doesn't describe gets its ID as its name.</summary>
    public static AgentInfo Of(string id) => id switch
    {
        _ when KnownLlms.TryGetValue(id, out var llm) => ForLlm(llm),
        ClaudeCodeAdapter.AgentId => Claude,
        CodexAdapter.AgentId => Codex,
        _ => new AgentInfo(id, id, id, id.Length >= 2 ? id[..2] : id, Color.Parse("#3B3F4A"), Color.Parse("#6B7280"), nameof(Strings.Settings_Agent_InstallHelp_Codex), nameof(Strings.Settings_Agent_SignInHelp_Codex)),
    };
}
