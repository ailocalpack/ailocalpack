using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiLocalPack.Agents;

/// <summary>
/// <see cref="AgentEvent"/>s as JSON lines, one object per event with a <c>type</c> (task 40): the fake agent's recording
/// format and the CLI's <c>agent run --json</c> output.
/// <code>
/// {"type":"session","sessionId":"s1","model":"m"}      {"type":"message","text":"…"}
/// {"type":"toolCall","id":"t1","name":"extract","input":{…}}   {"type":"toolResult","id":"t1","output":"…","isError":false}
/// {"type":"usage","inputTokens":1,"outputTokens":2,"cacheReadTokens":0,"cacheWriteTokens":0,"costUsd":0.01}
/// {"type":"limit","resetsAt":"2026-11-08T18:00:00Z","message":"…"}   {"type":"error","kind":"signedOut","message":"…"}
/// {"type":"diagnostic","text":"…"}   {"type":"done","outcome":"succeeded","result":"…","message":null,"exitCode":0}
/// </code>
/// Enum values are camelCase; unknown properties are ignored.
/// </summary>
public static class AgentEventJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The event as one line of JSON (no line break).</summary>
    public static string Write(AgentEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            switch (e)
            {
                case AgentSessionStarted s:
                    w.WriteString("type", "session");
                    w.WriteString("sessionId", s.SessionId);
                    w.WriteString("model", s.Model);
                    break;
                case AgentMessage m:
                    w.WriteString("type", "message");
                    w.WriteString("text", m.Text);
                    break;
                case AgentToolCall c:
                    w.WriteString("type", "toolCall");
                    w.WriteString("id", c.Id);
                    w.WriteString("name", c.Name);
                    w.WritePropertyName("input");
                    WriteRawOrString(w, c.Input);
                    break;
                case AgentToolResult r:
                    w.WriteString("type", "toolResult");
                    w.WriteString("id", r.CallId);
                    w.WriteString("output", r.Output);
                    w.WriteBoolean("isError", r.IsError);
                    break;
                case AgentUsage u:
                    w.WriteString("type", "usage");
                    w.WriteNumber("inputTokens", u.InputTokens);
                    w.WriteNumber("outputTokens", u.OutputTokens);
                    w.WriteNumber("cacheReadTokens", u.CacheReadTokens);
                    w.WriteNumber("cacheWriteTokens", u.CacheWriteTokens);
                    if (u.CostUsd is { } cost)
                    {
                        w.WriteNumber("costUsd", cost);
                    }
                    else
                    {
                        w.WriteNull("costUsd");
                    }

                    break;
                case AgentLimitReached l:
                    w.WriteString("type", "limit");
                    if (l.ResetsAt is { } resets)
                    {
                        w.WriteString("resetsAt", resets.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        w.WriteNull("resetsAt");
                    }

                    w.WriteString("message", l.Message);
                    break;
                case AgentError x:
                    w.WriteString("type", "error");
                    w.WriteString("kind", Name(x.Kind));
                    w.WriteString("message", x.Message);
                    break;
                case AgentDiagnostic d:
                    w.WriteString("type", "diagnostic");
                    w.WriteString("text", d.Text);
                    break;
                case AgentDone d:
                    w.WriteString("type", "done");
                    w.WriteString("outcome", Name(d.Outcome));
                    w.WriteString("result", d.Result);
                    w.WriteString("message", d.Message);
                    if (d.ExitCode is { } code)
                    {
                        w.WriteNumber("exitCode", code);
                    }
                    else
                    {
                        w.WriteNull("exitCode");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown event {e.GetType().Name}.", nameof(e));
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Reads one line; <c>null</c> with <paramref name="error"/> (a plain sentence) when it isn't an event.</summary>
    public static AgentEvent? Read(string line, out string? error)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "not a JSON object";
                return null;
            }

            var type = OptionalString(root, "type");
            AgentEvent? e = type switch
            {
                "session" => new AgentSessionStarted(OptionalString(root, "sessionId"), OptionalString(root, "model")),
                "message" => new AgentMessage(RequiredString(root, "text")),
                "toolCall" => new AgentToolCall(
                    RequiredString(root, "id"),
                    RequiredString(root, "name"),
                    root.TryGetProperty("input", out var input) && input.ValueKind != JsonValueKind.Null ? input.GetRawText() : "{}"),
                "toolResult" => new AgentToolResult(RequiredString(root, "id"), OptionalString(root, "output") ?? string.Empty, OptionalBool(root, "isError")),
                "usage" => new AgentUsage(
                    Count(root, "inputTokens"),
                    Count(root, "outputTokens"),
                    Count(root, "cacheReadTokens"),
                    Count(root, "cacheWriteTokens"),
                    root.TryGetProperty("costUsd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null),
                "limit" => new AgentLimitReached(
                    OptionalString(root, "resetsAt") is { } resets
                        ? DateTimeOffset.Parse(resets, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                        : null,
                    OptionalString(root, "message") ?? "The usage limit was reached."),
                "error" => new AgentError(Parse<AgentErrorKind>(root, "kind"), OptionalString(root, "message") ?? string.Empty),
                "diagnostic" => new AgentDiagnostic(RequiredString(root, "text")),
                "done" => new AgentDone(
                    Parse<AgentOutcome>(root, "outcome"),
                    OptionalString(root, "result"),
                    OptionalString(root, "message"),
                    root.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null),
                null => throw new FormatException("no \"type\""),
                _ => throw new FormatException($"unknown type \"{type}\""),
            };
            error = null;
            return e;
        }
        catch (JsonException)
        {
            error = "not JSON";
            return null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            error = e.Message;
            return null;
        }
    }

    private static void WriteRawOrString(Utf8JsonWriter w, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(w);
        }
        catch (JsonException)
        {
            w.WriteStringValue(json);
        }
    }

    private static string Name<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static T Parse<T>(JsonElement root, string property)
        where T : struct, Enum
    {
        var text = RequiredString(root, property);
        return Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) && !char.IsDigit(text[0])
            ? value
            : throw new FormatException($"unknown {property} \"{text}\"");
    }

    private static string RequiredString(JsonElement root, string property) =>
        OptionalString(root, property) ?? throw new FormatException($"no \"{property}\"");

    private static string? OptionalString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"\"{property}\" isn't a string")
            : null;

    private static bool OptionalBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Count(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        var count = value.GetInt64();
        return count >= 0 ? count : throw new FormatException($"\"{property}\" is negative");
    }
}
