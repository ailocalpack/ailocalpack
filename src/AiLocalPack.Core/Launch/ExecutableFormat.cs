namespace AiLocalPack.Core.Launch;

/// <summary>What kind of program a game file is, as far as starting it goes.</summary>
public enum ExecutableFormat
{
    /// <summary>Not a program this app can start (unreadable, unknown bytes).</summary>
    Unknown,

    /// <summary>Windows PE (<c>MZ</c>).</summary>
    WindowsPe,

    /// <summary>Linux ELF.</summary>
    LinuxElf,

    /// <summary>A bare macOS Mach-O binary (thin or universal).</summary>
    MacMachO,

    /// <summary>A macOS <c>.app</c> bundle folder.</summary>
    MacBundle,

    /// <summary>A shell script (<c>.sh</c> or <c>#!</c>), run with <c>/bin/sh</c>.</summary>
    Script,
}

/// <summary>Tells an executable's <see cref="ExecutableFormat"/> from its first bytes (a bundle by its folder). Never throws.</summary>
public static class ExecutableFormats
{
    public static ExecutableFormat Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (Directory.Exists(path))
            {
                return path.TrimEnd('/', '\\').EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(path, "Contents", "Info.plist"))
                    ? ExecutableFormat.MacBundle
                    : ExecutableFormat.Unknown;
            }

            if (!File.Exists(path))
            {
                return ExecutableFormat.Unknown;
            }

            Span<byte> head = stackalloc byte[4];
            int length;
            using (var stream = File.OpenRead(path))
            {
                length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }

            return Detect(head[..length], Path.GetExtension(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ExecutableFormat.Unknown;
        }
    }

    /// <summary>The format from the file's first bytes, a <c>.sh</c> extension counting as a script.</summary>
    public static ExecutableFormat Detect(ReadOnlySpan<byte> head, string extension)
    {
        if (head.StartsWith("MZ"u8))
        {
            return ExecutableFormat.WindowsPe;
        }

        if (head.StartsWith((byte[])[0x7F, (byte)'E', (byte)'L', (byte)'F']))
        {
            return ExecutableFormat.LinuxElf;
        }

        if (head.StartsWith((byte[])[0xFE, 0xED, 0xFA, 0xCE]) || head.StartsWith((byte[])[0xFE, 0xED, 0xFA, 0xCF])
            || head.StartsWith((byte[])[0xCE, 0xFA, 0xED, 0xFE]) || head.StartsWith((byte[])[0xCF, 0xFA, 0xED, 0xFE])
            || head.StartsWith((byte[])[0xCA, 0xFE, 0xBA, 0xBE]))
        {
            return ExecutableFormat.MacMachO;
        }

        return head.StartsWith("#!"u8) || string.Equals(extension, ".sh", StringComparison.OrdinalIgnoreCase)
            ? ExecutableFormat.Script
            : ExecutableFormat.Unknown;
    }
}
