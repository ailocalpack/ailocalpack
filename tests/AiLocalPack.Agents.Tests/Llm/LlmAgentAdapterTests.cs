using System.Net;
using AiLocalPack.Agents.Llm;
using AiLocalPack.Core.Llm;

namespace AiLocalPack.Agents.Tests.Llm;

public sealed class LlmAgentAdapterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly LlmTestServer _server = new();
    private readonly FakeToolHostFactory _tools = new();

    public void Dispose() => _dir.Dispose();

    private static readonly LlmRunOptions Fast = new(MaxTurns: 5, RetryDelays: [TimeSpan.Zero], MaxToolOutput: 40);

    private LlmAgentAdapter Adapter(string provider = "ollama", string? key = null) => new(
        new LlmConnection("llm-0a1b2c3d", "Qwen", provider, LlmTestServer.Base, "qwen3:30b", key is not null),
        _server.Handler.CreateClient(),
        _ => Task.FromResult(key),
        _tools,
        Fast);

    private AgentRunRequest Request(string prompt = "Translate the chunk.") => new(prompt, _dir.Path)
    {
        SystemPrompt = "You translate games.",
        McpServers = [new AgentMcpServer("ailocalpack", "app", ["--mcp-server"])],
    };

    private static Task<List<AgentEvent>> RunAsync(LlmAgentAdapter adapter, AgentRunRequest request) =>
        RunUntilAsync(adapter, request, TestContext.Current.CancellationToken);

    private static async Task<List<AgentEvent>> RunUntilAsync(LlmAgentAdapter adapter, AgentRunRequest request, CancellationToken cancellationToken)
    {
        var installation = (await adapter.DetectAsync(cancellationToken))!;
        var events = new List<AgentEvent>();
        await foreach (var e in adapter.RunAsync(installation, request, cancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task A_tool_call_is_passed_to_the_tool_and_its_output_back_to_the_model()
    {
        // Arrange
        _server.Call("mcp__ailocalpack__put_translations", """{"translations":{"a":"б"}}""").Text("Stored 1 translation.");

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        (_tools.Calls.Single(), _server.Sent[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>(), events.OfType<AgentDone>().Single())
            .Should().Be((("mcp__ailocalpack__put_translations", """{"translations":{"a":"б"}}"""), "mcp__ailocalpack__put_translations done",
                new AgentDone(AgentOutcome.Succeeded, "Stored 1 translation.")));
    }

    [Fact]
    public async Task The_run_reports_its_session_tool_call_result_usage_and_end_in_order()
    {
        // Arrange
        _server.Call("mcp__ailocalpack__put_translations", "{}").Text("Done.");

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        events.Select(e => e.GetType().Name).Should().Equal(
            "AgentSessionStarted", "AgentUsage", "AgentToolCall", "AgentToolResult", "AgentUsage", "AgentMessage", "AgentDone");
    }

    [Fact]
    public async Task The_first_request_has_the_system_prompt_the_task_and_the_tools()
    {
        // Arrange
        _server.Text("Done.");

        // Act
        await RunAsync(Adapter(), Request());

        // Assert
        var sent = _server.Sent.Single();
        (sent["model"]!.GetValue<string>(), sent["messages"]![0]!["content"]!.GetValue<string>(), sent["messages"]![1]!["content"]!.GetValue<string>(),
            sent["tools"]![0]!["function"]!["name"]!.GetValue<string>())
            .Should().Be(("qwen3:30b", "You translate games.", "Translate the chunk.", "mcp__ailocalpack__put_translations"));
    }

    [Fact]
    public async Task A_long_tool_output_is_cut_for_the_model()
    {
        // Arrange
        _tools.Answer = (_, _) => new LlmToolOutput(new string('x', 100), false);
        _server.Call("mcp__ailocalpack__put_translations", "{}").Text("Done.");

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        events.OfType<AgentToolResult>().Single().Output.Should().StartWith(new string('x', 40) + "\n… (cut: 60 more characters");
    }

    [Fact]
    public async Task A_run_that_needs_file_or_command_tools_ends_at_once()
    {
        // Arrange
        var request = Request() with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.RunCommands } };

        // Act
        var events = await RunAsync(Adapter(), request);

        // Assert
        (events.OfType<AgentError>().Single().Kind, events.OfType<AgentDone>().Single().Outcome, _server.Sent.Count)
            .Should().Be((AgentErrorKind.StartFailed, AgentOutcome.Failed, 0));
    }

    [Fact]
    public async Task A_refused_key_ends_the_run_signed_out()
    {
        // Arrange
        _server.Status(HttpStatusCode.Unauthorized);

        // Act
        var events = await RunAsync(Adapter("openrouter", "sk-test"), Request());

        // Assert
        events.OfType<AgentDone>().Single().Outcome.Should().Be(AgentOutcome.SignedOut);
    }

    [Fact]
    public async Task A_preset_that_needs_a_key_without_one_is_signed_out()
    {
        // Arrange
        var adapter = Adapter("openai");

        // Act
        var auth = await adapter.GetAuthStatusAsync((await adapter.DetectAsync(TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);

        // Assert
        auth.State.Should().Be(AgentAuthState.SignedOut);
    }

    [Fact]
    public async Task The_key_goes_in_the_authorization_header()
    {
        // Arrange
        _server.Text("Done.");

        // Act
        await RunAsync(Adapter("openrouter", "sk-test"), Request());

        // Assert
        _server.Handler.Requests.Single().Header("Authorization").Should().Be("Bearer sk-test");
    }

    [Fact]
    public async Task Too_many_requests_is_the_limit_with_its_reset_time()
    {
        // Arrange
        _server.Status(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(5));
        var before = DateTimeOffset.UtcNow;

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        (events.OfType<AgentLimitReached>().Single().ResetsAt > before.AddMinutes(4), events.OfType<AgentDone>().Single().Outcome)
            .Should().Be((true, AgentOutcome.LimitReached));
    }

    [Fact]
    public async Task A_server_error_is_tried_again_before_the_run_fails()
    {
        // Arrange
        _server.Status(HttpStatusCode.BadGateway).Text("Done.");

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        (events.OfType<AgentDiagnostic>().Count(), events.OfType<AgentDone>().Single().Outcome).Should().Be((1, AgentOutcome.Succeeded));
    }

    [Fact]
    public async Task A_model_that_never_stops_calling_tools_is_stopped()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
        {
            _server.Call("mcp__ailocalpack__put_translations", "{}", $"call_{i}");
        }

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        events.OfType<AgentDone>().Single().Message.Should().Be("The model was stopped after 5 answers without finishing.");
    }

    [Fact]
    public async Task A_tool_server_that_does_not_start_fails_the_run()
    {
        // Arrange
        _tools.FailStart = "The tool server \"ailocalpack\" didn't start: boom";

        // Act
        var events = await RunAsync(Adapter(), Request());

        // Assert
        (events.OfType<AgentError>().Single().Kind, events.OfType<AgentDone>().Single().Outcome).Should().Be((AgentErrorKind.StartFailed, AgentOutcome.Failed));
    }

    [Fact]
    public async Task A_resumed_session_continues_the_saved_conversation()
    {
        // Arrange
        _server.Text("First answer.").Text("Second answer.");
        var first = await RunAsync(Adapter(), Request("First task."));
        var session = first.OfType<AgentSessionStarted>().Single().SessionId;

        // Act
        await RunAsync(Adapter(), Request("Second task.") with { ResumeSessionId = session });

        // Assert
        _server.Sent[1]["messages"]!.AsArray().Select(m => m!["content"]!.GetValue<string>())
            .Should().Equal("You translate games.", "First task.", "First answer.", "Second task.");
    }

    [Fact]
    public async Task A_cancelled_run_ends_cancelled_and_stops_the_tools()
    {
        // Arrange
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _tools.Answer = (_, _) =>
        {
            cancel.Cancel();
            return new LlmToolOutput("ok", false);
        };
        _server.Call("mcp__ailocalpack__put_translations", "{}").Text("Done.");

        // Act
        var events = await RunUntilAsync(Adapter(), Request(), cancel.Token);

        // Assert
        (events.OfType<AgentDone>().Single().Outcome, _tools.Disposed).Should().Be((AgentOutcome.Cancelled, true));
    }
}
