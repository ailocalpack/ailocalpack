using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Codex;
using AiLocalPack.Desktop.Resources;
using Avalonia.Media;

namespace AiLocalPack.Desktop.Agents;

/// <summary>How the app shows an agent: product and vendor names, the two-letter mark on its logo tile and its colours.</summary>
/// <param name="Id">The adapter's ID.</param>
/// <param name="Name">The product name (never translated).</param>
/// <param name="Vendor">Who publishes its official builds.</param>
/// <param name="Mark">Two letters on the logo tile.</param>
/// <param name="ColorFrom">Logo gradient start./param>
/// <param name="ColorTo">Logo gradient end./param>
/// <param name="InstallHelpKey">What <c>Install</c> does, with the download size as <c>{0}</c>.</param>
/// <param name="SignInHelpKey">What <c>Sign in</c> does.</param>
public sealed record AgentInfo(
    string Id,
    string Name,
    string Vendor,
    string Mark,
    Color ColorFrom,
    Color ColorTo,
    string InstallHelpKey,
    string SignInHelpKey)
{
    public static AgentInfo Claude { get; } = new(
        ClaudeCodeAdapter.AgentId,
        "Claude Code",
        "Anthropic",
        "Cc",
        Color.Parse("#D97757"),
        Color.Parse("#8A3B1F"),
        nameof(Strings.Settings_Agent_InstallHelp_Claude),
        nameof(Strings.Settings_Agent_SignInHelp_Claude));

    public static AgentInfo Codex { get; } = new(
        CodexAdapter.AgentId,
        "Codex",
        "OpenAI",
        "Cx",
        Color.Parse("#3B3F4A"),
        Color.Parse("#10A37F"),
        nameof(Strings.Settings_Agent_InstallHelp_Codex),
        nameof(Strings.Settings_Agent_SignInHelp_Codex));

    /// <summary>The agent's info; an agent the app doesn't describe gets its ID as its name.</summary>
    public static AgentInfo Of(string id) => id switch
    {
        ClaudeCodeAdapter.AgentId => Claude,
        CodexAdapter.AgentId => Codex,
        _ => new AgentInfo(id, id, id, id.Length >= 2 ? id[..2] : id, Color.Parse("#3B3F4A"), Color.Parse("#6B7280"), nameof(Strings.Settings_Agent_InstallHelp_Codex), nameof(Strings.Settings_Agent_SignInHelp_Codex)),
    };
}
