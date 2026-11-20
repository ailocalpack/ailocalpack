using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Settings;

namespace AiLocalPack.Agents.Llm;

/// <summary>The player's own models as agents, for the app and the CLI.</summary>
public static class LlmAgents
{
    /// <summary>One client for every run: long answers, no overall timeout (each request has its own).</summary>
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ailocalpack");
        return http;
    }

    /// <summary>The adapter of the model <paramref name="agentId"/> in <paramref name="settings"/>, or <c>null</c> when there is none.</summary>
    public static LlmAgentAdapter? Create(AppSettings settings, string agentId, ISecretStore secrets, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        return LlmConnections.Find(settings.Llms, agentId) is { } connection
            ? new LlmAgentAdapter(connection, http, ct => ReadKeyAsync(secrets, connection, ct))
            : null;
    }

    /// <summary>The connection's API key from the keychain; <c>null</c> when it has none or the keychain can't be read.</summary>
    public static async Task<string?> ReadKeyAsync(ISecretStore secrets, LlmConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.HasKey)
        {
            return null;
        }

        var read = await secrets.ReadAsync(LlmConnections.SecretName(connection.Id), cancellationToken).ConfigureAwait(false);
        return read.IsOk ? read.Value : null;
    }
}
