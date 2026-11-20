using System.Text.Json;
using AiLocalPack.Core.Llm;

namespace AiLocalPack.Agents.Llm;

/// <summary>What a check of a model's server found.</summary>
public enum LlmCheckState
{
    /// <summary>The server answered and lists the model (or lists none, as some do); with the tool test, the model called the tool.</summary>
    Ready,

    /// <summary>The server answered, but its list hasn't the model.</summary>
    ModelMissing,

    /// <summary>The model answered without calling the test tool: translation needs tool calling.</summary>
    NoToolCalls,

    /// <summary>Nothing answered at the address.</summary>
    Unreachable,

    /// <summary>The server refused the key or wants one.</summary>
    KeyRefused,

    /// <summary>Anything else: the server's words are in <see cref="LlmCheck.Detail"/>.</summary>
    Failed,
}

/// <summary>A check's outcome.</summary>
/// <param name="State">What was found.</param>
/// <param name="Models">The models the server lists (empty when it didn't answer or lists none).</param>
/// <param name="Detail">The server's or network's own words for a problem, in English.</param>
public sealed record LlmCheck(LlmCheckState State, IReadOnlyList<string> Models, string? Detail = null)
{
    public bool IsReady => State == LlmCheckState.Ready;
}

/// <summary>
/// Checks one of the player's models: <see cref="ProbeAsync"/> lists the server's models (Settings rows, on start and
/// each time the section opens: no tokens spent), <see cref="TestAsync"/> also asks the model to call a test tool
/// (the <em>Add model</em> dialog's <c>Check connection</c>: one short answer). Never throws but for cancellation.
/// </summary>
public static class LlmConnectionCheck
{
    private const string TestTool = "report_ready";

    public static async Task<LlmCheck> ProbeAsync(HttpClient http, string baseUrl, string? apiKey, string? model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var client = new OpenAiChatClient(http, baseUrl, apiKey);
        try
        {
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var missing = model is { Length: > 0 } && models.Count > 0 && !models.Contains(model, StringComparer.Ordinal);
            return new LlmCheck(missing ? LlmCheckState.ModelMissing : LlmCheckState.Ready, models);
        }
        catch (LlmRequestException e)
        {
            return Problem(e);
        }
    }

    public static async Task<LlmCheck> TestAsync(HttpClient http, string baseUrl, string? apiKey, string model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(model);
        var probe = await ProbeAsync(http, baseUrl, apiKey, model, cancellationToken).ConfigureAwait(false);
        if (probe.State is not (LlmCheckState.Ready or LlmCheckState.ModelMissing))
        {
            return probe;
        }

        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"word":{"type":"string","description":"The word to report."}},"required":["word"]}""");
        var tool = new LlmTool(TestTool, "Reports that the model is ready.", schema.RootElement.Clone());
        var client = new OpenAiChatClient(http, baseUrl, apiKey);
        try
        {
            var reply = await client.CompleteAsync(
                model,
                [OpenAiChatClient.UserMessage($"Call the {TestTool} tool with the word \"ready\". Do not answer with text.")],
                [tool],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return reply.ToolCalls.Any(c => c.Name == TestTool)
                ? probe with { State = LlmCheckState.Ready }
                : probe with { State = LlmCheckState.NoToolCalls, Detail = reply.Content };
        }
        catch (LlmRequestException e)
        {
            // A 404 here is the model, not the address (the list just answered).
            return e.Kind == LlmFailureKind.NotFound ? probe with { State = LlmCheckState.ModelMissing, Detail = e.Message } : Problem(e) with { Models = probe.Models };
        }
    }

    private static LlmCheck Problem(LlmRequestException e) => new(
        e.Kind switch
        {
            LlmFailureKind.Unreachable => LlmCheckState.Unreachable,
            LlmFailureKind.Unauthorized => LlmCheckState.KeyRefused,
            _ => LlmCheckState.Failed,
        },
        [],
        e.Message);
}
