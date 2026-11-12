namespace AiLocalPack.Cli.Tests;

/// <summary>
/// <c>agent install / uninstall / login / logout</c> (task 43) end to end against stand-in <c>claude</c> shell scripts
/// (Unix only), with a throwaway home so the player's own installs aren't found. Real downloads are checked by hand.
/// </summary>
public sealed class AgentInstallTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static bool IsUnix => !OperatingSystem.IsWindows();

    /// <summary>
    /// A <c>claude</c> in <paramref name="folder"/>: <c>auth login</c> marks it signed in (after a moment, as a browser
    /// would) and waits, <c>auth logout</c> signs out, <c>auth status</c> reads the mark.
    /// </summary>
    private string FakeClaude(string folder)
    {
        _dir.File($"{folder}/claude", $$"""
            #!/bin/sh
            mark="{{_dir.Combine("signed-in")}}"
            case "$1 $2" in
              "--version ") echo "2.1.319 (Claude Code)" ;;
              "auth login") echo "Opening browser to sign in…"; (sleep 1; touch "$mark") & sleep 30 ;;
              "auth logout") rm -f "$mark"; echo "Successfully logged out" ;;
              "auth status") if [ -f "$mark" ]; then echo '{"loggedIn": true, "email": "player@example.com"}'; else echo '{"loggedIn": false}'; exit 1; fi ;;
            esac
            """.ReplaceLineEndings("\n"));
        var path = _dir.Combine($"{folder}/claude");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private Dictionary<string, string> Machine(string? path = null) => new()
    {
        ["HOME"] = _dir.Combine("home"),
        ["PATH"] = (path is null ? string.Empty : path + ":") + "/usr/bin:/bin",
        ["AILOCALPACK_HOME"] = _dir.Combine("app"),
    };

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Login_waits_for_the_browser_sign_in()
    {
        // Arrange
        FakeClaude("bin");

        // Act
        var result = await CliRunner.RunAsync(Machine(_dir.Combine("bin")), "agent", "login", "claude");

        // Assert
        (result.ExitCode, result.StdOut.ReplaceLineEndings("\n").EndsWith("  Opening browser to sign in…\nSigned in (player@example.com).\n", StringComparison.Ordinal)).Should().Be((0, true));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Login_when_signed_in_starts_nothing()
    {
        // Arrange
        FakeClaude("bin");
        _dir.File("signed-in", string.Empty);

        // Act
        var result = await CliRunner.RunAsync(Machine(_dir.Combine("bin")), "agent", "login", "claude");

        // Assert
        (result.ExitCode, result.StdOut.Contains("Already signed in (player@example.com)", StringComparison.Ordinal)).Should().Be((0, true));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Logout_signs_out()
    {
        // Arrange
        FakeClaude("bin");
        _dir.File("signed-in", string.Empty);

        // Act
        var result = await CliRunner.RunAsync(Machine(_dir.Combine("bin")), "agent", "logout", "claude");

        // Assert
        (result.ExitCode, result.StdOut.Trim(), File.Exists(_dir.Combine("signed-in"))).Should().Be((0, "Claude Code: signed out.", false));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Install_leaves_the_players_own_claude_code_in_charge()
    {
        // Arrange
        var own = FakeClaude("bin");

        // Act
        var result = await CliRunner.RunAsync(Machine(_dir.Combine("bin")), "agent", "install", "claude");

        // Assert
        (result.ExitCode, result.StdOut.Trim()).Should().Be((0,
            $"Claude Code 2.1.319 is already installed by you at {own}; the app uses that one. Pass --force to install the app's own copy anyway."));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "The stand-in claude is a shell script.")]
    public async Task Probe_finds_the_apps_own_copy_when_the_player_has_none()
    {
        // Arrange
        var program = FakeClaude("app/data/agents/claude/2.1.319");
        _dir.File("app/data/agents/claude/installed.json", """{"schemaVersion": 1, "version": "2.1.319", "executable": "claude", "installedAt": "2026-11-08T12:00:00Z"}""");

        // Act
        var result = await CliRunner.RunAsync(Machine(), "agent", "probe", "claude");

        // Assert
        result.StdOut.Should().Contain($"program:  {program} (installed by the app)");
    }

    [Fact]
    public async Task Uninstall_without_the_apps_copy_says_so()
    {
        // Act
        var result = await CliRunner.RunAsync(Machine(), "agent", "uninstall", "codex");

        // Assert
        (result.ExitCode, result.StdOut.Trim()).Should().Be((0, "The app has no copy of codex installed."));
    }

    [Fact]
    public async Task Installing_an_unknown_agent_exits_one()
    {
        // Act
        var result = await CliRunner.RunAsync(Machine(), "agent", "install", "gemini");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "Unknown agent \"gemini\": known agents are claude, codex."));
    }
}
