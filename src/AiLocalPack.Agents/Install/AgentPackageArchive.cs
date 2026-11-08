using System.Formats.Tar;
using System.IO.Compression;

namespace AiLocalPack.Agents.Install;

/// <summary>A package that can't be unpacked safely.</summary>
public sealed class AgentArchiveException(string message) : Exception(message);

/// <summary>
/// Unpacks an agent's <c>.tar.gz</c> package (task 43) into an empty folder. Every entry name is checked before it is
/// written: relative, <c>/</c>-separated, no <c>..</c>, drive or backslash, no name twice (ignoring case); only folders
/// and regular files (links and devices refuse the package); at most <see cref="MaxEntries"/> entries and
/// <see cref="MaxBytes"/> unpacked. On Unix a file keeps its permission bits (rwx only: never set-ID).
/// </summary>
public static class AgentPackageArchive
{
    public const int MaxEntries = 20_000;

    public const long MaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Unpacks <paramref name="archive"/> into <paramref name="target"/>. Throws <see cref="AgentArchiveException"/>, and IO errors.</summary>
    public static void Extract(string archive, string target, long maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(target);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;
        long total = 0;
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        try
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (++entries > MaxEntries)
                {
                    throw new AgentArchiveException($"The package has more than {MaxEntries} entries.");
                }

                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                var name = Name(entry.Name);
                if (name is null)
                {
                    continue;
                }

                var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(path);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        if (!seen.Add(name))
                        {
                            throw new AgentArchiveException($"{name} is in the package twice.");
                        }

                        total += entry.Length;
                        if (total > maxBytes)
                        {
                            throw new AgentArchiveException($"The package unpacks to more than {maxBytes} bytes.");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            entry.DataStream?.CopyTo(output);
                        }

                        if (!OperatingSystem.IsWindows())
                        {
                            const UnixFileMode rwx = (UnixFileMode)0x1FF;
                            File.SetUnixFileMode(path, (entry.Mode & rwx) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        }

                        break;
                    default:
                        throw new AgentArchiveException($"{name}: {entry.EntryType} entries aren't allowed in a package.");
                }
            }
        }
        catch (InvalidDataException e)
        {
            throw new AgentArchiveException($"The package is damaged: {e.Message}");
        }
        catch (FormatException e)
        {
            throw new AgentArchiveException($"The package is damaged: {e.Message}");
        }
    }

    /// <summary>The entry's checked relative name, or <c>null</c> for the root itself.</summary>
    private static string? Name(string raw)
    {
        var name = raw;
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        name = name.TrimEnd('/');
        if (name.Length == 0 || name == ".")
        {
            return null;
        }

        if (name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal)
            || name.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new AgentArchiveException($"\"{raw}\" isn't a safe name inside the package.");
        }

        return name;
    }
}
