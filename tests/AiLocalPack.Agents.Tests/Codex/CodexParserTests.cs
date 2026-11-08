using AiLocalPack.Agents.Codex;

namespace AiLocalPack.Agents.Tests.Codex;

/// <summary>Codex's <c>exec --json</c> events read into normalized events (task 42).</summary>
public sealed class CodexParserTests
{
    private static List<AgentEvent> Parse(TimeProvider? clock, params IEnumerable<string> lines)
    {
        var parser = new CodexParser("gpt-5.5-codex", clock ?? new FakeClock());
        return lines.SelectMany(parser.ParseLine).ToList();
    }

    private static List<AgentEvent> Parse(params IEnumerable<string> lines) => Parse(null, lines);

    private static List<AgentEvent> Transcript(string name) => Parse(CodexTranscripts.Lines(name));

    [Fact]
    public void The_thread_starts_the_session_with_the_requested_model()
    {
        // Act
        var events = Transcript("success");

        // Assert
        events[0].Should().Be(new AgentSessionStarted("01a1bb5f-c74b-75f2-84db-b03c527b26c6", "gpt-5.5-codex"));
    }

    [Fact]
    public void A_recorded_run_reads_as_session_messages_a_command_usage_and_done()
    {
        // Act
        var events = Transcript("success");

        // Assert
        events.Select(e => e.GetType()).Should().Equal(
            typeof(AgentSessionStarted), typeof(AgentMessage), typeof(AgentToolCall), typeof(AgentToolResult), typeof(AgentMessage), typeof(AgentUsage), typeof(AgentDone));
    }

    [Fact]
    public void A_command_is_a_tool_call_with_the_command_line()
    {
        // Act
        var call = Transcript("success").OfType<AgentToolCall>().Single();

        // Assert
        call.Should().Be(new AgentToolCall("item_1", "command", """{"command":"/bin/zsh -lc 'ls -la'"}"""));
    }

    [Fact]
    public void A_multi_line_command_stays_one_line_of_json()
    {
        // Act
        var call = Parse("""{"type":"item.started","item":{"id":"c1","type":"command_execution","command":"printf 'Привет' > b.txt\ncat b.txt","status":"in_progress"}}""")
            .OfType<AgentToolCall>().Single();

        // Assert
        call.Input.Should().Be("{\"command\":\"printf 'Привет' > b.txt\\ncat b.txt\"}");
    }

    [Fact]
    public void A_commands_result_is_its_output()
    {
        // Act
        var result = Transcript("success").OfType<AgentToolResult>().Single();

        // Assert
        (result.CallId, result.IsError, result.Output.Contains("a.txt", StringComparison.Ordinal)).Should().Be(("item_1", false, true));
    }

    [Fact]
    public void A_command_with_a_non_zero_exit_code_is_an_error_result()
    {
        // Act
        var events = Parse("""{"type":"item.completed","item":{"id":"c1","type":"command_execution","command":"touch /x","aggregated_output":"operation not permitted","exit_code":1,"status":"failed"}}""");

        // Assert
        events.OfType<AgentToolResult>().Single().IsError.Should().BeTrue();
    }

    [Fact]
    public void A_completed_item_without_a_start_reports_its_call_and_result()
    {
        // Act
        var events = Parse("""{"type":"item.completed","item":{"id":"c1","type":"command_execution","command":"ls","aggregated_output":"a.txt\n","exit_code":0,"status":"completed"}}""");

        // Assert
        events.Should().Equal(new AgentToolCall("c1", "command", """{"command":"ls"}"""), new AgentToolResult("c1", "a.txt\n", false));
    }

    [Fact]
    public void An_mcp_tool_call_is_named_like_claude_codes()
    {
        // Act
        var call = Transcript("mcp-tool-call").OfType<AgentToolCall>().Single();

        // Assert
        (call.Name, call.Input).Should().Be(("mcp__toolkit__count_strings", "{}"));
    }

    [Fact]
    public void An_mcp_tools_result_is_its_text()
    {
        // Act
        var result = Transcript("mcp-tool-call").OfType<AgentToolResult>().Single();

        // Assert
        result.Should().Be(new AgentToolResult("item_1", "28 strings", false));
    }

    [Fact]
    public void A_failed_mcp_tool_call_is_an_error_with_its_message()
    {
        // Act
        var events = Parse("""{"type":"item.completed","item":{"id":"m1","type":"mcp_tool_call","server":"toolkit","tool":"extract","arguments":{},"result":null,"error":{"message":"MCP tool call requires approval"},"status":"failed"}}""");

        // Assert
        events.OfType<AgentToolResult>().Single().Should().Be(new AgentToolResult("m1", "MCP tool call requires approval", true));
    }

    [Fact]
    public void A_file_change_names_each_changed_file()
    {
        // Act
        var result = Transcript("file-change").OfType<AgentToolResult>().Single(r => r.CallId == "item_3");

        // Assert
        result.Should().Be(new AgentToolResult("item_3", "update /work/a.txt", false));
    }

    [Fact]
    public void The_turns_usage_counts_cached_input_apart()
    {
        // Act
        var usage = Transcript("success").OfType<AgentUsage>().Single();

        // Assert
        usage.Should().Be(new AgentUsage(31291 - 27648, 56, 27648, 0));
    }

    [Fact]
    public void A_completed_turn_succeeds_with_the_last_message()
    {
        // Act
        var done = Transcript("success").OfType<AgentDone>().Single();

        // Assert
        done.Should().Be(new AgentDone(AgentOutcome.Succeeded, "- a.txt\n\ndone."));
    }

    [Fact]
    public void Reasoning_is_left_out()
    {
        // Act
        var events = Parse("""{"type":"item.completed","item":{"id":"r1","type":"reasoning","text":"thinking"}}""");

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void A_notice_item_is_a_diagnostic()
    {
        // Act
        var events = Parse("""{"type":"item.completed","item":{"id":"e1","type":"error","message":"Codex is ignoring 1 unrecognized configuration setting."}}""");

        // Assert
        events.Should().Equal(new AgentDiagnostic("Codex is ignoring 1 unrecognized configuration setting."));
    }

    [Fact]
    public void Reconnecting_retries_are_diagnostics()
    {
        // Act
        var events = Transcript("signed-out");

        // Assert
        events.OfType<AgentDiagnostic>().Count(d => d.Text.StartsWith("Reconnecting...", StringComparison.Ordinal)).Should().Be(9);
    }

    [Fact]
    public void A_refused_sign_in_is_reported_once_as_signed_out()
    {
        // Act
        var events = Transcript("signed-out");

        // Assert
        events.OfType<AgentError>().Should().ContainSingle().Which.Kind.Should().Be(AgentErrorKind.SignedOut);
    }

    [Fact]
    public void An_expired_sign_in_is_signed_out()
    {
        // Act
        var events = Parse("""{"type":"error","message":"Your access token could not be refreshed because your refresh token has expired. Please log out and sign in again."}""");

        // Assert
        events.OfType<AgentError>().Single().Kind.Should().Be(AgentErrorKind.SignedOut);
    }

    [Fact]
    public void A_failed_turn_fails_with_its_message()
    {
        // Act
        var done = Transcript("signed-out").OfType<AgentDone>().Single();

        // Assert
        (done.Outcome, done.Message!.StartsWith("unexpected status 401 Unauthorized", StringComparison.Ordinal)).Should().Be((AgentOutcome.Failed, true));
    }

    [Fact]
    public void A_usage_limit_is_reported_once_with_its_reset_date()
    {
        // Act
        var limits = Transcript("limit-reached").OfType<AgentLimitReached>().ToList();

        // Assert
        limits.Should().ContainSingle().Which.ResetsAt.Should().Be(new DateTimeOffset(2026, 11, 9, 18, 10, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_reset_time_of_today_is_read_in_the_machines_time_zone()
    {
        // Arrange
        var clock = new ZonedClock(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(3));

        // Act
        var limit = Parse(clock, """{"type":"error","message":"You’ve hit your usage limit. Try again at 6:10 PM."}""").OfType<AgentLimitReached>().Single();

        // Assert
        limit.ResetsAt.Should().Be(new DateTimeOffset(2026, 11, 8, 18, 10, 0, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void A_limit_without_a_reset_time_has_none()
    {
        // Act
        var limit = Parse("""{"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again later."}}""").OfType<AgentLimitReached>().Single();

        // Assert
        limit.ResetsAt.Should().BeNull();
    }

    [Fact]
    public void Another_fatal_error_is_reported_in_codexs_words()
    {
        // Act
        var events = Parse("""{"type":"error","message":"stream disconnected before completion"}""");

        // Assert
        events.Should().Equal(new AgentError(AgentErrorKind.Reported, "stream disconnected before completion"));
    }

    [Fact]
    public void An_unknown_event_type_is_left_out()
    {
        // Act
        var events = Parse("""{"type":"turn.started"}""", """{"type":"item.updated","item":{"id":"t","type":"todo_list","items":[]}}""");

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void A_line_that_is_not_json_is_malformed_with_its_number()
    {
        // Act
        var events = Parse("""{"type":"turn.started"}""", "thread 'main' panicked");

        // Assert
        events.Should().ContainSingle().Which.Should().Match<AgentError>(e => e.Kind == AgentErrorKind.Malformed && e.Message.StartsWith("Output line 2: not JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void A_json_line_without_a_type_is_malformed()
    {
        // Act
        var events = Parse("""{"item":{}}""");

        // Assert
        events.Should().Equal(new AgentError(AgentErrorKind.Malformed, "Output line 1: not a Codex event (no \"type\")."));
    }
}
