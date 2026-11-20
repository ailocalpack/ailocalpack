using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Llm;

/// <summary>Limits of a run of the player's own model (tests shorten the waits).</summary>
/// <param name="MaxTurns">Answers the model may give in one run before it is stopped.</param>
/// <param name="RetryDelays">Waits before trying a failed request again (server or network trouble), one per retry.</param>
/// <param name="MaxToolOutput">Characters of a tool's output the model gets; the rest is cut with a note.</param>
public sealed record LlmRunOptions(int MaxTurns, IReadOnlyList<TimeSpan> RetryDelays, int MaxToolOutput)
{
    public static LlmRunOptions Default { get; } = new(200, [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)], 60_000);
}

/// <summary>
/// The player's own model as an agent (Settings → AI agents → <em>Your models</em>): the app runs the agent loop itself
/// over the OpenAI Chat Completions API. A run starts the request's MCP servers (the toolkit server) through
/// <see cref="ILlmToolHostFactory"/>, offers their tools, and passes calls on until the model answers without one; the
/// last answer is the result. It has no built-in file, edit or command tools and no sandbox, so a request that needs them
/// (the agent's own reader for an unknown engine) ends at once with <see cref="AgentErrorKind.StartFailed"/>; web search
/// isn't offered (a diagnostic says so). A session is the conversation saved as JSON in the working folder
/// (<c>.llm-sessions/&lt;id&gt;.json</c>), so a fix run can resume it. Keeps the <see cref="IAgentAdapter"/> contract:
/// the stream never throws and ends with one <see cref="AgentDone"/>; a refused key is <see cref="AgentOutcome.SignedOut"/>,
/// a 429 <see cref="AgentOutcome.LimitReached"/>.
/// </summary>
public sealed class LlmAgentAdapter(
    LlmConnection connection,
    HttpClient http,
    Func<CancellationToken, Task<string?>> readKey,
    ILlmToolHostFactory? tools = null,
    LlmRunOptions? options = null) : IAgentAdapter
{
    public const string SessionsFolder = ".llm-sessions";

    private readonly ILlmToolHostFactory _tools = tools ?? McpToolHostFactory.Instance;
    private readonly LlmRunOptions _options = options ?? LlmRunOptions.Default;

    public LlmConnection Connection { get; } = connection ?? throw new ArgumentNullException(nameof(connection));

    public string Id => Connection.Id;

    public string DisplayName => Connection.Name;

    /// <summary>Always "installed": the server's address as the program, the model as the version.</summary>
    public Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AgentInstallation?>(new AgentInstallation(Connection.Id, Connection.BaseUrl, Connection.Model, AgentSource.Configured));

    /// <summary>
    /// Signed in unless the preset needs a key and the keychain has none (nothing is sent to the server here; Settings
    /// checks the server itself).
    /// </summary>
    public async Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        if (!LlmProviders.Of(Connection.Provider).NeedsKey)
        {
            return new AgentAuthStatus(AgentAuthState.SignedIn, Connection.Host);
        }

        var key = await readKey(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(key)
            ? new AgentAuthStatus(AgentAuthState.SignedOut, Detail: "No API key is saved for this model.")
            : new AgentAuthStatus(AgentAuthState.SignedIn, Connection.Host);
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentInstallation installation, AgentRunRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(request);
        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var worker = Task.Run(
            async () =>
            {
                AgentDone done;
                try
                {
                    done = await RunLoopAsync(request, e => channel.Writer.TryWrite(e), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    done = new AgentDone(AgentOutcome.Cancelled, Message: "Cancelled.");
                }
                catch (Exception e)
                {
                    channel.Writer.TryWrite(new AgentError(AgentErrorKind.Reported, e.Message));
                    done = new AgentDone(AgentOutcome.Failed, Message: e.Message);
                }

                channel.Writer.TryWrite(done);
                channel.Writer.TryComplete();
            },
            CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return e;
        }

        await worker.ConfigureAwait(false);
    }

    private async Task<AgentDone> RunLoopAsync(AgentRunRequest request, Action<AgentEvent> emit, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new AgentDone(AgentOutcome.Cancelled, Message: "Cancelled before the model started.");
        }

        if (request.BuiltinTools.Any(t => t != AgentBuiltinTool.WebSearch))
        {
            const string message = "Your own models have no file or command tools, so they can't write their own reader for this game. Use Claude Code or Codex, or a game reader from the toolkit.";
            emit(new AgentError(AgentErrorKind.StartFailed, message));
            return new AgentDone(AgentOutcome.Failed, Message: message);
        }

        if (request.BuiltinTools.Contains(AgentBuiltinTool.WebSearch))
        {
            emit(new AgentDiagnostic("Web search isn't available to your own models; the run goes on without it."));
        }

        var key = await readKey(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key) && LlmProviders.Of(Connection.Provider).NeedsKey)
        {
            const string message = "No API key is saved for this model: add it in Settings → AI agents.";
            emit(new AgentError(AgentErrorKind.SignedOut, message));
            return new AgentDone(AgentOutcome.SignedOut, Message: message);
        }

        var client = new OpenAiChatClient(http, Connection.BaseUrl, key);
        var model = request.Model ?? Connection.Model;
        var (sessionId, messages) = StartSession(request);
        emit(new AgentSessionStarted(sessionId, model));

        ILlmToolHost toolHost;
        try
        {
            toolHost = await _tools.StartAsync(request.McpServers, request.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmToolStartException e)
        {
            emit(new AgentError(AgentErrorKind.StartFailed, e.Message));
            return new AgentDone(AgentOutcome.Failed, Message: e.Message);
        }

        await using (toolHost.ConfigureAwait(false))
        {
            string? last = null;
            for (var turn = 0; turn < _options.MaxTurns; turn++)
            {
                LlmReply reply;
                try
                {
                    reply = await CompleteWithRetriesAsync(client, model, messages, toolHost.Tools, emit, cancellationToken).ConfigureAwait(false);
                }
                catch (LlmRequestException e)
                {
                    Save(request.WorkingDirectory, sessionId, messages);
                    return Stopped(e, model, emit);
                }

                if (reply.Usage is { } usage)
                {
                    emit(new AgentUsage(usage.InputTokens - usage.CachedTokens, usage.OutputTokens, usage.CachedTokens));
                }

                messages.Add(OpenAiChatClient.AssistantMessage(reply));
                if (reply.Content is { Length: > 0 } content && !string.IsNullOrWhiteSpace(content))
                {
                    last = content.Trim();
                    emit(new AgentMessage(last));
                }

                if (reply.ToolCalls.Count == 0)
                {
                    Save(request.WorkingDirectory, sessionId, messages);
                    return new AgentDone(AgentOutcome.Succeeded, Result: last ?? string.Empty);
                }

                foreach (var call in reply.ToolCalls)
                {
                    emit(new AgentToolCall(call.Id, call.Name, NormalizedInput(call.Arguments)));
                    var output = await toolHost.CallAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
                    var text = output.Text.Length <= _options.MaxToolOutput
                        ? output.Text
                        : output.Text[.._options.MaxToolOutput] + $"\n… (cut: {output.Text.Length - _options.MaxToolOutput} more characters; ask for less at a time)";
                    emit(new AgentToolResult(call.Id, text, output.IsError));
                    messages.Add(OpenAiChatClient.ToolMessage(call.Id, text));
                }

                Save(request.WorkingDirectory, sessionId, messages);
            }

            var stopped = $"The model was stopped after {_options.MaxTurns} answers without finishing.";
            emit(new AgentError(AgentErrorKind.Reported, stopped));
            return new AgentDone(AgentOutcome.Failed, Result: last, Message: stopped);
        }
    }

    private async Task<LlmReply> CompleteWithRetriesAsync(
        OpenAiChatClient client, string model, List<JsonObject> messages, IReadOnlyList<LlmTool> tools, Action<AgentEvent> emit, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.CompleteAsync(model, messages, tools, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (LlmRequestException e) when (e.IsTransient && attempt < _options.RetryDelays.Count)
            {
                emit(new AgentDiagnostic($"{e.Message} Trying again in {_options.RetryDelays[attempt].TotalSeconds:0} s."));
                await Task.Delay(_options.RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private AgentDone Stopped(LlmRequestException e, string model, Action<AgentEvent> emit)
    {
        switch (e.Kind)
        {
            case LlmFailureKind.Unauthorized:
                var refused = $"{Connection.Host} refused the API key ({e.Message}): check it in Settings → AI agents.";
                emit(new AgentError(AgentErrorKind.SignedOut, refused));
                return new AgentDone(AgentOutcome.SignedOut, Message: refused);
            case LlmFailureKind.RateLimited:
                emit(new AgentLimitReached(e.RetryAt, e.Message));
                return new AgentDone(AgentOutcome.LimitReached, Message: e.Message);
            default:
                var message = e.Kind switch
                {
                    LlmFailureKind.NotFound => $"{Connection.Host} has no model \"{model}\" ({e.Message}).",
                    LlmFailureKind.Refused => $"{Connection.Host} refused the request ({e.Message}). The model may not support tool calls, or the conversation got longer than its context.",
                    _ => e.Message,
                };
                emit(new AgentError(AgentErrorKind.Reported, message));
                return new AgentDone(AgentOutcome.Failed, Message: message);
        }
    }

    /// <summary>A new conversation, or the saved one of <see cref="AgentRunRequest.ResumeSessionId"/> with the new task added.</summary>
    private static (string SessionId, List<JsonObject> Messages) StartSession(AgentRunRequest request)
    {
        if (request.ResumeSessionId is { } resume && Load(request.WorkingDirectory, resume) is { Count: > 0 } saved)
        {
            if (request.SystemPrompt is { } newSystem)
            {
                saved.RemoveAll(m => m["role"]?.GetValue<string>() == "system");
                saved.Insert(0, OpenAiChatClient.SystemMessage(newSystem));
            }

            saved.Add(OpenAiChatClient.UserMessage(request.Prompt));
            return (resume, saved);
        }

        var messages = new List<JsonObject>();
        if (request.SystemPrompt is { Length: > 0 } system)
        {
            messages.Add(OpenAiChatClient.SystemMessage(system));
        }

        messages.Add(OpenAiChatClient.UserMessage(request.Prompt));
        return (Guid.NewGuid().ToString("N"), messages);
    }

    internal static string SessionPath(string workingDirectory, string sessionId)
    {
        // A session ID is ours (hex), but never let one leave the folder.
        var safe = string.Concat(sessionId.Where(char.IsAsciiLetterOrDigit));
        return Path.Combine(workingDirectory, SessionsFolder, $"{safe}.json");
    }

    private static List<JsonObject>? Load(string workingDirectory, string sessionId)
    {
        try
        {
            var path = SessionPath(workingDirectory, sessionId);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray array
                ? [.. array.OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone())]
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Saves the conversation (best effort: a failed save only loses a later resume).</summary>
    private static void Save(string workingDirectory, string sessionId, List<JsonObject> messages)
    {
        try
        {
            var path = SessionPath(workingDirectory, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var array = new JsonArray([.. messages.Select(m => m.DeepClone())]);
            AtomicFile.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(array.ToJsonString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>The arguments as compact JSON for the transcript (the model's own text when it isn't JSON).</summary>
    private static string NormalizedInput(string arguments)
    {
        try
        {
            return string.IsNullOrWhiteSpace(arguments) ? "{}" : JsonNode.Parse(arguments)?.ToJsonString() ?? "{}";
        }
        catch (JsonException)
        {
            return JsonValue.Create(arguments).ToJsonString();
        }
    }
}
