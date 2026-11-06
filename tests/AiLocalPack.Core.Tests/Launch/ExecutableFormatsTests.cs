using AiLocalPack.Core.Launch;

namespace AiLocalPack.Core.Tests.Launch;

public sealed class ExecutableFormatsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(new byte[] { (byte)'M', (byte)'Z', 0x90, 0 }, ExecutableFormat.WindowsPe)]
    [InlineData(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, ExecutableFormat.LinuxElf)]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, ExecutableFormat.MacMachO)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, ExecutableFormat.MacMachO)]
    [InlineData(new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b' }, ExecutableFormat.Script)]
    [InlineData(new byte[] { 1, 2, 3, 4 }, ExecutableFormat.Unknown)]
    public void File_format_comes_from_its_first_bytes(byte[] head, ExecutableFormat expected)
    {
        // Arrange
        var path = _dir.Combine("game");
        File.WriteAllBytes(path, head);

        // Act
        var format = ExecutableFormats.Detect(path);

        // Assert
        format.Should().Be(expected);
    }

    [Fact]
    public void Sh_file_without_shebang_is_a_script()
    {
        // Arrange
        var path = _dir.File("start.sh", "cd game\n").Combine("start.sh");

        // Act
        var format = ExecutableFormats.Detect(path);

        // Assert
        format.Should().Be(ExecutableFormat.Script);
    }

    [Fact]
    public void App_folder_is_a_bundle()
    {
        // Arrange
        var path = _dir.Dir("Lantern.app").Combine("Lantern.app");

        // Act
        var format = ExecutableFormats.Detect(path);

        // Assert
        format.Should().Be(ExecutableFormat.MacBundle);
    }

    [Fact]
    public void Missing_file_is_unknown()
    {
        // Act
        var format = ExecutableFormats.Detect(_dir.Combine("missing.exe"));

        // Assert
        format.Should().Be(ExecutableFormat.Unknown);
    }
}
