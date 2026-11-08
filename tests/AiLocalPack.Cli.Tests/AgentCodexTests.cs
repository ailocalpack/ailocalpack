namespace AiLocalPack.Cli.Tests;

/// <summary>
/// <c>agent probe codex</c> / <c>agent run codex</c> (task 42) end to end against a stand-in <c>codex</c> shell script
/// first on <c>PATH</c> (Unix only).
/// </summary>
public sealed class AgentCodexTests : IDisposable
{
    private const string Transcript = """
        {"type":"thread.started","thread_id":"t-1"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"ls","aggregated_output":"a.txt\n","exit_code":0,"status":"completed"}}
        {"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"One file: a.txt."}}
        {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":5,"reasoning_output_tokens":0}}
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static bool IsUnix => !OperatingSystem.IsWindows();

    /// <summary>A <c>codex</c> answering <c>--version</c>, <c>login status</c> (on standard error) and <c>exec</c> (the transcript; its arguments saved).</summary>
    private Dictionary<string, string> FakeCodex(bool signedIn = true)
    {
        _dir.File("bin/transcript.jsonl", Transcript + "\n");
        _dir.File("bin/codex", $"""
            #!/bin/sh
            here=$(dirname "$0")
            case "$1" in
              --version) echo "codex-cli 0.167.0" ;;
              login) {(signedIn ? "echo 'Logged in using ChatGPT' >&2; exit 0" : "echo 'Not logged in' >&2; exit 1")} ;;
              exec) printf '%s\n' "$@" > "$here/args.txt"; cat > "$here/prompt.txt"; cat "$here/transcript.jsonl" ;;
            esac
            """.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir.Combine("bin/codex"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new Dictionary<string, string> { ["PATH"] = _dir.Combine("bin") + ":/usr/bin:/bin" };
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in codex is a shell script.")]
    public async Task Probe_prints_the_program_its_version_and_the_sign_in()
    {
        // Arrange
        var environment = FakeCodex();

        // Act
        var result = await CliRunner.RunAsync(environment, "agent", "probe", "codex");

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0,
            "Codex (codex)\n"
            + $"program:  {_dir.Combine("bin/codex")} (installed by you)\n"
            + "version:  0.167.0\n"
            + "sign-in:  signed in (ChatGPT)\n"));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in codex is a shell script.")]
    public async Task Probe_of_a_signed_out_codex_exits_one()
    {
        // Arrange
        var environment = FakeCodex(signedIn: false);

        // Act
        var result = await CliRunner.RunAsync(environment, "agent", "probe", "codex");

        // Assert
        (result.ExitCode, result.StdOut.Contains("sign-in:  signed out", StringComparison.Ordinal)).Should().Be((1, true));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in codex is a shell script.")]
    public async Task Run_streams_codexs_events()
    {
        // Arrange
        var environment = FakeCodex();

        // Act
        var result = await CliRunner.RunAsync(environment, "agent", "run", "codex", "list files", "--cwd", _dir.Path);

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n")).Should().Be((0,
            "session  t-1\n"
            + "tool     item_1 command {\"command\":\"ls\"}\n"
            + "result   item_1 ok: a.txt ⏎ \n"
            + "message  One file: a.txt.\n"
            + "usage    in 40 · out 5 · cache read 60 · cache write 0\n"
            + "done     Succeeded (exit 0): One file: a.txt.\n"));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in codex is a shell script.")]
    public async Task Run_passes_the_prompt_on_standard_input()
    {
        // Arrange
        var environment = FakeCodex();

        // Act
        await CliRunner.RunAsync(environment, "agent", "run", "codex", "list files", "--cwd", _dir.Path);

        // Assert
        File.ReadAllText(_dir.Combine("bin/prompt.txt")).Should().Be("list files");
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in codex is a shell script.")]
    public async Task Run_works_in_the_given_folder_in_the_read_only_sandbox()
    {
        // Arrange
        var environment = FakeCodex();

        // Act
        await CliRunner.RunAsync(environment, "agent", "run", "codex", "list files", "--cwd", _dir.Path);

        // Assert
        File.ReadAllLines(_dir.Combine("bin/args.txt")).Should().ContainInOrder("--cd", _dir.Path, "--sandbox", "read-only");
    }
}
