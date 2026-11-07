using System.IO.Compression;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Dependencies;

/// <summary>A dependency archive that can't be installed (damaged, a path leaving the game folder, a link…).</summary>
public sealed class DependencyArchiveException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A file of an unpacked dependency: its path in the game folder and the unpacked copy.</summary>
public sealed record DependencyFile(string Path, string Source);

/// <summary>
/// Unpacks a whitelisted dependency's ZIP release (its hash already checked) into a staging folder, laid out as it goes
/// into the game: entries under <see cref="DependencyLayout.From"/> go to <see cref="DependencyLayout.Into"/>. Every
/// entry name is checked before anything is written (relative, no <c>..</c>, no drive or <c>\</c>, no link, no two
/// names that differ only in case), so no path in the archive can reach outside the staging folder. Bounded:
/// <see cref="MaxEntries"/> files, <see cref="MaxSize"/> unpacked.
/// </summary>
public static class DependencyArchive
{
    public const int MaxEntries = 20_000;

    public const long MaxSize = 1L << 30;

    /// <summary>Unix file type bits of a ZIP entry's external attributes (high 16 bits): a symbolic link.</summary>
    private const int UnixLink = 0xA000;

    /// <exception cref="DependencyArchiveException">The archive is damaged or holds a name that can't be installed.</exception>
    public static IReadOnlyList<DependencyFile> Extract(string archive, DependencyLayout layout, string staging, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archive);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(staging);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var plan = Plan(zip, layout);
            var files = new List<DependencyFile>();
            long written = 0;
            foreach (var (entry, path) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = InstallPaths.Resolve(staging, path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        written += read;
                        if (written > MaxSize)
                        {
                            throw new DependencyArchiveException($"{System.IO.Path.GetFileName(archive)} unpacks to more than {MaxSize / (1024 * 1024)} MB.");
                        }

                        output.Write(buffer, 0, read);
                    }
                }

                files.Add(new DependencyFile(path, target));
            }

            return files;
        }
        catch (InvalidDataException e)
        {
            throw new DependencyArchiveException($"{System.IO.Path.GetFileName(archive)} is not a readable ZIP archive: {e.Message}", e);
        }
    }

    /// <summary>The entries to unpack with their game paths, every name checked first.</summary>
    private static List<(ZipArchiveEntry Entry, string Path)> Plan(ZipArchive zip, DependencyLayout layout)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            throw new DependencyArchiveException($"The archive has {zip.Entries.Count} entries, more than {MaxEntries}.");
        }

        var from = layout.From.Length == 0 ? string.Empty : layout.From + "/";
        var plan = new List<(ZipArchiveEntry, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/'))
            {
                continue;
            }

            if (!PackFormat.IsRelativePath(name) || name.Contains(':', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
            {
                throw new DependencyArchiveException($"The archive entry '{name}' is not a plain relative path.");
            }

            if (((entry.ExternalAttributes >> 16) & 0xF000) == UnixLink)
            {
                throw new DependencyArchiveException($"The archive entry '{name}' is a link.");
            }

            if (!name.StartsWith(from, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = name[from.Length..];
            var path = layout.Into.Length == 0 ? rest : $"{layout.Into}/{rest}";
            if (!InstallPaths.IsValid(path))
            {
                throw new DependencyArchiveException($"The archive entry '{name}' can't be installed as '{path}'.");
            }

            if (!seen.Add(path))
            {
                throw new DependencyArchiveException($"The archive has '{name}' twice (names differing only in case count as one).");
            }

            plan.Add((entry, path));
        }

        if (plan.Count == 0)
        {
            throw new DependencyArchiveException(from.Length == 0 ? "The archive holds no file." : $"The archive holds no file under '{layout.From}/'.");
        }

        // Prefixes of each other ("a" a file and "a/b" a file) can't both be written.
        foreach (var (_, path) in plan)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join('/', parts[..i]);
                if (seen.Contains(folder))
                {
                    throw new DependencyArchiveException($"The archive has '{folder}' both as a file and as a folder.");
                }
            }
        }

        return [.. plan.OrderBy(p => p.Item2, StringComparer.Ordinal)];
    }
}
