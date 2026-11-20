using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiLocalPack.Agents.Llm;

namespace AiLocalPack.Agents.Tests.Llm;

/// <summary>A fake OpenAI-compatible server: answers <c>/chat/completions</c> from a queue, records what it was sent.</summary>
internal sealed class LlmTestServer
{
    public const string Base = "http://localhost:11434/v1";

    private readonly Queue<Func<HttpResponseMessage>> _replies = new();

    public LlmTestServer()
    {
        Handler.On(HttpMethod.Post, Base + "/chat/completions", _ => _replies.Count > 0 ? _replies.Dequeue()() : Json("""{"error":{"message":"no reply queued"}}""", HttpStatusCode.InternalServerError));
    }

    public FakeHttpHandler Handler { get; } = new();

    /// <summary>The bodies of the chat requests, parsed.</summary>
    public IReadOnlyList<JsonNode> Sent => [.. Handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)).Select(r => JsonNode.Parse(r.Body!)!)];

    public LlmTestServer Text(string text) => Queue(() => Json(Completion(new JsonObject { ["role"] = "assistant", ["content"] = text })));

    public LlmTestServer Call(string name, string arguments, string id = "call_1") => Queue(() => Json(Completion(new JsonObject
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["id"] = id,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
        }),
    })));

    public LlmTestServer Status(HttpStatusCode status, string body = """{"error":{"message":"nope"}}""", TimeSpan? retryAfter = null) => Queue(() =>
    {
        var response = Json(body, status);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    });

    private LlmTestServer Queue(Func<HttpResponseMessage> reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    private static string Completion(JsonObject message) => new JsonObject
    {
        ["model"] = "qwen3:30b",
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = "stop" }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 120, ["completion_tokens"] = 30 },
    }.ToJsonString();

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Tools without processes: each call answers from <see cref="Answer"/>; the servers asked for are recorded.</summary>
internal sealed class FakeToolHostFactory : ILlmToolHostFactory, ILlmToolHost
{
    public List<(string Name, string Arguments)> Calls { get; } = [];

    public IReadOnlyList<AgentMcpServer>? Started { get; private set; }

    public bool Disposed { get; private set; }

    public Func<string, string, LlmToolOutput> Answer { get; set; } = (name, _) => new LlmToolOutput($"{name} done", false);

    public IReadOnlyList<LlmTool> Tools { get; } =
    [
        new("mcp__ailocalpack__put_translations", "Stores translations.", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
    ];

    public string? FailStart { get; set; }

    public Task<ILlmToolHost> StartAsync(IReadOnlyList<AgentMcpServer> servers, string workingDirectory, CancellationToken cancellationToken)
    {
        Started = servers;
        return FailStart is { } message ? throw new LlmToolStartException(message) : Task.FromResult<ILlmToolHost>(this);
    }

    public Task<LlmToolOutput> CallAsync(string name, string arguments, CancellationToken cancellationToken)
    {
        Calls.Add((name, arguments));
        return Task.FromResult(Answer(name, arguments));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
