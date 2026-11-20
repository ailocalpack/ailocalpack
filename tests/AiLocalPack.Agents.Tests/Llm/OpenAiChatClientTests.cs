using System.Text.Json.Nodes;
using AiLocalPack.Agents.Llm;

namespace AiLocalPack.Agents.Tests.Llm;

public sealed class OpenAiChatClientTests
{
    [Fact]
    public void Arguments_given_as_an_object_are_read_as_json_text()
    {
        // Arrange
        var json = JsonNode.Parse("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","function":{"name":"get_glossary","arguments":{"term":"HP"}}}]}}]}""")!;

        // Act
        var reply = OpenAiChatClient.Read(json);

        // Assert
        reply.ToolCalls.Single().Should().Be(new LlmToolCall("c1", "get_glossary", """{"term":"HP"}"""));
    }

    [Fact]
    public void A_tool_call_without_an_id_gets_one()
    {
        // Arrange
        var json = JsonNode.Parse("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"function":{"name":"refresh","arguments":"{}"}}]}}]}""")!;

        // Act
        var reply = OpenAiChatClient.Read(json);

        // Assert
        reply.ToolCalls.Single().Id.Should().Be("call_1");
    }

    [Fact]
    public void Cached_prompt_tokens_are_read_with_the_usage()
    {
        // Arrange
        var json = JsonNode.Parse("""{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":100,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":60}}}""")!;

        // Act
        var reply = OpenAiChatClient.Read(json);

        // Assert
        reply.Usage.Should().Be(new LlmUsage(100, 7, 60));
    }

    [Fact]
    public void An_error_object_in_a_successful_answer_is_a_server_error()
    {
        // Arrange
        var json = JsonNode.Parse("""{"error":{"message":"Upstream timed out"}}""")!;

        // Act
        var act = () => OpenAiChatClient.Read(json);

        // Assert
        act.Should().Throw<LlmRequestException>().Which.Kind.Should().Be(LlmFailureKind.ServerError);
    }
}
