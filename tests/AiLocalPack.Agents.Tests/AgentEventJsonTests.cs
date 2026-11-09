namespace AiLocalPack.Agents.Tests;

/// <summary><see cref="AgentEventJson"/>: the fake agent's recordings and <c>agent run --json</c> (task 40).</summary>
public sealed class AgentEventJsonTests
{
    public static TheoryData<AgentEvent> Events => new(new AgentEvent[]
    {
        new AgentSessionStarted("s1", "fake-1"),
        new AgentSessionStarted(null, null),
        new AgentMessage("Привет, \"мир\"\n"),
        new AgentToolCall("t1", "mcp__toolkit__extract", """{"path":".","depth":2}"""),
        new AgentToolResult("t1", "28 strings", IsError: true),
        new AgentUsage(120, 40, 7, 3, 0.0123m),
        new AgentUsage(1, 2),
        new AgentLimitReached(new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero), "5-hour limit reached"),
        new AgentLimitReached(null, "limit"),
        new AgentLimitStatus(0.45, "five_hour", new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero)),
        new AgentLimitStatus(0.5, null, null),
        new AgentError(AgentErrorKind.SignedOut, "Please run /login"),
        new AgentDiagnostic("warning"),
        new AgentDone(AgentOutcome.LimitReached, "partial", "limit", 1),
        new AgentDone(AgentOutcome.Cancelled),
    });

    [Theory]
    [MemberData(nameof(Events))]
    public void An_event_reads_back_as_written(AgentEvent e)
    {
        // Arrange
        var line = AgentEventJson.Write(e);

        // Act
        var read = AgentEventJson.Read(line, out _);

        // Assert
        read.Should().Be(e);
    }

    [Fact]
    public void An_event_is_written_on_one_line()
    {
        // Act
        var line = AgentEventJson.Write(new AgentMessage("a\nb"));

        // Assert
        line.Should().Be("""{"type":"message","text":"a\nb"}""");
    }

    [Fact]
    public void A_tool_call_input_is_written_as_json_not_as_a_string()
    {
        // Act
        var line = AgentEventJson.Write(new AgentToolCall("t1", "x", """{"a":1}"""));

        // Assert
        line.Should().Be("""{"type":"toolCall","id":"t1","name":"x","input":{"a":1}}""");
    }

    [Fact]
    public void A_tool_call_without_input_reads_as_an_empty_object()
    {
        // Act
        var read = AgentEventJson.Read("""{"type":"toolCall","id":"t1","name":"x"}""", out _);

        // Assert
        read.Should().Be(new AgentToolCall("t1", "x", "{}"));
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        // Act
        var read = AgentEventJson.Read("""{"type":"message","text":"hi","extra":[1,2]}""", out _);

        // Assert
        read.Should().Be(new AgentMessage("hi"));
    }

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("[1]", "not a JSON object")]
    [InlineData("""{"text":"hi"}""", "no \"type\"")]
    [InlineData("""{"type":"poem"}""", "unknown type \"poem\"")]
    [InlineData("""{"type":"message"}""", "no \"text\"")]
    [InlineData("""{"type":"message","text":5}""", "\"text\" isn't a string")]
    [InlineData("""{"type":"usage","inputTokens":-1}""", "\"inputTokens\" is negative")]
    [InlineData("""{"type":"done","outcome":"maybe"}""", "unknown outcome \"maybe\"")]
    [InlineData("""{"type":"done","outcome":"1"}""", "unknown outcome \"1\"")]
    [InlineData("""{"type":"error","kind":"oops","message":"x"}""", "unknown kind \"oops\"")]
    public void A_line_that_is_not_an_event_is_refused_with_the_reason(string line, string reason)
    {
        // Act
        var read = AgentEventJson.Read(line, out var error);

        // Assert
        (read, error).Should().Be(((AgentEvent?)null, reason));
    }
}
