using AiLocalPack.Agents;
using AiLocalPack.Agents.Llm;
using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.Agents;

namespace AiLocalPack.Desktop.Tests.Settings;

/// <summary>
/// The player's own models in tests: no network, no keychain. Every server answers <see cref="Probe"/> (Ready with
/// <see cref="Models"/> by default) and the tool test <see cref="Test"/>; keys go to <see cref="Keys"/>.
/// </summary>
internal sealed class FakeLlmHost : ILlmHost
{
    public FakeSecretStore Keys { get; } = new();

    public List<string> Models { get; } = ["qwen3:30b", "llama3.1:8b"];

    /// <summary>What a server check answers; <c>null</c> = Ready with <see cref="Models"/>.</summary>
    public LlmCheck? Probe { get; set; }

    /// <summary>What the dialog's tool test answers; <c>null</c> = Ready with <see cref="Models"/>.</summary>
    public LlmCheck? Test { get; set; }

    /// <summary>Holds every check until set (a check in progress).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(string Url, string? Key)> Listed { get; } = [];

    public List<string> Tested { get; } = [];

    public async Task<LlmCheck> ProbeAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        await Wait(cancellationToken);
        return Probe ?? new LlmCheck(LlmCheckState.Ready, Models);
    }

    public async Task<LlmCheck> ListModelsAsync(string baseUrl, string? apiKey, LlmConnection? existing, CancellationToken cancellationToken = default)
    {
        Listed.Add((baseUrl, apiKey));
        await Wait(cancellationToken);
        return Probe ?? new LlmCheck(LlmCheckState.Ready, Models);
    }

    public async Task<LlmCheck> TestAsync(string baseUrl, string? apiKey, LlmConnection? existing, string model, CancellationToken cancellationToken = default)
    {
        Tested.Add(model);
        await Wait(cancellationToken);
        return Test ?? new LlmCheck(LlmCheckState.Ready, Models);
    }

    public Task<SecretResult> SaveKeyAsync(string connectionId, string apiKey, CancellationToken cancellationToken = default) =>
        Keys.WriteAsync(LlmConnections.SecretName(connectionId), apiKey, cancellationToken);

    public Task<SecretResult> DeleteKeyAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Keys.DeleteAsync(LlmConnections.SecretName(connectionId), cancellationToken);

    public IAgentAdapter? CreateAdapter(string agentId) => null;

    private async Task Wait(CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
    }
}
