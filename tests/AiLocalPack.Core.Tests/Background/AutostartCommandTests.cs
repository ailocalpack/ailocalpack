using AiLocalPack.Core.Background;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Background;

public sealed class AutostartCommandTests
{
    private static OsEnvironment Env(OsKind os, string? appImage = null) => new(os, "/home/p", name => name == "APPIMAGE" ? appImage : null);

    /// <summary>A fully qualified path on the host running the tests (a Unix path isn't one on Windows).</summary>
    private static string Abs(string unixPath) => OperatingSystem.IsWindows() ? "C:" + unixPath : unixPath;

    [Fact]
    public void Own_executable_runs_with_the_background_argument()
    {
        // Arrange
        var env = Env(OsKind.MacOS);

        // Act
        var command = AutostartCommand.ForCurrentProcess(env, Abs("/Applications/ailocalpack.app/Contents/MacOS/AiLocalPack"), entryAssembly: null);

        // Assert
        command.Should().BeEquivalentTo(new AutostartCommand(Abs("/Applications/ailocalpack.app/Contents/MacOS/AiLocalPack"), ["--background"]));
    }

    [Fact]
    public void Dotnet_host_runs_the_app_assembly()
    {
        // Arrange
        var env = Env(OsKind.Linux);

        // Act
        var command = AutostartCommand.ForCurrentProcess(env, Abs("/usr/share/dotnet/dotnet"), Abs("/src/bin/AiLocalPack.dll"));

        // Assert
        command.Should().BeEquivalentTo(new AutostartCommand(Abs("/usr/share/dotnet/dotnet"), [Abs("/src/bin/AiLocalPack.dll"), "--background"]));
    }

    [Fact]
    public void AppImage_runs_the_image_not_its_temporary_mount()
    {
        // Arrange
        var env = Env(OsKind.Linux, Abs("/home/deck/Apps/ailocalpack.AppImage"));

        // Act
        var command = AutostartCommand.ForCurrentProcess(env, Abs("/tmp/.mount_ailoXYZ/usr/bin/AiLocalPack"), entryAssembly: null);

        // Assert
        command!.Executable.Should().Be(Abs("/home/deck/Apps/ailocalpack.AppImage"));
    }

    [Fact]
    public void Unknown_process_path_gives_no_command()
    {
        // Arrange
        var env = Env(OsKind.Windows);

        // Act
        var command = AutostartCommand.ForCurrentProcess(env, processPath: null, entryAssembly: null);

        // Assert
        command.Should().BeNull();
    }
}
