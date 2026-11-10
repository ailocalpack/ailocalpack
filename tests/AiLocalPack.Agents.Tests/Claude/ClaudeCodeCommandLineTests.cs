using System.Text.Json;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary>Claude Code's headless command line (task 41).</summary>
public sealed class ClaudeCodeCommandLineTests
{
    private static readonly AgentRunRequest Request = new("Translate.", "/work");

    private static readonly AgentMcpServer Toolkit = new("toolkit", "/app/ailocalpack", ["mcp", "--game", "/work"])
    {
        Environment = new Dictionary<string, string> { ["AILOCALPACK_HOME"] = "/home/p/.ailocalpack" },
    };

    private static string ValueAfter(IReadOnlyList<string> arguments, string flag) => arguments[arguments.ToList().IndexOf(flag) + 1];

    [Fact]
    public void A_run_is_headless_streaming_json_with_the_prompt_on_standard_input()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request, null, null);

        // Assert
        arguments.Take(6).Should().Equal("-p", "--output-format", "stream-json", "--verbose", "--input-format", "text");
    }

    [Fact]
    public void A_run_is_restricted_to_its_folder_and_the_players_settings_are_ignored()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request, null, null);

        // Assert
        arguments.Should().Contain(["--restricted", "--disable-slash-commands", "--strict-mcp-config"]);
    }

    [Fact]
    public void Anything_not_allowed_is_denied_without_asking()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request, null, null);

        // Assert
        (ValueAfter(arguments, "--permission-mode"), ValueAfter(arguments, "--permission-prompts")).Should().Be(("dontAsk", "none"));
    }

    [Fact]
    public void Without_allowed_tools_every_built_in_tool_is_off()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request, null, null);

        // Assert
        (ValueAfter(arguments, "--tools"), arguments.Contains("--allowedTools")).Should().Be((string.Empty, false));
    }

    [Fact]
    public void Allowed_kinds_of_tools_become_claude_codes_tools()
    {
        // Arrange
        var request = Request with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.EditFiles, AgentBuiltinTool.ReadFiles } };

        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(request, null, null);

        // Assert
        ValueAfter(arguments, "--tools").Should().Be("Read,Glob,Grep,Edit,Write");
    }

    [Fact]
    public void Running_commands_allows_bash()
    {
        // Act
        var names = ClaudeCodeCommandLine.BuiltinToolNames(new HashSet<AgentBuiltinTool> { AgentBuiltinTool.RunCommands });

        // Assert
        names.Should().Equal("Bash");
    }

    [Fact]
    public void Allowed_tools_and_every_tool_of_each_mcp_server_are_pre_approved()
    {
        // Arrange
        var request = Request with
        {
            BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles },
            McpServers = [Toolkit, new AgentMcpServer("glossary", "/app/g", [])],
        };

        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(request, "/tmp/r/mcp.json", null);

        // Assert
        ValueAfter(arguments, "--allowedTools").Should().Be("Read,Glob,Grep,mcp__toolkit,mcp__glossary");
    }

    [Fact]
    public void The_mcp_config_and_system_prompt_are_passed_as_files()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request, "/tmp/r/mcp.json", "/tmp/r/system-prompt.md");

        // Assert
        (ValueAfter(arguments, "--mcp-config"), ValueAfter(arguments, "--append-system-prompt-file")).Should().Be(("/tmp/r/mcp.json", "/tmp/r/system-prompt.md"));
    }

    [Fact]
    public void Without_a_system_prompt_none_is_appended()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request, null, null);

        // Assert
        arguments.Should().NotContain(["--append-system-prompt-file", "--mcp-config"]);
    }

    [Fact]
    public void A_model_and_a_session_to_resume_are_passed_on()
    {
        // Arrange
        var request = Request with { Model = "sonnet", ResumeSessionId = "439c3701" };

        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(request, null, null);

        // Assert
        arguments.TakeLast(4).Should().Equal("--model", "sonnet", "--resume", "439c3701");
    }

    [Fact]
    public void The_mcp_config_names_each_server_with_its_command_arguments_and_environment()
    {
        // Act
        using var config = JsonDocument.Parse(ClaudeCodeCommandLine.McpConfig([Toolkit]));

        // Assert
        var server = config.RootElement.GetProperty("mcpServers").GetProperty("toolkit");
        (server.GetProperty("type").GetString(),
            server.GetProperty("command").GetString(),
            string.Join(' ', server.GetProperty("args").EnumerateArray().Select(a => a.GetString())),
            server.GetProperty("env").GetProperty("AILOCALPACK_HOME").GetString())
            .Should().Be(("stdio", "/app/ailocalpack", "mcp --game /work", "/home/p/.ailocalpack"));
    }

    [Fact]
    public void An_mcp_server_name_claude_code_cannot_prefix_tools_with_is_refused()
    {
        // Act
        var act = () => ClaudeCodeCommandLine.McpConfig([new AgentMcpServer("tool kit", "/app/x", [])]);

        // Assert
        act.Should().Throw<ProcessStartException>().WithMessage("*\"tool kit\"*");
    }

    [Fact]
    public void An_mcp_server_name_given_twice_is_refused()
    {
        // Act
        var act = () => ClaudeCodeCommandLine.McpConfig([Toolkit, Toolkit]);

        // Assert
        act.Should().Throw<ProcessStartException>();
    }

    [Theory]
    [InlineData("2.1.319 (Claude Code)", "2.1.319")]
    [InlineData("claude 2.2.0-beta.1\n", "2.2.0-beta.1")]
    [InlineData("Claude Code", null)]
    public void The_version_is_read_from_its_output(string output, string? version)
    {
        // Act
        var parsed = ClaudeCodeCommandLine.ParseVersion(output);

        // Assert
        parsed.Should().Be(version);
    }

    [Theory]
    [InlineData("2.1.319", true)]
    [InlineData("2.1.259", true)]
    [InlineData("3.0.0-beta.1", true)]
    [InlineData("2.1.258", false)]
    [InlineData("1.0.128", false)]
    [InlineData(null, false)]
    public void Only_a_version_with_every_flag_is_supported(string? version, bool supported)
    {
        // Act
        var result = ClaudeCodeCommandLine.IsSupported(version);

        // Assert
        result.Should().Be(supported);
    }

    [Fact]
    public void A_settings_file_is_passed_with_settings()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.RunCommands } }, null, null, "/tmp/r/settings.json");

        // Assert
        (ValueAfter(arguments, "--settings"), ValueAfter(arguments, "--tools")).Should().Be(("/tmp/r/settings.json", "Bash"));
    }

    [Fact]
    public void Commands_still_run_restricted_to_the_working_folder()
    {
        // Act
        var arguments = ClaudeCodeCommandLine.Arguments(Request with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.RunCommands, AgentBuiltinTool.EditFiles } }, null, null, "/tmp/r/settings.json");

        // Assert
        arguments.Should().Contain(["--restricted", "--permission-mode", "dontAsk"]);
    }
}
