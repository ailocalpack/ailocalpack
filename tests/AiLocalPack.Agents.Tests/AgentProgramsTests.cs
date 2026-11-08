using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Tests;

/// <summary>Starting an agent's program the same way for every agent (tasks 41, 42).</summary>
public sealed class AgentProgramsTests
{
    private static OsEnvironment Machine(OsKind os, string? path) => new(os, os == OsKind.Windows ? @"C:\Users\P" : "/home/p", name => name == "PATH" ? path : null);

    [Fact]
    public void A_program_runs_directly_with_its_argument_list()
    {
        // Act
        var spec = AgentPrograms.Process("/home/p/.local/bin/claude", ["-p", "a b"], Machine(OsKind.Linux, "/usr/bin"));

        // Assert
        (spec.FileName, string.Join('|', spec.Arguments), spec.RawArguments).Should().Be(("/home/p/.local/bin/claude", "-p|a b", (string?)null));
    }

    [Fact]
    public void The_programs_folder_goes_first_on_its_path_so_npms_node_is_found()
    {
        // Act
        var spec = AgentPrograms.Process("/opt/homebrew/bin/codex", ["--version"], Machine(OsKind.MacOS, "/usr/bin:/bin"));

        // Assert
        spec.Environment["PATH"].Should().Be("/opt/homebrew/bin:/usr/bin:/bin");
    }

    [Fact]
    public void Without_an_inherited_path_the_programs_folder_is_the_path()
    {
        // Act
        var spec = AgentPrograms.Process("/opt/homebrew/bin/codex", [], Machine(OsKind.MacOS, null));

        // Assert
        spec.Environment["PATH"].Should().Be("/opt/homebrew/bin");
    }

    [Fact]
    public void On_windows_the_path_is_joined_with_semicolons()
    {
        // Act
        var spec = AgentPrograms.Process(@"C:\Users\P\AppData\Roaming\npm\codex.cmd", [], Machine(OsKind.Windows, @"C:\Windows"));

        // Assert
        spec.Environment["PATH"].Should().Be(@"C:\Users\P\AppData\Roaming\npm;C:\Windows");
    }

    [Fact]
    public void The_templates_environment_is_kept()
    {
        // Arrange
        var template = new ProcessSpec("x", []) { Environment = new Dictionary<string, string?> { ["CODEX_HOME"] = "/home/p/.codex" } };

        // Act
        var spec = AgentPrograms.Process("/usr/bin/codex", [], Machine(OsKind.Linux, "/bin"), template);

        // Assert
        spec.Environment["CODEX_HOME"].Should().Be("/home/p/.codex");
    }

    [Fact]
    public void A_windows_exe_runs_directly_with_its_argument_list()
    {
        // Act
        var spec = AgentPrograms.Process(@"C:\Users\P\.local\bin\claude.exe", ["--tools", ""], Machine(OsKind.Windows, null));

        // Assert
        (spec.FileName, spec.RawArguments).Should().Be((@"C:\Users\P\.local\bin\claude.exe", (string?)null));
    }

    [Fact]
    public void A_windows_batch_file_runs_through_cmd_with_its_own_escaping()
    {
        // Act
        var spec = AgentPrograms.Process(@"C:\Users\P\AppData\Roaming\npm\claude.cmd", ["--tools", ""], Machine(OsKind.Windows, null));

        // Assert
        spec.RawArguments.Should().Be(@"/d /s /c ""C:\Users\P\AppData\Roaming\npm\claude.cmd ^^^""--tools^^^"" ^^^""^^^""""");
    }

    [Fact]
    public void A_batch_file_keeps_the_runs_folder_and_standard_input()
    {
        // Arrange
        var template = new ProcessSpec("x", []) { WorkingDirectory = @"C:\work", StandardInput = "Translate." };

        // Act
        var spec = AgentPrograms.Process(@"C:\npm\claude.cmd", ["-p"], Machine(OsKind.Windows, null), template);

        // Assert
        (spec.WorkingDirectory, spec.StandardInput, spec.Arguments.Count).Should().Be((@"C:\work", "Translate.", 0));
    }

    [Theory]
    [InlineData("codex-cli 0.167.0", "0.167.0")]
    [InlineData("2.1.319 (Claude Code)", "2.1.319")]
    [InlineData("no version", null)]
    public void The_version_is_the_first_semantic_version_in_the_output(string output, string? version)
    {
        // Act
        var parsed = AgentPrograms.ParseVersion(output);

        // Assert
        parsed.Should().Be(version);
    }

    [Fact]
    public void Codex_is_looked_for_on_path_then_in_the_installers_folders()
    {
        // Arrange
        var layout = new AgentProgramLayout("codex", [".local/bin"], []);

        // Act
        var candidates = AgentPrograms.Candidates(layout, Machine(OsKind.Linux, "/usr/bin"), "/home/p/.local/share/ailocalpack/agents/codex", []);

        // Assert
        candidates.Select(c => c.Path).Should().Equal(
            "/usr/bin/codex",
            "/home/p/.local/bin/codex",
            "/home/p/.npm-global/bin/codex",
            "/home/p/.local/share/ailocalpack/agents/codex/codex");
    }
}
