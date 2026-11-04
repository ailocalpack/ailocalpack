using System.IO.Compression;
using System.Text;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Packs;

public sealed class ExecutableFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string name, byte[] content)
    {
        _dir.File(name, content);
        return _dir.Combine(name);
    }

    private static byte[] Gzip(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
        {
            gzip.Write(content);
        }

        return buffer.ToArray();
    }

    /// <summary>A tar archive: 512-byte header with <c>ustar</c> at offset 257.</summary>
    private static byte[] Tar()
    {
        var tar = new byte[1024];
        Encoding.ASCII.GetBytes("game.exe").CopyTo(tar, 0);
        Encoding.ASCII.GetBytes("ustar").CopyTo(tar, 257);
        return tar;
    }

    public static TheoryData<string, byte[], string> Disguised => new()
    {
        { "pe", [.. "MZ"u8, 0x90, 0x00, 0x03], "Windows executable" },
        { "elf", [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1], "Linux executable" },
        { "macho", [0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1], "macOS executable" },
        { "fat-macho", [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2], "universal" },
        { "script", [.. "#!/bin/sh\nrm -rf ~\n"u8], "script" },
        { "zip", [.. "PK\x03\x04"u8, 20, 0], "zip archive" },
        { "seven-zip", [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4], "7-Zip" },
        { "wasm", [.. "\0asm"u8, 1, 0, 0, 0], "WebAssembly" },
        { "tar", Tar(), "tar archive" },
    };

    [Theory]
    [MemberData(nameof(Disguised))]
    public void Content_that_can_run_is_caught_behind_a_json_extension(string name, byte[] content, string expected)
    {
        // Arrange
        var path = Write($"{name}.json", content);

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().Contain(expected);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData("run.sh")]
    [InlineData("payload.dll")]
    [InlineData("install.bat")]
    [InlineData("hook.py")]
    [InlineData("pack.zip")]
    public void Executable_extension_is_caught_whatever_the_content(string name)
    {
        // Arrange
        var path = Write(name, "{}"u8.ToArray());

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().Contain("extension");
    }

    [Fact]
    public void Gzip_compressed_executable_is_caught()
    {
        // Arrange
        var path = Write("strings.json.gz", Gzip([.. "MZ"u8, 0x90, 0x00, .. new byte[2000]]));

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().Contain("compressed file").And.Contain("Windows executable");
    }

    [Fact]
    public void Gzip_compressed_tar_is_caught()
    {
        // Arrange
        var path = Write("strings.json.gz", Gzip(Tar()));

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().Contain("tar archive");
    }

    [Fact]
    public void Gzip_compressed_string_table_passes()
    {
        // Arrange
        var path = Write("strings.json.gz", Gzip(Encoding.UTF8.GetBytes("""{"schemaVersion":1,"language":"ru","strings":{"a":"б"}}""")));

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().BeNull();
    }

    [Fact]
    public void Plain_json_passes()
    {
        // Arrange
        var path = Write("manifest.json", Encoding.UTF8.GetBytes("""{"schemaVersion": 1}"""));

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().BeNull();
    }

    [Fact]
    public void Empty_file_passes()
    {
        // Arrange
        var path = Write("empty.json", []);

        // Act
        var reason = ExecutableFiles.Check(path);

        // Assert
        reason.Should().BeNull();
    }
}
