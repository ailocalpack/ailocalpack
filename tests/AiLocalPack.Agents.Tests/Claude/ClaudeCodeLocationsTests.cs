using AiLocalPack.Agents.Claude;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests.Claude;

/// <summary>The places Claude Code is looked for, in order (task 41).</summary>
public sealed class ClaudeCodeLocationsTests
{
    private static OsEnvironment Windows(string path) => new(
        OsKind.Windows,
        @"C:\Users\P",
        name => name switch { "PATH" => path, "APPDATA" => @"C:\Users\P\AppData\Roaming", _ => null });

    private static OsEnvironment Mac(string path) => new(OsKind.MacOS, "/Users/p", name => name == "PATH" ? path : null);

    [Fact]
    public void On_unix_path_comes_first_then_the_installers_folders_then_the_managed_one()
    {
        // Act
        var candidates = ClaudeCodeLocations.Candidates(Mac("/usr/bin:/opt/tools"), "/Users/p/Library/Application Support/ailocalpack/agents/claude");

        // Assert
        candidates.Select(c => c.Path).Should().Equal(
            "/usr/bin/claude",
            "/opt/tools/claude",
            "/Users/p/.local/bin/claude",
            "/Users/p/.claude/local/claude",
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude",
            "/Users/p/.npm-global/bin/claude",
            "/Users/p/Library/Application Support/ailocalpack/agents/claude/claude");
    }

    [Fact]
    public void On_windows_the_exe_comes_before_npms_batch_file()
    {
        // Act
        var candidates = ClaudeCodeLocations.Candidates(Windows(@"C:\Tools"), null);

        // Assert
        candidates.Select(c => c.Path).Should().Equal(
            @"C:\Tools\claude.exe",
            @"C:\Tools\claude.cmd",
            @"C:\Users\P\.local\bin\claude.exe",
            @"C:\Users\P\.local\bin\claude.cmd",
            @"C:\Users\P\AppData\Roaming\npm\claude.exe",
            @"C:\Users\P\AppData\Roaming\npm\claude.cmd");
    }

    [Fact]
    public void Quoted_windows_path_entries_are_unquoted()
    {
        // Act
        var candidates = ClaudeCodeLocations.Candidates(Windows(@"""C:\My Tools"""), null);

        // Assert
        candidates[0].Path.Should().Be(@"C:\My Tools\claude.exe");
    }

    [Fact]
    public void Relative_path_entries_are_ignored()
    {
        // Act
        var candidates = ClaudeCodeLocations.Candidates(Mac(".:bin"), null);

        // Assert
        candidates[0].Path.Should().Be("/Users/p/.local/bin/claude");
    }

    [Fact]
    public void A_folder_listed_twice_is_tried_once()
    {
        // Act
        var candidates = ClaudeCodeLocations.Candidates(Mac("/usr/local/bin:/usr/local/bin/"), null);

        // Assert
        candidates.Count(c => c.Path == "/usr/local/bin/claude").Should().Be(1);
    }

    [Fact]
    public void The_managed_folder_is_marked_as_managed()
    {
        // Act
        var candidates = ClaudeCodeLocations.Candidates(Mac(string.Empty), "/opt/managed");

        // Assert
        candidates[^1].Should().Be(("/opt/managed/claude", AgentSource.Managed));
    }
}
