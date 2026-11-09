using AiLocalPack.Agents.Claude;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary>Claude Code's stream-json read into normalized events (task 41).</summary>
public sealed class ClaudeCodeParserTests
{
    private static readonly DateTimeOffset ResetTime = DateTimeOffset.FromUnixTimeSeconds(1794160800);

    private static List<AgentEvent> Parse(params IEnumerable<string> lines)
    {
        var parser = new ClaudeCodeParser();
        return lines.SelectMany(parser.ParseLine).ToList();
    }

    private static List<AgentEvent> Transcript(string name) => Parse(ClaudeTranscripts.Lines(name));

    [Fact]
    public void The_init_message_starts_the_session_with_its_id_and_model()
    {
        // Act
        var events = Transcript("success");

        // Assert
        events[0].Should().Be(new AgentSessionStarted("439c3701-76d6-4d93-ba43-b7d66a9df324", "claude-haiku-5-5"));
    }

    [Fact]
    public void A_recorded_run_reads_as_session_tool_call_limit_status_result_message_usage_and_done()
    {
        // Act
        var events = Transcript("success");

        // Assert
        events.Select(e => e.GetType()).Should().Equal(
            typeof(AgentSessionStarted), typeof(AgentToolCall), typeof(AgentLimitStatus), typeof(AgentToolResult), typeof(AgentMessage), typeof(AgentUsage), typeof(AgentDone));
    }

    [Fact]
    public void A_tool_call_keeps_its_input_as_json_text()
    {
        // Act
        var call = Transcript("success").OfType<AgentToolCall>().Single();

        // Assert
        (call.Id, call.Name, call.Input).Should().Be(("toolu_01Y6JxfTStsB9WhLTCrfeqK8", "Glob", """{"pattern":"*","path":"/work"}"""));
    }

    [Fact]
    public void A_tool_result_given_as_text_blocks_is_their_text()
    {
        // Act
        var result = Transcript("mcp-tool-call").OfType<AgentToolResult>().Single();

        // Assert
        result.Should().Be(new AgentToolResult("toolu_017PoQKtcTd1FXXtfq2y41aP", "28 strings", false));
    }

    [Fact]
    public void A_refused_tool_call_is_an_error_result()
    {
        // Act
        var result = Transcript("denied-outside-folder").OfType<AgentToolResult>().Single();

        // Assert
        (result.IsError, result.Output).Should().Be((true, "/etc/hosts is outside /work; --restricted confines the file tools to the working directory."));
    }

    [Fact]
    public void The_result_reports_the_whole_runs_usage_and_cost()
    {
        // Act
        var usage = Transcript("success").OfType<AgentUsage>().Single();

        // Assert
        usage.Should().Be(new AgentUsage(4, 158, 8804, 2528, 0.00067304m));
    }

    [Fact]
    public void A_successful_result_is_the_agents_verdict_with_its_text()
    {
        // Act
        var done = Transcript("success").OfType<AgentDone>().Single();

        // Assert
        done.Should().Be(new AgentDone(AgentOutcome.Succeeded, "The directory contains one file: `a.txt`.\n\nDone."));
    }

    [Fact]
    public void Thinking_blocks_are_left_out()
    {
        // Act
        var events = Parse("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"}]}}""");

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void A_signed_out_agent_is_a_signed_out_error_with_its_words()
    {
        // Act
        var events = Transcript("signed-out");

        // Assert
        events.OfType<AgentError>().Should().Equal(new AgentError(AgentErrorKind.SignedOut, "Not logged in · Please run /login"));
    }

    [Fact]
    public void A_signed_out_agents_result_is_a_failure()
    {
        // Act
        var done = Transcript("signed-out").OfType<AgentDone>().Single();

        // Assert
        done.Outcome.Should().Be(AgentOutcome.Failed);
    }

    [Fact]
    public void A_rejected_rate_limit_event_is_the_limit_with_its_reset_time()
    {
        // Act
        var limit = Transcript("limit-reached").OfType<AgentLimitReached>().Single();

        // Assert
        limit.Should().Be(new AgentLimitReached(ResetTime, "Claude usage limit reached (five-hour)."));
    }

    [Fact]
    public void An_allowed_rate_limit_event_without_utilization_is_left_out()
    {
        // Act
        var events = Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1794160800}}""");

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void An_allowed_rate_limit_event_is_the_used_share_of_its_window()
    {
        // Act
        var status = Transcript("success").OfType<AgentLimitStatus>().Single();

        // Assert
        status.Should().Be(new AgentLimitStatus(0.39, "five_hour", DateTimeOffset.FromUnixTimeSeconds(1794159000)));
    }

    [Fact]
    public void An_allowed_rate_limit_event_without_windows_uses_its_own_utilization()
    {
        // Act
        var events = Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","utilization":0.7,"resetsAt":1794160800}}""");

        // Assert
        events.Should().Equal(new AgentLimitStatus(0.7, null, ResetTime));
    }

    [Fact]
    public void A_rate_limit_error_without_an_event_is_the_limit_in_the_agents_words()
    {
        // Act
        var events = Parse("""{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"You've hit your limit · resets 6pm"}]}}""");

        // Assert
        events.Should().Equal(new AgentLimitReached(null, "You've hit your limit · resets 6pm"));
    }

    [Fact]
    public void An_older_limit_text_names_its_reset_time()
    {
        // Act
        var events = Parse("""{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"Claude AI usage limit reached|1794160800"}]}}""");

        // Assert
        events.Should().Equal(new AgentLimitReached(ResetTime, "Claude AI usage limit reached"));
    }

    [Fact]
    public void A_used_up_subscription_says_credits_are_needed()
    {
        // Act
        var limit = Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","errorCode":"credits_required"}}""").OfType<AgentLimitReached>().Single();

        // Assert
        limit.Message.Should().Contain("credits");
    }

    [Fact]
    public void Another_api_error_is_reported_in_the_agents_words()
    {
        // Act
        var events = Parse("""{"type":"assistant","error":"billing_error","message":{"content":[{"type":"text","text":"Credit balance is too low"}]}}""");

        // Assert
        events.Should().Equal(new AgentError(AgentErrorKind.Reported, "Credit balance is too low"));
    }

    [Fact]
    public void An_error_result_fails_with_its_errors()
    {
        // Act
        var events = Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["MCP server toolkit failed","aborted"]}""");

        // Assert
        events.Should().Equal(new AgentDone(AgentOutcome.Failed, Message: "MCP server toolkit failed\naborted"));
    }

    [Fact]
    public void An_unknown_message_type_is_left_out()
    {
        // Act
        var events = Parse("""{"type":"prompt_suggestion","suggestion":"next"}""");

        // Assert
        events.Should().BeEmpty();
    }

    [Fact]
    public void A_line_that_is_not_json_is_malformed_with_its_number()
    {
        // Act
        var events = Parse(ClaudeTranscripts.Lines("success")[0], "Unexpected token");

        // Assert
        events.OfType<AgentError>().Should().ContainSingle()
            .Which.Should().Match<AgentError>(e => e.Kind == AgentErrorKind.Malformed && e.Message.StartsWith("Output line 2: not JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void A_json_line_without_a_type_is_malformed()
    {
        // Act
        var events = Parse("""{"message":"hi"}""");

        // Assert
        events.Should().Equal(new AgentError(AgentErrorKind.Malformed, "Output line 1: not a Claude Code message (no \"type\")."));
    }
}
