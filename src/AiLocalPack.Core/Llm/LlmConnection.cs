using System.Security.Cryptography;

namespace AiLocalPack.Core.Llm;

/// <summary>
/// One of the player's own models (Settings → AI agents → <em>Your models</em>): a server that speaks the OpenAI Chat
/// Completions API with tool calling (Ollama, LM Studio, OpenRouter, OpenAI, any other), used in place of an agent CLI.
/// Kept in <see cref="Settings.AppSettings.LlmConnections"/>; its API key, when it has one, only in the OS keychain
/// under <see cref="LlmConnections.SecretName"/>.
/// </summary>
/// <param name="Id">Stable ID, <c>llm-</c> + 8 lowercase hex (<see cref="LlmConnections.NewId"/>); the job's agent ID.</param>
/// <param name="Name">What the player calls it (shown on cards and in the default agent choice).</param>
/// <param name="Provider">The preset it was made from (<see cref="LlmProviders"/> ID: <c>ollama</c>, <c>lmstudio</c>, <c>openrouter</c>, <c>openai</c>, <c>custom</c>).</param>
/// <param name="BaseUrl">The API's base URL, up to and including <c>/v1</c> (<c>http://localhost:11434/v1</c>).</param>
/// <param name="Model">The model ID the server knows (<c>qwen3:30b</c>, <c>openai/gpt-5-mini</c>).</param>
/// <param name="HasKey">An API key is kept in the keychain for it.</param>
public sealed record LlmConnection(string Id, string Name, string Provider, string BaseUrl, string Model, bool HasKey = false)
{
    /// <summary>The server's host and port, for one-line summaries (<c>localhost:11434</c>, <c>openrouter.ai</c>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Host => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}" : BaseUrl;

    /// <summary>The server runs on this computer (no data leaves it).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLocal => LlmConnections.IsLocalUrl(BaseUrl);
}

/// <summary>A server preset of the <em>Add model</em> dialog.</summary>
/// <param name="Id">Stored in <see cref="LlmConnection.Provider"/>.</param>
/// <param name="Name">Product name (never translated; empty for <c>custom</c>, which the UI names).</param>
/// <param name="BaseUrl">The address it listens on by default (empty for <c>custom</c>).</param>
/// <param name="NeedsKey">It refuses requests without an API key.</param>
/// <param name="IsLocal">It runs on the player's computer.</param>
/// <param name="KeysUrl">Where the player gets a key, for the dialog's link.</param>
public sealed record LlmProvider(string Id, string Name, string BaseUrl, bool NeedsKey, bool IsLocal, string? KeysUrl = null);

/// <summary>The presets, in the order the dialog offers them.</summary>
public static class LlmProviders
{
    public const string Custom = "custom";

    public static LlmProvider Ollama { get; } = new("ollama", "Ollama", "http://localhost:11434/v1", NeedsKey: false, IsLocal: true);

    public static LlmProvider LmStudio { get; } = new("lmstudio", "LM Studio", "http://localhost:1234/v1", NeedsKey: false, IsLocal: true);

    public static LlmProvider OpenRouter { get; } = new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", NeedsKey: true, IsLocal: false, "https://openrouter.ai/settings/keys");

    public static LlmProvider OpenAi { get; } = new("openai", "OpenAI", "https://api.openai.com/v1", NeedsKey: true, IsLocal: false, "https://platform.openai.com/api-keys");

    public static LlmProvider Other { get; } = new(Custom, string.Empty, string.Empty, NeedsKey: false, IsLocal: false);

    public static IReadOnlyList<LlmProvider> All { get; } = [Ollama, LmStudio, OpenRouter, OpenAi, Other];

    /// <summary>The preset of <paramref name="id"/>; an unknown one (a newer app's) is <see cref="Other"/>.</summary>
    public static LlmProvider Of(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Other;
}

/// <summary>Rules shared by the app, the CLI and the adapter.</summary>
public static class LlmConnections
{
    public const string IdPrefix = "llm-";

    /// <summary>The manifest's <c>agent.id</c> of a pack made by one of the player's models (the connection's own ID is local).</summary>
    public const string ManifestAgentId = "openai-compatible";

    public const int MaxNameLength = 60;

    public const int MaxModelLength = 128;

    /// <summary>A new connection ID.</summary>
    public static string NewId() => IdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>Whether a job's agent ID names one of the player's models.</summary>
    public static bool IsLlmId(string? agentId) => agentId?.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The keychain entry holding the connection's API key.</summary>
    public static string SecretName(string id) => $"llm-key-{id}";

    /// <summary>The connection of <paramref name="id"/> in <paramref name="connections"/>, or <c>null</c>.</summary>
    public static LlmConnection? Find(IEnumerable<LlmConnection>? connections, string? id) =>
        connections?.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The base URL as stored: trimmed, no trailing <c>/</c>, an absolute <c>http</c> / <c>https</c> URL without user info,
    /// query or fragment; <c>null</c> when it isn't one. A pasted <c>…/chat/completions</c> is cut back to its base.
    /// </summary>
    public static string? NormalizeUrl(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        foreach (var tail in (string[])["/chat/completions", "/models"])
        {
            if (url.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^tail.Length];
            }
        }

        return url;
    }

    /// <summary>The URL's host is this computer (<c>localhost</c>, a loopback address).</summary>
    public static bool IsLocalUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}
