using AiLocalPack.Agents;
using AiLocalPack.Agents.Llm;
using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Agents;

/// <summary>
/// The player's own models as Settings → AI agents, the <em>Add model</em> dialog and the generation services use them:
/// check a server, list its models, test tool calling, keep the API key in the keychain, make the adapter of a job.
/// Behind an interface so view models are tested with a fake; never throws but for cancellation.
/// </summary>
public interface ILlmHost
{
    /// <summary>Lists the server's models with the saved key (no tokens spent).</summary>
    Task<LlmCheck> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Lists the models of a server being added (<paramref name="apiKey"/> as typed, else the saved one of <paramref name="existing"/>).</summary>
    Task<LlmCheck> ListModelsAsync(string baseUrl, string? apiKey, LlmConnection? existing, CancellationToken cancellationToken = default);

    /// <summary>Lists the models and asks the model to call a test tool.</summary>
    Task<LlmCheck> TestAsync(string baseUrl, string? apiKey, LlmConnection? existing, string model, CancellationToken cancellationToken = default);

    Task<SecretResult> SaveKeyAsync(string connectionId, string apiKey, CancellationToken cancellationToken = default);

    Task<SecretResult> DeleteKeyAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>The adapter of the model <paramref name="agentId"/> as the settings name it now, or <c>null</c>.</summary>
    IAgentAdapter? CreateAdapter(string agentId);
}

public sealed class LlmHost(ISecretStore secrets, SettingsStore settings, ILogger<LlmHost> logger) : ILlmHost
{
    private readonly HttpClient _http = LlmAgents.CreateHttpClient();

    public async Task<LlmCheck> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var key = await LlmAgents.ReadKeyAsync(secrets, connection, cancellationToken).ConfigureAwait(false);
        if (connection.HasKey && key is null)
        {
            logger.LogWarning("Model {Id}: its API key can't be read from the keychain", connection.Id);
        }

        var check = await LlmConnectionCheck.ProbeAsync(_http, connection.BaseUrl, key, connection.Model, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Model {Id} ({Model} at {Host}): {State}{Detail}", connection.Id, connection.Model, connection.Host, check.State, check.Detail is { } d ? $" ({d})" : "");
        return check;
    }

    public async Task<LlmCheck> ListModelsAsync(string baseUrl, string? apiKey, LlmConnection? existing, CancellationToken cancellationToken = default)
    {
        var key = await KeyAsync(apiKey, existing, cancellationToken).ConfigureAwait(false);
        return await LlmConnectionCheck.ProbeAsync(_http, baseUrl, key, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LlmCheck> TestAsync(string baseUrl, string? apiKey, LlmConnection? existing, string model, CancellationToken cancellationToken = default)
    {
        var key = await KeyAsync(apiKey, existing, cancellationToken).ConfigureAwait(false);
        var check = await LlmConnectionCheck.TestAsync(_http, baseUrl, key, model, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Model test {Model} at {Url}: {State}{Detail}", model, baseUrl, check.State, check.Detail is { } d ? $" ({d})" : "");
        return check;
    }

    public Task<SecretResult> SaveKeyAsync(string connectionId, string apiKey, CancellationToken cancellationToken = default) =>
        secrets.WriteAsync(LlmConnections.SecretName(connectionId), apiKey, cancellationToken);

    public Task<SecretResult> DeleteKeyAsync(string connectionId, CancellationToken cancellationToken = default) =>
        secrets.DeleteAsync(LlmConnections.SecretName(connectionId), cancellationToken);

    public IAgentAdapter? CreateAdapter(string agentId) => LlmAgents.Create(settings.Current, agentId, secrets, _http);

    private async Task<string?> KeyAsync(string? typed, LlmConnection? existing, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(typed) ? typed.Trim()
        : existing is not null ? await LlmAgents.ReadKeyAsync(secrets, existing, cancellationToken).ConfigureAwait(false)
        : null;
}
