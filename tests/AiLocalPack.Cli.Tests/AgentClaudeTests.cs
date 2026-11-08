namespace AiLocalPack.Cli.Tests;

/// <summary>
/// <c>agent probe claude</c> / <c>agent run claude</c> (task 41) end to end against a stand-in <c>claude</c> shell script
/// first on <c>PATH</c> (Unix only: Windows would look for <c>claude.exe</c> / <c>claude.cmd</c>).
/// </summary>
public sealed class AgentClaudeTests : IDisposable
{
    private const string Transcript = """
        {"type":"system","subtype":"init","session_id":"s-1","model":"claude-haiku-5-5"}
        {"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Glob","input":{"pattern":"*"}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"a.txt"}]}}
        {"type":"result","subtype":"success","is_error":false,"result":"One file: a.txt.","total_cost_usd":0.25,"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static bool IsUnix => !OperatingSystem.IsWindows();

    /// <summary>A <c>claude</c> answering <c>--version</c>, <c>auth status</c> (signed in or not) and <c>-p</c> (the transcript; its arguments saved).</summary>
    private Dictionary<string, string> FakeClaude(bool signedIn = true)
    {
        _dir.File("bin/transcript.jsonl", Transcript + "\n");
        var auth = signedIn ? """{"loggedIn": true, "email": "player@example.com"}""" : """{"loggedIn": false}""";
        _dir.File("bin/claude", $"""
            #!/bin/sh
            here=$(dirname "$0")
            case "$1" in
              --version) echo "2.1.319 (Claude Code)" ;;
              auth) echo '{auth}'; {(signedIn ? "exit 0" : "exit 1")} ;;
              -p) printf '%s\n' "$@" > "$here/args.txt"; cat > "$here/prompt.txt"; cat "$here/transcript.jsonl" ;;
            esac
            """.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir.Combine("bin/claude"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new Dictionary<string, string> { ["PATH"] = _dir.Combine("bin") + ":/usr/bin:/bin" };
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Probe_prints_the_program_its_version_and_the_sign_in()
    {
        // Arrange
        var environment = FakeClaude();

        // Act
        var result = await CliRunner.RunAsync(environment, "agent", "probe", "claude");

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0,
            "Claude Code (claude)\n"
            + $"program:  {_dir.Combine("bin/claude")} (installed by you)\n"
            + "version:  2.1.319\n"
            + "sign-in:  signed in (player@example.com)\n"));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Probe_of_a_signed_out_agent_exits_one()
    {
        // Arrange
        var environment = FakeClaude(signedIn: false);

        // Act
        var result = await CliRunner.RunAsync(environment, "agent", "probe", "claude", "--json");

        // Assert
        (result.ExitCode, result.StdOut.Contains("\"auth\": \"signedOut\"", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Run_streams_claude_codes_events()
    {
        // Arrange
        var environment = FakeClaude();

        // Act
        var result = await CliRunner.RunAsync(environment, "agent", "run", "claude", "list files", "--cwd", _dir.Path);

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0,
            "session  s-1 (model claude-haiku-5-5)\n"
            + "tool     t1 Glob {\"pattern\":\"*\"}\n"
            + "result   t1 ok: a.txt\n"
            + "usage    in 10 · out 5 · cache read 0 · cache write 0 · $0.25\n"
            + "done     Succeeded (exit 0): One file: a.txt.\n"));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Run_passes_the_prompt_on_standard_input()
    {
        // Arrange
        var environment = FakeClaude();

        // Act
        await CliRunner.RunAsync(environment, "agent", "run", "claude", "list files", "--cwd", _dir.Path);

        // Assert
        File.ReadAllText(_dir.Combine("bin/prompt.txt")).Should().Be("list files");
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Run_allows_reading_by_default()
    {
        // Arrange
        var environment = FakeClaude();

        // Act
        await CliRunner.RunAsync(environment, "agent", "run", "claude", "list files", "--cwd", _dir.Path);

        // Assert
        File.ReadAllLines(_dir.Combine("bin/args.txt")).Should().ContainInOrder("--tools", "Read,Glob,Grep");
    }

    [Fact]
    public async Task Probe_of_an_unknown_agent_exits_one()
    {
        // Act
        var result = await CliRunner.RunAsync("agent", "probe", "gpt");

        // Assert
        (result.ExitCode, result.StdErr.Contains("known agents are claude", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Run_without_a_prompt_exits_one()
    {
        // Act
        var result = await CliRunner.RunAsync("agent", "run", "claude");

        // Assert
        (result.ExitCode, result.StdErr.Contains("Give the prompt", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact]
    public async Task Run_with_an_unknown_tool_kind_exits_one()
    {
        // Act
        var result = await CliRunner.RunAsync("agent", "run", "claude", "hi", "--allow", "web");

        // Assert
        (result.ExitCode, result.StdErr.Contains("--allow web", StringComparison.Ordinal)).Should().Be((1, true));
    }
}
