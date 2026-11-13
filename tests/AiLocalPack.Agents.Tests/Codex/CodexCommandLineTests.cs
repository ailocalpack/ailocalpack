using AiLocalPack.Agents.Codex;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests.Codex;

/// <summary>Codex's headless command line (task 42).</summary>
public sealed class CodexCommandLineTests
{
    private static readonly AgentRunRequest Request = new("Translate.", "/work");

    private static IReadOnlyList<string> Overrides(IReadOnlyList<string> arguments) =>
        arguments.Select((a, i) => (a, i)).Where(x => x.a == "-c").Select(x => arguments[x.i + 1]).ToList();

    private static string ValueAfter(IReadOnlyList<string> arguments, string flag) => arguments[arguments.ToList().IndexOf(flag) + 1];

    [Fact]
    public void A_run_is_exec_with_json_events_and_the_prompt_on_standard_input()
    {
        // Act
        var arguments = CodexCommandLine.Arguments(Request);

        // Assert
        (arguments[0], arguments[1], arguments[^1]).Should().Be(("exec", "--json", "-"));
    }

    [Fact]
    public void The_players_config_and_rules_are_ignored()
    {
        // Act
        var arguments = CodexCommandLine.Arguments(Request);

        // Assert
        arguments.Should().Contain(["--ignore-user-config", "--ignore-rules", "--skip-git-repo-check"]);
    }

    [Fact]
    public void The_agent_works_in_the_runs_folder()
    {
        // Act
        var arguments = CodexCommandLine.Arguments(Request);

        // Assert
        ValueAfter(arguments, "--cd").Should().Be("/work");
    }

    [Fact]
    public void Codex_never_asks_and_never_searches_the_web()
    {
        // Act
        var overrides = Overrides(CodexCommandLine.Arguments(Request));

        // Assert
        overrides.Should().Contain(["approval_policy=\"never\"", "web_search=\"disabled\""]);
    }

    [Fact]
    public void Web_search_turns_codexs_live_search_on()
    {
        // Arrange
        var request = Request with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.WebSearch } };

        // Act
        var overrides = Overrides(CodexCommandLine.Arguments(request));

        // Assert
        overrides.Should().Contain("web_search=\"live\"").And.NotContain("web_search=\"disabled\"");
    }

    [Fact]
    public void The_effort_is_codexs_reasoning_effort()
    {
        // Arrange
        var request = Request with { Effort = AgentEffort.Low };

        // Act
        var overrides = Overrides(CodexCommandLine.Arguments(request));

        // Assert
        overrides.Should().Contain("model_reasoning_effort=\"low\"");
    }

    [Fact]
    public void Features_acting_outside_the_sandbox_are_off()
    {
        // Act
        var overrides = Overrides(CodexCommandLine.Arguments(Request));

        // Assert
        overrides.Should().Contain(["features.apps=false", "features.plugins=false", "features.hooks=false", "features.computer_use=false"]);
    }

    [Fact]
    public void Without_tools_the_sandbox_is_read_only_and_the_shell_off()
    {
        // Act
        var arguments = CodexCommandLine.Arguments(Request);

        // Assert
        (ValueAfter(arguments, "--sandbox"), Overrides(arguments).Contains("features.shell_tool=false")).Should().Be(("read-only", true));
    }

    [Fact]
    public void Reading_keeps_the_shell_in_the_read_only_sandbox()
    {
        // Arrange
        var request = Request with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles } };

        // Act
        var arguments = CodexCommandLine.Arguments(request);

        // Assert
        (ValueAfter(arguments, "--sandbox"), Overrides(arguments).Contains("features.shell_tool=false")).Should().Be(("read-only", false));
    }

    [Fact]
    public void Editing_writes_only_into_the_runs_folder_without_network()
    {
        // Arrange
        var request = Request with { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.EditFiles } };

        // Act
        var arguments = CodexCommandLine.Arguments(request);

        // Assert
        (ValueAfter(arguments, "--sandbox"), Overrides(arguments).Where(o => o.StartsWith("sandbox_workspace_write.", StringComparison.Ordinal)))
            .Should().BeEquivalentTo(("workspace-write", new[]
            {
                "sandbox_workspace_write.network_access=false",
                "sandbox_workspace_write.exclude_slash_tmp=true",
                "sandbox_workspace_write.exclude_tmpdir_env_var=true",
            }));
    }

    [Fact]
    public void An_mcp_server_is_configured_required_with_its_tools_approved()
    {
        // Arrange
        var request = Request with
        {
            McpServers =
            [
                new AgentMcpServer("toolkit", @"C:\Program Files\ailocalpack\ailocalpack.exe", ["mcp", "--game", "/work"])
                {
                    Environment = new Dictionary<string, string> { ["AILOCALPACK_HOME"] = "/home/p", ["odd.key"] = "x" },
                },
            ],
        };

        // Act
        var overrides = Overrides(CodexCommandLine.Arguments(request)).Where(o => o.StartsWith("mcp_servers.", StringComparison.Ordinal));

        // Assert
        overrides.Should().Equal(
            @"mcp_servers.toolkit.command=""C:\\Program Files\\ailocalpack\\ailocalpack.exe""",
            """mcp_servers.toolkit.args=["mcp", "--game", "/work"]""",
            """mcp_servers.toolkit.env={AILOCALPACK_HOME = "/home/p", "odd.key" = "x"}""",
            "mcp_servers.toolkit.default_tools_approval_mode=\"approve\"",
            "mcp_servers.toolkit.required=true");
    }

    [Fact]
    public void An_mcp_server_name_that_is_not_a_bare_key_is_refused()
    {
        // Arrange
        var request = Request with { McpServers = [new AgentMcpServer("tool.kit", "/x", [])] };

        // Act
        var act = () => CodexCommandLine.Arguments(request);

        // Assert
        act.Should().Throw<ProcessStartException>();
    }

    [Fact]
    public void The_system_prompt_becomes_developer_instructions()
    {
        // Arrange
        var request = Request with { SystemPrompt = "Keep \"{name}\" as is.\nAlways." };

        // Act
        var overrides = Overrides(CodexCommandLine.Arguments(request));

        // Assert
        overrides.Should().Contain("developer_instructions=\"Keep \\\"{name}\\\" as is.\\nAlways.\"");
    }

    [Fact]
    public void A_model_is_passed_on()
    {
        // Act
        var arguments = CodexCommandLine.Arguments(Request with { Model = "gpt-5.5-codex" });

        // Assert
        ValueAfter(arguments, "--model").Should().Be("gpt-5.5-codex");
    }

    [Fact]
    public void A_session_is_resumed_with_the_resume_subcommand_last()
    {
        // Act
        var arguments = CodexCommandLine.Arguments(Request with { ResumeSessionId = "01a1bb63" });

        // Assert
        arguments.TakeLast(3).Should().Equal("resume", "01a1bb63", "-");
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
    [InlineData("tab\tend\u0001", "\"tab\\tend\\u0001\"")]
    [InlineData("Привет", "\"Привет\"")]
    public void Text_is_written_as_a_toml_basic_string(string text, string toml)
    {
        // Act
        var result = CodexCommandLine.Toml(text);

        // Assert
        result.Should().Be(toml);
    }
}
