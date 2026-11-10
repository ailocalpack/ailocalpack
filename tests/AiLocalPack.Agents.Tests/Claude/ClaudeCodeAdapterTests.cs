using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary>Finding Claude Code, its sign-in probe and the files of a run (task 41).</summary>
public sealed class ClaudeCodeAdapterTests : IDisposable
{
    private static readonly ScriptedOutput Version = ScriptedOutput.Of("2.1.319 (Claude Code)");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static bool IsUnix => !OperatingSystem.IsWindows();

    /// <summary>A Claude Code program file (executable on Unix) in <paramref name="folder"/> under the temp folder.</summary>
    private string Program(string folder)
    {
        var relative = Path.Combine(folder, ClaudeTranscripts.ProgramName);
        _dir.File(relative, "#!/bin/sh\n");
        var path = _dir.Combine(relative);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private ClaudeCodeAdapter Adapter(IProcessRunner runner, string? path = null, string? managed = null) =>
        new(runner, ClaudeTranscripts.Environment(_dir.Combine("home"), path), managed, systemFolders: []);

    [Fact]
    public async Task Claude_code_on_path_is_found_with_its_version()
    {
        // Arrange
        var program = Program("bin");
        var adapter = Adapter(new RoutingProcessRunner(_ => Version), path: _dir.Combine("bin"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation.Should().Be(new AgentInstallation("claude", program, "2.1.319", AgentSource.Path));
    }

    [Fact]
    public async Task Claude_code_in_the_native_installers_folder_is_found_without_it_on_path()
    {
        // Arrange
        var program = Program(Path.Combine("home", ".local", "bin"));
        var adapter = Adapter(new RoutingProcessRunner(_ => Version));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation!.Executable.Should().Be(program);
    }

    [Fact]
    public async Task The_version_is_asked_with_version_flag()
    {
        // Arrange
        Program("bin");
        var runner = new RoutingProcessRunner(_ => Version);

        // Act
        await Adapter(runner, path: _dir.Combine("bin")).DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        runner.Started.Single().Arguments.Should().Equal("--version");
    }

    [Fact]
    public async Task A_program_on_path_wins_over_the_app_managed_one()
    {
        // Arrange
        var own = Program("bin");
        Program("managed");
        var adapter = Adapter(new RoutingProcessRunner(_ => Version), path: _dir.Combine("bin"), managed: _dir.Combine("managed"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation!.Executable.Should().Be(own);
    }

    [Fact]
    public async Task The_app_managed_program_is_found_when_the_player_has_none()
    {
        // Arrange
        var managed = Program("managed");
        var adapter = Adapter(new RoutingProcessRunner(_ => Version), managed: _dir.Combine("managed"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        (installation!.Executable, installation.Source).Should().Be((managed, AgentSource.Managed));
    }

    [Fact]
    public async Task A_program_that_does_not_answer_its_version_gives_way_to_the_next()
    {
        // Arrange
        var broken = Program("broken");
        var working = Program("bin");
        var runner = new RoutingProcessRunner(spec => spec.FileName == broken ? ScriptedOutput.Of() with { ExitCode = 127 } : Version);
        var adapter = Adapter(runner, path: _dir.Combine("broken") + Path.PathSeparator + _dir.Combine("bin"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation!.Executable.Should().Be(working);
    }

    [Fact]
    public async Task A_program_that_never_answers_its_version_is_found_without_one()
    {
        // Arrange
        var program = Program("bin");
        var adapter = Adapter(new RoutingProcessRunner(_ => ScriptedOutput.Of("garbage")), path: _dir.Combine("bin"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation.Should().Be(new AgentInstallation("claude", program, null, AgentSource.Path));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Execute bits are Unix only.")]
    public async Task A_file_without_an_execute_bit_is_not_claude_code()
    {
        // Arrange
        var program = Program("bin");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var adapter = Adapter(new RoutingProcessRunner(_ => Version), path: _dir.Combine("bin"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation.Should().BeNull();
    }

    [Fact]
    public async Task Nothing_is_found_when_claude_code_is_not_installed()
    {
        // Arrange
        var adapter = Adapter(new RoutingProcessRunner(_ => Version), path: _dir.Combine("bin"));

        // Act
        var installation = await adapter.DetectAsync(TestContext.Current.CancellationToken);

        // Assert
        installation.Should().BeNull();
    }

    [Fact]
    public async Task The_sign_in_is_asked_with_auth_status()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => ScriptedOutput.Of("""{"loggedIn": true}"""));
        var installation = new AgentInstallation("claude", "/bin/claude", "2.1.319", AgentSource.Path);

        // Act
        await Adapter(runner).GetAuthStatusAsync(installation, TestContext.Current.CancellationToken);

        // Assert
        runner.Started.Single().Arguments.Should().Equal("auth", "status", "--json");
    }

    [Fact]
    public async Task A_sign_in_probe_that_cannot_start_is_unknown()
    {
        // Arrange
        var runner = new ScriptedProcessRunner(ScriptedOutput.Of()) { StartError = new ProcessStartException("claude: not found") };
        var installation = new AgentInstallation("claude", "/bin/claude", "2.1.319", AgentSource.Path);

        // Act
        var status = await Adapter(runner).GetAuthStatusAsync(installation, TestContext.Current.CancellationToken);

        // Assert
        status.Should().Be(new AgentAuthStatus(AgentAuthState.Unknown, Detail: "claude: not found"));
    }

    [Fact]
    public async Task A_run_writes_its_mcp_config_and_system_prompt_for_claude_code_to_read()
    {
        // Arrange
        var seen = new Dictionary<string, string>();
        var runner = new RoutingProcessRunner(spec =>
        {
            seen["mcp"] = File.ReadAllText(spec.Arguments[spec.Arguments.ToList().IndexOf("--mcp-config") + 1]);
            seen["prompt"] = File.ReadAllText(spec.Arguments[spec.Arguments.ToList().IndexOf("--append-system-prompt-file") + 1]);
            return ClaudeTranscripts.Output("success");
        });
        var request = new AgentRunRequest("Translate.", _dir.Path)
        {
            SystemPrompt = "Keep placeholders.",
            McpServers = [new AgentMcpServer("toolkit", "/app/ailocalpack", ["mcp"])],
        };

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(new AgentInstallation("claude", "/bin/claude", null, AgentSource.Path), request, TestContext.Current.CancellationToken))
        {
        }

        // Assert
        (seen["mcp"].Contains("\"toolkit\"", StringComparison.Ordinal), seen["prompt"]).Should().Be((true, "Keep placeholders."));
    }

    [Fact]
    public async Task A_run_with_commands_reads_sandbox_settings_that_never_run_a_command_unconfined()
    {
        // Arrange
        string? settings = null;
        var runner = new RoutingProcessRunner(spec =>
        {
            settings = File.ReadAllText(spec.Arguments[spec.Arguments.ToList().IndexOf("--settings") + 1]);
            return ClaudeTranscripts.Output("success");
        });
        var request = new AgentRunRequest("Write an extractor.", _dir.Path) { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.RunCommands } };

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(new AgentInstallation("claude", "/bin/claude", null, AgentSource.Path), request, TestContext.Current.CancellationToken))
        {
        }

        // Assert
        var sandbox = System.Text.Json.JsonDocument.Parse(settings!).RootElement.GetProperty("sandbox");
        (sandbox.GetProperty("enabled").GetBoolean(), sandbox.GetProperty("failIfUnavailable").GetBoolean(), sandbox.GetProperty("allowUnsandboxedCommands").GetBoolean(),
            sandbox.GetProperty("network").GetProperty("allowedDomains").GetArrayLength())
            .Should().Be((true, true, false, 0));
    }

    [Fact]
    public async Task A_run_with_commands_has_its_temporary_folder_inside_the_working_folder()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => ClaudeTranscripts.Output("success"));
        var request = new AgentRunRequest("Write an extractor.", _dir.Path) { BuiltinTools = new HashSet<AgentBuiltinTool> { AgentBuiltinTool.RunCommands } };

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(new AgentInstallation("claude", "/bin/claude", null, AgentSource.Path), request, TestContext.Current.CancellationToken))
        {
        }

        // Assert
        var temp = Path.Combine(_dir.Path, AgentSandbox.TempFolderName);
        (runner.Started.Single().Environment["TMPDIR"], Directory.Exists(temp)).Should().Be((temp, true));
    }

    [Fact]
    public async Task A_run_without_commands_has_no_sandbox_settings()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => ClaudeTranscripts.Output("success"));

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(new AgentInstallation("claude", "/bin/claude", null, AgentSource.Path), new AgentRunRequest("Translate.", _dir.Path), TestContext.Current.CancellationToken))
        {
        }

        // Assert
        runner.Started.Single().Arguments.Should().NotContain("--settings");
    }

    [Fact]
    public async Task A_runs_files_are_deleted_once_it_ended()
    {
        // Arrange
        string? config = null;
        var runner = new RoutingProcessRunner(spec =>
        {
            config = spec.Arguments[spec.Arguments.ToList().IndexOf("--append-system-prompt-file") + 1];
            return ClaudeTranscripts.Output("success");
        });
        var request = new AgentRunRequest("Translate.", _dir.Path) { SystemPrompt = "Keep placeholders." };

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(new AgentInstallation("claude", "/bin/claude", null, AgentSource.Path), request, TestContext.Current.CancellationToken))
        {
        }

        // Assert
        Directory.Exists(Path.GetDirectoryName(config)).Should().BeFalse();
    }

    [Fact]
    public async Task A_run_starts_in_its_folder_with_the_prompt_on_standard_input()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => ClaudeTranscripts.Output("success"));
        var installation = new AgentInstallation("claude", "/bin/node", null, AgentSource.Configured) { Arguments = ["/lib/claude/cli.js"] };

        // Act
        await foreach (var _ in Adapter(runner).RunAsync(installation, new AgentRunRequest("Переведи.", _dir.Path), TestContext.Current.CancellationToken))
        {
        }

        // Assert
        var spec = runner.Started.Single();
        (spec.FileName, spec.Arguments[0], spec.Arguments[1], spec.WorkingDirectory, spec.StandardInput)
            .Should().Be(("/bin/node", "/lib/claude/cli.js", "-p", _dir.Path, "Переведи."));
    }

    [Fact]
    public async Task A_bad_mcp_server_name_ends_the_run_as_failed_to_start()
    {
        // Arrange
        var runner = new RoutingProcessRunner(_ => ClaudeTranscripts.Output("success"));
        var request = new AgentRunRequest("Translate.", _dir.Path) { McpServers = [new AgentMcpServer("tool kit", "/app/x", [])] };
        var events = new List<AgentEvent>();

        // Act
        await foreach (var e in Adapter(runner).RunAsync(new AgentInstallation("claude", "/bin/claude", null, AgentSource.Path), request, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        // Assert
        events.OfType<AgentError>().Should().ContainSingle(e => e.Kind == AgentErrorKind.StartFailed);
    }
}
