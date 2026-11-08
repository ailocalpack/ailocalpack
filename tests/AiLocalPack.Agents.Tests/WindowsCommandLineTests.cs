using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Agents.Tests;

/// <summary>Quoting for the C runtime and escaping for cmd.exe, for npm's batch-file agents on Windows (task 41).</summary>
public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir\", @"""C:\dir\\""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    [InlineData(@"a\b", @"""a\b""")]
    public void An_argument_is_quoted_for_the_c_runtime(string argument, string quoted)
    {
        // Act
        var result = WindowsCommandLine.Quote(argument);

        // Assert
        result.Should().Be(quoted);
    }

    [Fact]
    public void Every_cmd_metacharacter_of_an_argument_is_escaped_twice()
    {
        // Act
        var spec = WindowsCommandLine.ForBatchFile(@"C:\npm\claude.cmd", ["a&b|c%PATH%"], new ProcessSpec("x", []), "cmd.exe");

        // Assert
        spec.RawArguments.Should().Be(@"/d /s /c ""C:\npm\claude.cmd ^^^""a^^^&b^^^|c^^^%PATH^^^%^^^""""");
    }

    [Fact]
    public void Spaces_in_the_batch_files_path_are_escaped_once()
    {
        // Act
        var spec = WindowsCommandLine.ForBatchFile(@"C:\Program Files\npm\claude.cmd", [], new ProcessSpec("x", []), "cmd.exe");

        // Assert
        spec.RawArguments.Should().Be(@"/d /s /c ""C:\Program^ Files\npm\claude.cmd""");
    }

    [Fact]
    public void The_batch_file_runs_through_the_given_command_interpreter()
    {
        // Act
        var spec = WindowsCommandLine.ForBatchFile(@"C:\npm\claude.cmd", ["-p"], new ProcessSpec("x", ["old"]), @"C:\Windows\system32\cmd.exe");

        // Assert
        (spec.FileName, spec.Arguments.Count).Should().Be((@"C:\Windows\system32\cmd.exe", 0));
    }

    [Theory]
    [InlineData("claude.cmd", true)]
    [InlineData(@"C:\npm\CLAUDE.BAT", true)]
    [InlineData("claude.exe", false)]
    [InlineData("claude", false)]
    public void Batch_files_are_known_by_their_extension(string executable, bool batch)
    {
        // Act
        var result = WindowsCommandLine.IsBatchFile(executable);

        // Assert
        result.Should().Be(batch);
    }
}
