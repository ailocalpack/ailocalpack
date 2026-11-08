using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiLocalPack.Agents.Codex;

/// <summary>
/// Reads <c>codex exec --json</c> output (task 42; <c>codex-rs/exec/src/exec_events.rs</c>, checked against Codex 0.167.0):
/// one JSON object per line with a <c>type</c>.
/// <list type="bullet">
/// <item><c>thread.started</c> → <see cref="AgentSessionStarted"/> (<c>thread_id</c>; Codex doesn't name its model, so the
/// requested one, if any).</item>
/// <item><c>item.started</c> / <c>item.completed</c> by item type: <c>agent_message</c> (completed) →
/// <see cref="AgentMessage"/>, the last one being the run's result; <c>command_execution</c> → tool <c>command</c>
/// (<c>{"command"}</c>), its result = the output, an error when the exit code isn't 0 or it was declined;
/// <c>file_change</c> → tool <c>file_change</c> (<c>{"changes"}</c>), result = one <c>kind path</c> line per change;
/// <c>mcp_tool_call</c> → tool <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> (as Claude Code names them) with its arguments,
/// result = its text blocks or error; <c>web_search</c> → tool <c>web_search</c>. A call is reported once, when its item
/// first shows up; a completed item without a start reports both. <c>error</c> items (non-fatal notices) →
/// <see cref="AgentDiagnostic"/>; reasoning, to-do lists and updates are left out.</item>
/// <item><c>turn.completed</c> → <see cref="AgentUsage"/> (input without the cached part, output, cached input as cache
/// read, cache writes; no cost) then the verdict: succeeded with the last message.</item>
/// <item><c>error</c>: a <c>Reconnecting… n/5</c> retry → <see cref="AgentDiagnostic"/>; a usage limit
/// (<c>You’ve hit your usage limit</c>) → <see cref="AgentLimitReached"/> at the time its text names (<c>Try again at
/// 6:10 PM.</c> today, or <c>Nov 9th, 2026 6:10 PM</c>, in the machine's time zone); a refused sign-in (<c>401
/// Unauthorized</c>, <c>sign in again</c>) → <see cref="AgentErrorKind.SignedOut"/>; anything else →
/// <see cref="AgentErrorKind.Reported"/>. Each limit / sign-in problem is reported once.</item>
/// <item><c>turn.failed</c> → failed with its message (the limit or sign-in found in it too, when no <c>error</c> named it).</item>
/// </list>
/// Unknown types are left out; a line that isn't a JSON object with a <c>type</c> is <see cref="AgentErrorKind.Malformed"/>.
/// </summary>
/// <param name="model">The model the run asked for, reported with the session.</param>
/// <param name="clock">Today's date and the time zone a reset time is read in.</param>
public sealed partial class CodexParser(string? model = null, TimeProvider? clock = null) : IAgentOutputParser
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly HashSet<string> _calls = new(StringComparer.Ordinal);
    private int _line;
    private string? _lastMessage;
    private bool _limitReported;
    private bool _signedOutReported;

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
                return [Malformed("not a Codex event (no \"type\")")];
            }

            return type switch
            {
                "thread.started" => [new AgentSessionStarted(Text(root, "thread_id"), model)],
                "item.started" => Item(root, completed: false),
                "item.completed" => Item(root, completed: true),
                "turn.completed" => TurnCompleted(root),
                "turn.failed" => TurnFailed(root),
                "error" => Error(Text(root, "message") ?? "Codex reported an error."),
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

    private IEnumerable<AgentEvent> Item(JsonElement root, bool completed)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var id = Text(item, "id") ?? string.Empty;
        switch (Text(item, "type"))
        {
            case "agent_message" when completed && Text(item, "text") is { } text && !string.IsNullOrWhiteSpace(text):
                _lastMessage = text;
                return [new AgentMessage(text)];
            case "command_execution":
                return Tool(id, completed, "command", Json(w => w.WriteString("command", Text(item, "command"))), () =>
                {
                    var exit = item.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : (int?)null;
                    var status = Text(item, "status");
                    return (Text(item, "aggregated_output") ?? string.Empty, status is "failed" or "declined" || exit is not (null or 0));
                });
            case "file_change":
                return Tool(id, completed, "file_change", Raw(item, "changes", "changes"), () =>
                {
                    var lines = item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
                        ? changes.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).Select(c => $"{Text(c, "kind")} {Text(c, "path")}")
                        : [];
                    return (string.Join("\n", lines), Text(item, "status") == "failed");
                });
            case "mcp_tool_call":
                var name = $"mcp__{Text(item, "server")}__{Text(item, "tool")}";
                var input = item.TryGetProperty("arguments", out var arguments) && arguments.ValueKind != JsonValueKind.Null ? arguments.GetRawText() : "{}";
                return Tool(id, completed, name, input, () => McpResult(item));
            case "web_search":
                return Tool(id, completed, "web_search", Json(w => w.WriteString("query", Text(item, "query"))), () => (string.Empty, false));
            case "error" when completed && Text(item, "message") is { } notice:
                return [new AgentDiagnostic(notice)];
            default:
                return [];
        }
    }

    private List<AgentEvent> Tool(string id, bool completed, string name, string input, Func<(string Output, bool IsError)> result)
    {
        var events = new List<AgentEvent>();
        if (_calls.Add(id))
        {
            events.Add(new AgentToolCall(id, name, input));
        }

        if (completed)
        {
            var (output, isError) = result();
            events.Add(new AgentToolResult(id, output, isError));
        }

        return events;
    }

    private static (string, bool) McpResult(JsonElement item)
    {
        if (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            return (Text(error, "message") ?? "The tool failed.", true);
        }

        var text = item.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array
                ? string.Join(
                    "\n",
                    content.EnumerateArray()
                        .Where(c => c.ValueKind == JsonValueKind.Object && Text(c, "type") == "text")
                        .Select(c => Text(c, "text") ?? string.Empty))
                : string.Empty;
        return (text, Text(item, "status") == "failed");
    }

    private IEnumerable<AgentEvent> TurnCompleted(JsonElement root)
    {
        var events = new List<AgentEvent>();
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var cached = Count(usage, "cached_input_tokens");
            events.Add(new AgentUsage(
                Math.Max(0, Count(usage, "input_tokens") - cached),
                Count(usage, "output_tokens"),
                cached,
                Count(usage, "cache_write_input_tokens")));
        }

        events.Add(new AgentDone(AgentOutcome.Succeeded, _lastMessage));
        return events;
    }

    private IEnumerable<AgentEvent> TurnFailed(JsonElement root)
    {
        var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "message")
            : null;
        message ??= "The Codex turn failed.";
        var events = Problem(message, otherwise: null).ToList();
        events.Add(new AgentDone(AgentOutcome.Failed, _lastMessage, message));
        return events;
    }

    private IEnumerable<AgentEvent> Error(string message) =>
        Retry().IsMatch(message) ? [new AgentDiagnostic(message)] : Problem(message, otherwise: new AgentError(AgentErrorKind.Reported, message));

    /// <summary>The limit or sign-in problem <paramref name="message"/> names (once each), else <paramref name="otherwise"/>.</summary>
    private IEnumerable<AgentEvent> Problem(string message, AgentEvent? otherwise)
    {
        if (UsageLimit().IsMatch(message))
        {
            if (_limitReported)
            {
                return [];
            }

            _limitReported = true;
            return [new AgentLimitReached(ResetTime(message), message)];
        }

        if (SignedOutText().IsMatch(message))
        {
            if (_signedOutReported)
            {
                return [];
            }

            _signedOutReported = true;
            return [new AgentError(AgentErrorKind.SignedOut, message)];
        }

        return otherwise is null ? [] : [otherwise];
    }

    /// <summary>
    /// The reset time in a limit message, as Codex formats it in the machine's time zone: <c>h:mm AM</c> today, else
    /// <c>Mon d(st|nd|rd|th), yyyy h:mm AM</c>; <c>null</c> when the text names none.
    /// </summary>
    private DateTimeOffset? ResetTime(string message)
    {
        var match = TryAgainAt().Match(message);
        if (!match.Success)
        {
            return null;
        }

        var zone = _clock.LocalTimeZone;
        var when = DaySuffix().Replace(match.Groups["when"].Value.Trim(), "$1");
        DateTime local;
        if (DateTime.TryParseExact(when, "h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var today = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone).Date;
            local = today + time.TimeOfDay;
        }
        else if (!DateTime.TryParseExact(when, "MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            write(json);
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Raw(JsonElement element, string property, string name) =>
        element.TryGetProperty(property, out var value) ? Json(w => { w.WritePropertyName(name); value.WriteTo(w); }) : "{}";

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    [GeneratedRegex(@"^Reconnecting\.\.\.\s*\d+/\d+")]
    private static partial Regex Retry();

    [GeneratedRegex("hit your usage limit", RegexOptions.IgnoreCase)]
    private static partial Regex UsageLimit();

    [GeneratedRegex(@"401 Unauthorized|sign in again|Not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex SignedOutText();

    [GeneratedRegex(@"try again at (?<when>[^.]+?)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TryAgainAt();

    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)\b")]
    private static partial Regex DaySuffix();
}
