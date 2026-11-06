using System.Text;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Launch;

public sealed class SystemProcessListTests
{
    [Fact]
    public void Snapshot_lists_the_running_test_process_with_its_path()
    {
        // Arrange
        var list = new SystemProcessList();

        // Act
        var processes = list.Snapshot();

        // Assert
        processes.Should().Contain(p => p.Id == Environment.ProcessId && PathNames.Same(PathNames.RealPath(p.Path), PathNames.RealPath(Environment.ProcessPath!)));
    }

    [Fact]
    public void Wine_process_reports_the_windows_program_it_runs()
    {
        // Arrange
        var cmdline = Encoding.UTF8.GetBytes("Z:\\home\\deck\\.local\\share\\Steam\\steamapps\\common\\Lantern\\Lantern.exe\0--fullscreen\0");

        // Act
        var path = SystemProcessList.WinePath(cmdline);

        // Assert
        path.Should().Be("/home/deck/.local/share/Steam/steamapps/common/Lantern/Lantern.exe");
    }

    [Fact]
    public void Wine_process_with_a_drive_other_than_z_is_not_mapped()
    {
        // Arrange
        var cmdline = Encoding.UTF8.GetBytes("C:\\windows\\system32\\services.exe\0");

        // Act
        var path = SystemProcessList.WinePath(cmdline);

        // Assert
        path.Should().BeNull();
    }
}
