using System.Buffers.Binary;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Dependencies;

/// <summary>The OS a game build is for and its CPUs, preferred first (a universal macOS binary has several).</summary>
public sealed record GamePlatform(OsKind Os, IReadOnlyList<string> Arches)
{
    public override string ToString() => $"{DependencyPlatform.OsName(Os)}-{string.Join("/", Arches)}";
}

/// <summary>
/// Tells the platform of a game build from its main executable's header: PE machine (Windows), ELF machine (Linux),
/// Mach-O CPU types (macOS; a <c>.app</c> bundle by its <c>Contents/MacOS</c> binary). Never throws for disk state.
/// </summary>
public static class GamePlatforms
{
    /// <param name="executable">The game's main executable (or <c>.app</c> bundle), full path.</param>
    /// <param name="preferredArch">The machine's CPU (<c>x64</c>, <c>arm64</c>), first among a universal binary's.</param>
    public static GamePlatform? Detect(string executable, string? preferredArch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        try
        {
            if (Directory.Exists(executable))
            {
                var macOs = Path.Combine(executable, "Contents", "MacOS");
                return Directory.Exists(macOs)
                    ? Directory.EnumerateFiles(macOs).Order(StringComparer.Ordinal).Select(f => Read(f, preferredArch)).FirstOrDefault(p => p?.Os == OsKind.MacOS)
                    : null;
            }

            return File.Exists(executable) ? Read(executable, preferredArch) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The machine's CPU in <see cref="DependencyPlatform.Arches"/> names.</summary>
    public static string MachineArch => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    /// <summary>The platform from the first bytes of an executable, or <c>null</c> for an unknown or unsupported one.</summary>
    public static GamePlatform? Parse(ReadOnlySpan<byte> head, string? preferredArch = null)
    {
        if (head.Length >= 0x40 && head.StartsWith("MZ"u8))
        {
            var pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
            if (pe < 0 || pe + 6 > head.Length || !head[pe..].StartsWith("PE\0\0"u8))
            {
                return null;
            }

            return Arch(BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 4)..]), 0x14C, 0x8664, 0xAA64) is { } arch
                ? new GamePlatform(OsKind.Windows, [arch])
                : null;
        }

        if (head.Length >= 0x14 && head.StartsWith((byte[])[0x7F, (byte)'E', (byte)'L', (byte)'F']))
        {
            var machine = head[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(head[0x12..]) : BinaryPrimitives.ReadUInt16LittleEndian(head[0x12..]);
            return Arch(machine, 3, 0x3E, 0xB7) is { } arch ? new GamePlatform(OsKind.Linux, [arch]) : null;
        }

        if (head.Length < 8)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (magic is 0xCEFAEDFE or 0xCFFAEDFE)
        {
            return MachArch(BinaryPrimitives.ReadUInt32LittleEndian(head[4..])) is { } arch ? new GamePlatform(OsKind.MacOS, [arch]) : null;
        }

        if (magic is 0xFEEDFACE or 0xFEEDFACF)
        {
            return MachArch(BinaryPrimitives.ReadUInt32BigEndian(head[4..])) is { } arch ? new GamePlatform(OsKind.MacOS, [arch]) : null;
        }

        if (magic == 0xCAFEBABE)
        {
            // Universal binary (Java class files share the magic, but have a version over 30 where this has a count).
            var count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            if (count is 0 or > 20 || 8 + (count * 20) > head.Length)
            {
                return null;
            }

            var arches = new List<string>();
            for (var i = 0; i < count; i++)
            {
                if (MachArch(BinaryPrimitives.ReadUInt32BigEndian(head[(8 + (i * 20))..])) is { } arch && !arches.Contains(arch))
                {
                    arches.Add(arch);
                }
            }

            if (preferredArch is not null && arches.Remove(preferredArch))
            {
                arches.Insert(0, preferredArch);
            }

            return arches.Count > 0 ? new GamePlatform(OsKind.MacOS, arches) : null;
        }

        return null;
    }

    private static GamePlatform? Read(string path, string? preferredArch)
    {
        Span<byte> head = stackalloc byte[4096];
        int length;
        using (var stream = File.OpenRead(path))
        {
            length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        return Parse(head[..length], preferredArch);
    }

    private static string? Arch(int machine, int x86, int x64, int arm64) =>
        machine == x86 ? "x86" : machine == x64 ? "x64" : machine == arm64 ? "arm64" : null;

    private static string? MachArch(uint cpu) => cpu switch
    {
        7 => "x86",
        0x01000007 => "x64",
        0x0100000C => "arm64",
        _ => null,
    };
}
