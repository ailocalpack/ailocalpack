using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>OS errors worded as install problems (task 27).</summary>
public sealed class InstallErrorsTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020), InstallProblem.FileInUse)]
    [InlineData(unchecked((int)0x80070021), InstallProblem.FileInUse)]
    [InlineData(unchecked((int)0x80070070), InstallProblem.DiskFull)]
    [InlineData(unchecked((int)0x80070027), InstallProblem.DiskFull)]
    [InlineData(unchecked((int)0x80070013), InstallProblem.ReadOnly)]
    [InlineData(unchecked((int)0x80070057), InstallProblem.IoError)]
    public void Windows_error_codes_map_to_problems(int hresult, InstallProblem expected)
    {
        // Arrange
        var error = new IOException("x", hresult);

        // Act
        var problem = InstallErrors.Classify(error, null, OsKind.Windows);

        // Assert
        problem.Should().Be(expected);
    }

    [Theory]
    [InlineData(28, OsKind.Linux, InstallProblem.DiskFull)]
    [InlineData(28, OsKind.MacOS, InstallProblem.DiskFull)]
    [InlineData(122, OsKind.Linux, InstallProblem.DiskFull)]
    [InlineData(69, OsKind.MacOS, InstallProblem.DiskFull)]
    [InlineData(69, OsKind.Linux, InstallProblem.IoError)]
    [InlineData(26, OsKind.Linux, InstallProblem.FileInUse)]
    [InlineData(30, OsKind.Linux, InstallProblem.ReadOnly)]
    [InlineData(13, OsKind.MacOS, InstallProblem.PermissionDenied)]
    public void Unix_errno_values_map_to_problems(int errno, OsKind os, InstallProblem expected)
    {
        // Arrange
        var error = new IOException("x", errno);

        // Act
        var problem = InstallErrors.Classify(error, null, os);

        // Assert
        problem.Should().Be(expected);
    }

    [Fact]
    public void Access_denied_on_a_writable_file_is_permission_denied()
    {
        // Arrange
        using var dir = new TempDir();
        dir.File("a.txt", "a");

        // Act
        var problem = InstallErrors.Classify(new UnauthorizedAccessException(), dir.Combine("a.txt"), OsKind.Linux);

        // Assert
        problem.Should().Be(InstallProblem.PermissionDenied);
    }

    [Fact]
    public void Access_denied_on_a_read_only_file_is_read_only()
    {
        // Arrange
        using var dir = new TempDir();
        dir.File("a.txt", "a");
        var path = dir.Combine("a.txt");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }

        // Act
        var problem = InstallErrors.Classify(new UnauthorizedAccessException(), path, OsKind.Linux);

        // Assert
        problem.Should().Be(InstallProblem.ReadOnly);
    }
}
