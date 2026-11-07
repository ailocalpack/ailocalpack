using System.Buffers.Binary;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Dependencies;

public sealed class GamePlatformsTests
{
    private static byte[] Pe(ushort machine)
    {
        var bytes = new byte[0x80];
        "MZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x40);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x40));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x44), machine);
        return bytes;
    }

    private static byte[] Elf(ushort machine)
    {
        var bytes = new byte[0x40];
        bytes[0] = 0x7F;
        "ELF"u8.CopyTo(bytes.AsSpan(1));
        bytes[4] = 2;
        bytes[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x12), machine);
        return bytes;
    }

    private static byte[] Fat(params uint[] cpus)
    {
        var bytes = new byte[8 + (cpus.Length * 20)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)cpus.Length);
        for (var i = 0; i < cpus.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + (i * 20)), cpus[i]);
        }

        return bytes;
    }

    [Theory]
    [InlineData((ushort)0x8664, "windows-x64")]
    [InlineData((ushort)0x14C, "windows-x86")]
    public void Windows_build_by_its_pe_machine(ushort machine, string expected)
    {
        // Act
        var platform = GamePlatforms.Parse(Pe(machine));

        // Assert
        platform!.ToString().Should().Be(expected);
    }

    [Fact]
    public void Linux_build_by_its_elf_machine()
    {
        // Act
        var platform = GamePlatforms.Parse(Elf(0x3E));

        // Assert
        platform!.ToString().Should().Be("linux-x64");
    }

    [Fact]
    public void Thin_mach_o_is_one_cpu()
    {
        // Arrange
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x0100000C);

        // Act
        var platform = GamePlatforms.Parse(bytes);

        // Assert
        platform!.ToString().Should().Be("macos-arm64");
    }

    [Fact]
    public void Universal_binary_puts_the_machine_cpu_first()
    {
        // Act
        var platform = GamePlatforms.Parse(Fat(0x01000007, 0x0100000C), preferredArch: "arm64");

        // Assert
        (platform!.Os, platform.Arches).Should().BeEquivalentTo((OsKind.MacOS, new[] { "arm64", "x64" }), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Text_stub_is_no_platform()
    {
        // Act
        var platform = GamePlatforms.Parse("MZ stub: not a real executable"u8);

        // Assert
        platform.Should().BeNull();
    }

    [Fact]
    public void Bundle_is_read_from_its_macos_binary()
    {
        // Arrange
        using var dir = new TempDir();
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x01000007);
        dir.File("Game.app/Contents/MacOS/Game", bytes);

        // Act
        var platform = GamePlatforms.Detect(dir.Combine("Game.app"));

        // Assert
        platform!.ToString().Should().Be("macos-x64");
    }
}
