using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiLocalPack.Agents.Llm;

/// <summary>Why a request to the model's server didn't give an answer.</summary>
public enum LlmFailureKind
{
    /// <summary>Nothing answered at the address (connection refused, unknown host, timed out).</summary>
    Unreachable,

    /// <summary>The server refused the API key, or wants one (401 / 403).</summary>
    Unauthorized,

    /// <summary>Too many requests or the account's quota is used up (429).</summary>
    RateLimited,

    /// <summary>No such address or model on the server (404).</summary>
    NotFound,

    /// <summary>The server refused the request (another 4xx): often a model without tool calling.</summary>
    Refused,

    /// <summary>The server failed (5xx).</summary>
    ServerError,

    /// <summary>An answer that isn't the OpenAI format.</summary>
    Malformed,
}

/// <summary>A request that failed, with the server's own words when it gave any.</summary>
public sealed class LlmRequestException(LlmFailureKind kind, string message, DateTimeOffset? retryAt = null, Exception? inner = null)
    : Exception(message, inner)
{
    public LlmFailureKind Kind { get; } = kind;

    /// <summary>When the server said to come back (a 429's <c>Retry-After</c>).</summary>
    public DateTimeOffset? RetryAt { get; } = retryAt;

    /// <summary>Worth trying again in a moment (the server or the network, not the request).</summary>
    public bool IsTransient => Kind is LlmFailureKind.Unreachable or LlmFailureKind.ServerError;
}

/// <summary>A tool the model may call: its name, what it does and its arguments' JSON Schema.</summary>
public sealed record LlmTool(string Name, string? Description, JsonElement Parameters);

/// <summary>A tool call in the model's answer (arguments as the JSON text it wrote).</summary>
public sealed record LlmToolCall(string Id, string Name, string Arguments);

/// <summary>Token counts of one answer.</summary>
public sealed record LlmUsage(long InputTokens, long OutputTokens, long CachedTokens);

/// <summary>One answer: its text, the tools it calls, why it stopped and its usage.</summary>
public sealed record LlmReply(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, string? FinishReason, LlmUsage? Usage, string? Model);

/// <summary>
/// The OpenAI Chat Completions API as every compatible server serves it (Ollama, LM Studio, vLLM, llama.cpp, OpenRouter,
/// OpenAI): <c>GET {base}/models</c> and <c>POST {base}/chat/completions</c> with <c>tools</c>, without streaming. Messages
/// are kept as JSON objects (<c>{role, content, tool_calls?, tool_call_id?}</c>) so a session is saved and resumed as they
/// are. Only the fields every server knows are sent: no reasoning effort, no response format.
/// </summary>
public sealed class OpenAiChatClient(HttpClient http, string baseUrl, string? apiKey)
{
    /// <summary>How long one answer may take: a local model on a modest PC writes slowly.</summary>
    public static TimeSpan AnswerTimeout { get; } = TimeSpan.FromMinutes(10);

    private const int ErrorTextLength = 400;

    private readonly string _base = baseUrl.TrimEnd('/');

    /// <summary>The model IDs the server lists, sorted.</summary>
    /// <exception cref="LlmRequestException">The server didn't answer with a list.</exception>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "/models");
        var json = await SendAsync(request, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (json["data"] is not JsonArray data)
        {
            throw new LlmRequestException(LlmFailureKind.Malformed, "The server's model list has no \"data\".");
        }

        return [.. data.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The model's answer to <paramref name="messages"/>, with <paramref name="tools"/> offered.</summary>
    /// <exception cref="LlmRequestException">No answer.</exception>
    public async Task<LlmReply> CompleteAsync(string model, IEnumerable<JsonObject> messages, IReadOnlyList<LlmTool> tools, bool requireTool = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray([.. messages.Select(m => m.DeepClone())]),
            ["stream"] = false,
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. tools.Select(ToolJson)]);
            body["tool_choice"] = requireTool ? "required" : "auto";
        }

        using var request = Request(HttpMethod.Post, "/chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await SendAsync(request, AnswerTimeout, cancellationToken).ConfigureAwait(false);
        return Read(json);
    }

    /// <summary>Reads a completion; also used for a session's saved messages.</summary>
    internal static LlmReply Read(JsonNode json)
    {
        if (json["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["message"] is not JsonObject message)
        {
            // Some servers answer 200 with an error object (OpenRouter's upstream failures).
            if (json["error"] is { } error)
            {
                throw new LlmRequestException(LlmFailureKind.ServerError, Short(ErrorText(error)));
            }

            throw new LlmRequestException(LlmFailureKind.Malformed, "The answer has no \"choices\".");
        }

        var calls = new List<LlmToolCall>();
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            var n = 0;
            foreach (var call in toolCalls)
            {
                n++;
                if (call?["function"] is not JsonObject function || Text(function["name"]) is not { Length: > 0 } name)
                {
                    continue;
                }

                // Arguments are a JSON string by the spec; some local servers give the object itself.
                var arguments = function["arguments"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject o => o.ToJsonString(),
                    _ => "{}",
                };
                calls.Add(new LlmToolCall(Text(call["id"]) is { Length: > 0 } id ? id : $"call_{n}", name, arguments));
            }
        }

        var content = message["content"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray parts => string.Concat(parts.Select(p => Text(p?["text"]))),
            _ => null,
        };
        LlmUsage? usage = null;
        if (json["usage"] is JsonObject u)
        {
            usage = new LlmUsage(Long(u["prompt_tokens"]), Long(u["completion_tokens"]), Long(u["prompt_tokens_details"]?["cached_tokens"]));
        }

        return new LlmReply(content, calls, Text(choices[0]?["finish_reason"]), usage, Text(json["model"]));
    }

    /// <summary>The assistant message that goes back into the conversation after <paramref name="reply"/>.</summary>
    public static JsonObject AssistantMessage(LlmReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content ?? string.Empty };
        if (reply.ToolCalls.Count > 0)
        {
            message["tool_calls"] = new JsonArray([.. reply.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        }

        return message;
    }

    public static JsonObject UserMessage(string text) => new() { ["role"] = "user", ["content"] = text };

    public static JsonObject SystemMessage(string text) => new() { ["role"] = "system", ["content"] = text };

    public static JsonObject ToolMessage(string callId, string output) => new() { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = output };

    private static JsonObject ToolJson(LlmTool tool)
    {
        var function = new JsonObject
        {
            ["name"] = tool.Name,
            ["parameters"] = tool.Parameters.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(tool.Parameters.GetRawText())
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        };
        if (tool.Description is { Length: > 0 } description)
        {
            function["description"] = description;
        }

        return new JsonObject { ["type"] = "function", ["function"] = function };
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _base + path);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmRequestException(LlmFailureKind.Unreachable, $"{_base} didn't answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            throw new LlmRequestException(LlmFailureKind.Unreachable, $"Can't reach {_base}: {e.Message}", inner: e);
        }

        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(timer.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new LlmRequestException(LlmFailureKind.Unreachable, $"The answer from {_base} broke off: {e.Message}", inner: e);
            }

            if (!response.IsSuccessStatusCode)
            {
                var said = Short(ServerWords(text));
                var status = (int)response.StatusCode;
                var message = said.Length > 0 ? $"HTTP {status}: {said}" : $"HTTP {status} {response.ReasonPhrase}";
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LlmRequestException(LlmFailureKind.Unauthorized, message),
                    HttpStatusCode.TooManyRequests => new LlmRequestException(LlmFailureKind.RateLimited, message, RetryAt(response)),
                    HttpStatusCode.NotFound => new LlmRequestException(LlmFailureKind.NotFound, message),
                    _ when status >= 500 => new LlmRequestException(LlmFailureKind.ServerError, message),
                    _ => new LlmRequestException(LlmFailureKind.Refused, message),
                };
            }

            try
            {
                return JsonNode.Parse(text) ?? throw new LlmRequestException(LlmFailureKind.Malformed, "The server answered with an empty body.");
            }
            catch (JsonException e)
            {
                throw new LlmRequestException(LlmFailureKind.Malformed, $"The server's answer isn't JSON: {Short(text)}", inner: e);
            }
        }
    }

    private static DateTimeOffset? RetryAt(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Date: { } date } => date,
        { Delta: { } delta } => DateTimeOffset.UtcNow + delta,
        _ => null,
    };

    /// <summary>The <c>error.message</c> of an error body, else the body itself.</summary>
    private static string ServerWords(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is { } json && json["error"] is { } error)
            {
                return ErrorText(error);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text itself.
        }

        return body.Trim();
    }

    private static string ErrorText(JsonNode error) => error switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => Text(error["message"]) ?? error.ToJsonString(),
    };

    private static string Short(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= ErrorTextLength ? flat : flat[..ErrorTextLength] + "…";
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Long(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0
        : 0;
}
