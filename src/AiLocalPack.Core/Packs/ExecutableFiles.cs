using System.IO.Compression;

namespace AiLocalPack.Core.Packs;

/// <summary>
/// Spots files that could run code, so registry CI can reject them: executables (PE, ELF, Mach-O, WebAssembly, Java
/// class), scripts (shebang, or a script extension) and archives that could carry either. Checked by extension
/// <em>and</em> by the first bytes, so a renamed executable (<c>strings.json</c> holding a PE) is still caught. A gzip
/// stream is inflated and its start checked again: a gzip-compressed string table passes, a <c>.tar.gz</c> doesn't.
/// </summary>
public static class ExecutableFiles
{
    /// <summary>Bytes read from the start of a file (a tar header's <c>ustar</c> sits at offset 257).</summary>
    private const int HeadLength = 512;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Native code and installers
        ".exe", ".dll", ".sys", ".scr", ".com", ".cpl", ".ocx", ".drv", ".efi", ".msi", ".msp", ".msix", ".appx",
        ".so", ".dylib", ".bundle", ".app", ".elf", ".out", ".run", ".bin", ".appimage", ".deb", ".rpm", ".dmg", ".pkg",
        ".apk", ".ipa", ".dex", ".wasm", ".node", ".jar", ".class", ".pyc", ".pyo", ".lnk", ".reg",
        // Scripts
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".mjs", ".cjs", ".wsf", ".wsh", ".hta",
        ".sh", ".bash", ".zsh", ".fish", ".csh", ".ksh", ".command", ".py", ".pyw", ".pl", ".rb", ".php", ".lua", ".tcl",
        ".applescript", ".scpt", ".desktop", ".csx", ".fsx",
        // Archives
        ".zip", ".7z", ".rar", ".tar", ".tgz", ".tbz", ".txz", ".xz", ".bz2", ".lz", ".lzma", ".zst", ".cab", ".iso",
    };

    /// <summary>Why <paramref name="path"/> looks able to run code, or <c>null</c>. Throws on a file that can't be read.</summary>
    public static string? Check(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Extensions.Contains(Path.GetExtension(path)))
        {
            return $"has an executable, script or archive extension ({Path.GetExtension(path).ToLowerInvariant()}).";
        }

        using var stream = File.OpenRead(path);
        var head = ReadHead(stream);
        if (Kind(head) is { } kind)
        {
            return $"is {kind}.";
        }

        if (!IsGzip(head))
        {
            return null;
        }

        stream.Position = 0;
        byte[] inner;
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            inner = ReadHead(gzip);
        }
        catch (InvalidDataException)
        {
            // Not valid gzip: nothing can run it either; the string table reader reports the damage.
            return null;
        }

        return Kind(inner) is { } innerKind ? $"holds a compressed file that is {innerKind}."
            : IsGzip(inner) ? "is a gzip stream inside a gzip stream." : null;
    }

    private static bool IsGzip(ReadOnlySpan<byte> head) => head.StartsWith((byte[])[0x1F, 0x8B]);

    private static byte[] ReadHead(Stream stream)
    {
        var head = new byte[HeadLength];
        var length = stream.ReadAtLeast(head, HeadLength, throwOnEndOfStream: false);
        return head[..length];
    }

    private static string? Kind(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("MZ"u8))
        {
            return "a Windows executable (PE)";
        }

        if (head.StartsWith((byte[])[0x7F, (byte)'E', (byte)'L', (byte)'F']))
        {
            return "a Linux executable (ELF)";
        }

        // Thin Mach-O, either byte order, 32 / 64 bit; CA FE BA BE is a fat Mach-O or a Java class file.
        if (head.StartsWith((byte[])[0xFE, 0xED, 0xFA, 0xCE]) || head.StartsWith((byte[])[0xFE, 0xED, 0xFA, 0xCF])
            || head.StartsWith((byte[])[0xCE, 0xFA, 0xED, 0xFE]) || head.StartsWith((byte[])[0xCF, 0xFA, 0xED, 0xFE]))
        {
            return "a macOS executable (Mach-O)";
        }

        if (head.StartsWith((byte[])[0xCA, 0xFE, 0xBA, 0xBE]))
        {
            return "a macOS universal executable or a Java class";
        }

        if (head.StartsWith("\0asm"u8))
        {
            return "a WebAssembly module";
        }

        if (head.StartsWith("#!"u8) || head.StartsWith((byte[])[0xEF, 0xBB, 0xBF, (byte)'#', (byte)'!']))
        {
            return "a script (starts with #!)";
        }

        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x05\x06"u8) || head.StartsWith("PK\x07\x08"u8))
        {
            return "a zip archive";
        }

        if (head.StartsWith((byte[])[(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return "a 7-Zip archive";
        }

        if (head.StartsWith("Rar!\x1A\x07"u8))
        {
            return "a RAR archive";
        }

        if (head.StartsWith((byte[])[0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00]))
        {
            return "an xz archive";
        }

        if (head.StartsWith("BZh"u8))
        {
            return "a bzip2 archive";
        }

        if (head.StartsWith((byte[])[0x28, 0xB5, 0x2F, 0xFD]))
        {
            return "a zstd archive";
        }

        if (head.StartsWith("MSCF"u8))
        {
            return "a Windows cabinet archive";
        }

        if (head.StartsWith((byte[])[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return "a Windows installer or compound document";
        }

        if (head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return "a tar archive";
        }

        return null;
    }
}
