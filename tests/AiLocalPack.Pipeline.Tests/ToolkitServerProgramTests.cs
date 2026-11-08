using AiLocalPack.Core.Background;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>How the agent starts the toolkit server for a job (task 49).</summary>
public sealed class ToolkitServerProgramTests
{
    private static OsEnvironment Env(Dictionary<string, string>? variables = null) =>
        new(OsKind.Linux, "/home/p", name => variables?.GetValueOrDefault(name));

    [Fact]
    public void The_cli_serves_through_its_mcp_command()
    {
        // Arrange
        var program = ToolkitServerProgram.Cli(new AppLaunch("/usr/bin/dotnet", ["/opt/cli.dll"]), Env());

        // Act
        var server = program.For("/w/job/game", "/w/job", "ru");

        // Assert
        new { server.Name, server.Command, server.Arguments }.Should().BeEquivalentTo(new
        {
            Name = "ailocalpack",
            Command = "/usr/bin/dotnet",
            Arguments = new[] { "/opt/cli.dll", "mcp", "--root", "/w/job/game", "--job", "/w/job", "--lang", "ru" },
        });
    }

    [Fact]
    public void The_desktop_app_serves_in_its_hidden_mode()
    {
        // Arrange
        var program = ToolkitServerProgram.DesktopApp(new AppLaunch("/Applications/ailocalpack.app/Contents/MacOS/ailocalpack", []), Env());

        // Act
        var server = program.For("/w/job/game", "/w/job", "ru");

        // Assert
        server.Arguments[0].Should().Be("--mcp-server");
    }

    [Fact]
    public void The_apps_home_and_engine_switches_are_passed_on()
    {
        // Arrange
        var env = Env(new() { ["AILOCALPACK_HOME"] = "/tmp/h", ["AILOCALPACK_TOOLKIT_ALL_ENGINES"] = "1", ["PATH"] = "/bin" });

        // Act
        var server = ToolkitServerProgram.Cli(new AppLaunch("/bin/ailocalpack", []), env).For("/w/job/game", "/w/job", "ru");

        // Assert
        server.Environment.Should().BeEquivalentTo(new Dictionary<string, string> { ["AILOCALPACK_HOME"] = "/tmp/h", ["AILOCALPACK_TOOLKIT_ALL_ENGINES"] = "1" });
    }
}
