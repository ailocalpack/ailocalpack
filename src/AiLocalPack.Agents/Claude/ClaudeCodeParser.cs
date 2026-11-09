using System.Globalization;
using System.Text.Json;

namespace AiLocalPack.Agents.Claude;

/// <summary>
/// Reads Claude Code's <c>--output-format stream-json --verbose</c> output (task 41; the Agent SDK's message types, checked
/// against Claude Code 2.1.319): one JSON object per line with a <c>type</c>.
/// <list type="bullet">
/// <item><c>system</c> / <c>init</c> → <see cref="AgentSessionStarted"/> (<c>session_id</c>, <c>model</c>); other system
/// messages (hooks, thinking tokens, denied permissions) are left out.</item>
/// <item><c>assistant</c>: each <c>text</c> block → <see cref="AgentMessage"/>, each <c>tool_use</c> →
/// <see cref="AgentToolCall"/> (thinking left out); an assistant message with <c>error</c> is Claude Code reporting a
/// failed API call in its text: <c>authentication_failed</c> / <c>oauth_org_not_allowed</c> →
/// <see cref="AgentErrorKind.SignedOut"/>, <c>rate_limit</c> → <see cref="AgentLimitReached"/>, anything else →
/// <see cref="AgentErrorKind.Reported"/>.</item>
/// <item><c>user</c>: each <c>tool_result</c> block → <see cref="AgentToolResult"/> (text, or its text blocks joined).</item>
/// <item><c>rate_limit_event</c> with status <c>rejected</c> → <see cref="AgentLimitReached"/> at its <c>resetsAt</c>
/// (Unix seconds); <c>allowed</c> / <c>allowed_warning</c> → <see cref="AgentLimitStatus"/> with the utilization of the
/// event's window (<c>unifiedWindows[rateLimitType]</c>, else <c>utilization</c>), when it has one.</item>
/// <item><c>result</c> → <see cref="AgentUsage"/> of the whole run (tokens, <c>total_cost_usd</c>), then the verdict:
/// <c>success</c> without <c>is_error</c> → succeeded with <c>result</c>, else failed with its text or <c>errors</c>.</item>
/// </list>
/// A limit is reported once (the rejected event, else the assistant error, whose old <c>…|&lt;unix seconds&gt;</c> text
/// names the reset). Unknown message types are left out (newer Claude Code); a line that isn't a JSON object with a
/// <c>type</c> is <see cref="AgentErrorKind.Malformed"/>.
/// </summary>
public sealed class ClaudeCodeParser : IAgentOutputParser
{
    private int _line;
    private bool _limitReported;
    private DateTimeOffset? _rejectedUntil;

    public IEnumerable<AgentEvent> ParseLine(string line)
    {
        _line++;
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { } type)
            {
                return [Malformed("not a Claude Code message (no \"type\")")];
            }

            return type switch
            {
                "system" => System(root),
                "assistant" => Assistant(root),
                "user" => User(root),
                "rate_limit_event" => RateLimit(root),
                "result" => Result(root),
                _ => [],
            };
        }
        catch (JsonException e)
        {
            return [Malformed($"not JSON ({e.Message})")];
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return [Malformed($"unexpected value ({e.Message})")];
        }
    }

    private AgentError Malformed(string reason) =>
        new(AgentErrorKind.Malformed, string.Create(CultureInfo.InvariantCulture, $"Output line {_line}: {reason}."));

    private static IEnumerable<AgentEvent> System(JsonElement root) =>
        Text(root, "subtype") == "init" ? [new AgentSessionStarted(Text(root, "session_id"), Text(root, "model"))] : [];

    private IEnumerable<AgentEvent> Assistant(JsonElement root)
    {
        var content = Content(root);
        if (Text(root, "error") is { } error)
        {
            var text = string.Join("\n", content.Where(c => Text(c, "type") == "text").Select(c => Text(c, "text") ?? string.Empty));
            text = text.Length > 0 ? text : $"Claude Code reported \"{error}\".";
            return ApiError(error, text);
        }

        var events = new List<AgentEvent>();
        foreach (var block in content)
        {
            switch (Text(block, "type"))
            {
                case "text" when Text(block, "text") is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text):
                    events.Add(new AgentMessage(text));
                    break;
                case "tool_use" when Text(block, "id") is { } id:
                    var input = block.TryGetProperty("input", out var value) ? value.GetRawText() : "{}";
                    events.Add(new AgentToolCall(id, Text(block, "name") ?? string.Empty, input));
                    break;
            }
        }

        return events;
    }

    private IEnumerable<AgentEvent> ApiError(string error, string text)
    {
        switch (error)
        {
            case "authentication_failed" or "oauth_org_not_allowed":
                return [new AgentError(AgentErrorKind.SignedOut, text)];
            case "rate_limit":
                if (_limitReported)
                {
                    return [];
                }

                _limitReported = true;
                var (message, resetsAt) = LegacyLimitText(text);
                return [new AgentLimitReached(_rejectedUntil ?? resetsAt, message)];
            default:
                return [new AgentError(AgentErrorKind.Reported, text)];
        }
    }

    /// <summary>Older Claude Code wrote <c>Claude AI usage limit reached|1794159000</c>: the text and the reset time.</summary>
    private static (string Message, DateTimeOffset? ResetsAt) LegacyLimitText(string text)
    {
        var bar = text.LastIndexOf('|');
        if (bar > 0 && long.TryParse(text.AsSpan(bar + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return (text[..bar].Trim(), FromUnixSeconds(seconds));
        }

        return (text, null);
    }

    private static IEnumerable<AgentEvent> User(JsonElement root)
    {
        var events = new List<AgentEvent>();
        foreach (var block in Content(root))
        {
            if (Text(block, "type") == "tool_result" && Text(block, "tool_use_id") is { } id)
            {
                var isError = block.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
                events.Add(new AgentToolResult(id, ToolOutput(block), isError));
            }
        }

        return events;
    }

    private static string ToolOutput(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString()!,
            JsonValueKind.Array => string.Join(
                "\n",
                content.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Object && Text(c, "type") == "text")
                    .Select(c => Text(c, "text") ?? string.Empty)),
            _ => string.Empty,
        };
    }

    private IEnumerable<AgentEvent> RateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (Text(info, "status") != "rejected")
        {
            return Status(info) is { } status ? [status] : [];
        }

        _rejectedUntil = info.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds)
            ? FromUnixSeconds(seconds)
            : null;
        if (_limitReported)
        {
            return [];
        }

        _limitReported = true;
        var kind = Text(info, "rateLimitType") is { } window ? $" ({window.Replace('_', '-')})" : string.Empty;
        var message = Text(info, "errorCode") == "credits_required"
            ? "The Claude subscription's included usage is used up; usage credits are needed to go on."
            : $"Claude usage limit reached{kind}.";
        return [new AgentLimitReached(_rejectedUntil, message)];
    }

    /// <summary>The used share of the event's window: <c>unifiedWindows[rateLimitType].utilization</c>, else <c>utilization</c>.</summary>
    private static AgentLimitStatus? Status(JsonElement info)
    {
        var window = Text(info, "rateLimitType");
        var source = info;
        if (window is not null
            && info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object
            && windows.TryGetProperty(window, out var w) && w.ValueKind == JsonValueKind.Object)
        {
            source = w;
        }

        if (!source.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        DateTimeOffset? resets = source.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds)
            ? FromUnixSeconds(seconds)
            : null;
        return new AgentLimitStatus(Math.Clamp(used.GetDouble(), 0, 1), window, resets);
    }

    private static IEnumerable<AgentEvent> Result(JsonElement root)
    {
        var events = new List<AgentEvent>();
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
            events.Add(new AgentUsage(
                Count(usage, "input_tokens"),
                Count(usage, "output_tokens"),
                Count(usage, "cache_read_input_tokens"),
                Count(usage, "cache_creation_input_tokens"),
                cost));
        }

        var result = Text(root, "result");
        var isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
        var subtype = Text(root, "subtype");
        if (subtype == "success" && !isError)
        {
            events.Add(new AgentDone(AgentOutcome.Succeeded, result));
            return events;
        }

        var errors = root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join("\n", list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
            : null;
        var message = !string.IsNullOrWhiteSpace(result) ? result
            : !string.IsNullOrWhiteSpace(errors) ? errors
            : $"Claude Code ended with \"{subtype ?? "an error"}\".";
        events.Add(new AgentDone(AgentOutcome.Failed, string.IsNullOrEmpty(result) ? null : result, message));
        return events;
    }

    private static List<JsonElement> Content(JsonElement root) =>
        root.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    private static DateTimeOffset? FromUnixSeconds(long seconds) =>
        seconds is > 0 and < 253_402_300_800 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
