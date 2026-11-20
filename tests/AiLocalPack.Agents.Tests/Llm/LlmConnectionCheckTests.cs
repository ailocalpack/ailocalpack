using System.Net;
using AiLocalPack.Agents.Llm;

namespace AiLocalPack.Agents.Tests.Llm;

public sealed class LlmConnectionCheckTests
{
    private readonly LlmTestServer _server = new();

    private void Models(params string[] ids) =>
        _server.Handler.OnGet(LlmTestServer.Base + "/models", $$"""{"object":"list","data":[{{string.Join(",", ids.Select(i => $$"""{"id":"{{i}}"}"""))}}]}""");

    [Fact]
    public async Task A_server_listing_the_model_is_ready()
    {
        // Arrange
        Models("qwen3:30b", "llama3.1:8b");

        // Act
        var check = await LlmConnectionCheck.ProbeAsync(_server.Handler.CreateClient(), LlmTestServer.Base, null, "qwen3:30b", TestContext.Current.CancellationToken);

        // Assert
        (check.State, check.Models).Should().BeEquivalentTo((LlmCheckState.Ready, new[] { "llama3.1:8b", "qwen3:30b" }));
    }

    [Fact]
    public async Task A_server_without_the_model_says_it_is_missing()
    {
        // Arrange
        Models("llama3.1:8b");

        // Act
        var check = await LlmConnectionCheck.ProbeAsync(_server.Handler.CreateClient(), LlmTestServer.Base, null, "qwen3:30b", TestContext.Current.CancellationToken);

        // Assert
        check.State.Should().Be(LlmCheckState.ModelMissing);
    }

    [Fact]
    public async Task Nothing_at_the_address_is_unreachable()
    {
        // Arrange
        _server.Handler.OnAsync(LlmTestServer.Base + "/models", (_, _) => throw new HttpRequestException("Connection refused"));

        // Act
        var check = await LlmConnectionCheck.ProbeAsync(_server.Handler.CreateClient(), LlmTestServer.Base, null, "qwen3:30b", TestContext.Current.CancellationToken);

        // Assert
        check.State.Should().Be(LlmCheckState.Unreachable);
    }

    [Fact]
    public async Task A_refused_key_says_so()
    {
        // Arrange
        _server.Handler.OnGet(LlmTestServer.Base + "/models", """{"error":{"message":"Invalid API key"}}""", HttpStatusCode.Unauthorized);

        // Act
        var check = await LlmConnectionCheck.ProbeAsync(_server.Handler.CreateClient(), LlmTestServer.Base, "sk-bad", "gpt-5-mini", TestContext.Current.CancellationToken);

        // Assert
        (check.State, check.Detail).Should().Be((LlmCheckState.KeyRefused, "HTTP 401: Invalid API key"));
    }

    [Fact]
    public async Task A_model_that_calls_the_test_tool_is_ready()
    {
        // Arrange
        Models("qwen3:30b");
        _server.Call("report_ready", """{"word":"ready"}""");

        // Act
        var check = await LlmConnectionCheck.TestAsync(_server.Handler.CreateClient(), LlmTestServer.Base, null, "qwen3:30b", TestContext.Current.CancellationToken);

        // Assert
        check.State.Should().Be(LlmCheckState.Ready);
    }

    [Fact]
    public async Task A_model_that_answers_with_text_does_not_call_tools()
    {
        // Arrange
        Models("gemma:2b");
        _server.Text("ready");

        // Act
        var check = await LlmConnectionCheck.TestAsync(_server.Handler.CreateClient(), LlmTestServer.Base, null, "gemma:2b", TestContext.Current.CancellationToken);

        // Assert
        check.State.Should().Be(LlmCheckState.NoToolCalls);
    }
}
