using System.Security.Cryptography;

namespace AiLocalPack.Core.Install;

/// <summary>Path checks and the file operations of an install transaction.</summary>
internal static class InstallPaths
{
    /// <summary>Relative, <c>/</c>-separated, no empty, <c>.</c> or <c>..</c> parts, no <c>\</c> or <c>:</c>.</summary>
    public static bool IsValid(string? path) =>
        !string.IsNullOrEmpty(path)
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.Contains('\0', StringComparison.Ordinal)
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>A single folder name of our own making (backup and transaction names).</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="root"/>; throws when it would leave it.</summary>
    public static string Resolve(string root, string relative)
    {
        if (!IsValid(relative))
        {
            throw new ArgumentException($"'{relative}' is not a relative '/'-separated path.", nameof(relative));
        }

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal)
            ? full
            : throw new ArgumentException($"'{relative}' leaves the folder.", nameof(relative));
    }

    /// <summary>SHA-256 (lowercase hex) of the file, or <c>null</c> when it doesn't exist.</summary>
    public static string? HashOrNull(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Hash(stream, cancellationToken);
    }

    public static string Hash(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> (created or replaced atomically: a temp file next to
    /// it, flushed, then renamed over it) and returns the SHA-256 of what was written. Keeps the Unix permissions of the
    /// file it replaces.
    /// </summary>
    public static string CopyAtomically(string source, string target, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(target)}.{Path.GetRandomFileName()}.ailocalpack");
        try
        {
            string sha;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                output.Flush(flushToDisk: true);
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!OperatingSystem.IsWindows() && File.Exists(target))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
            }

            File.Move(temp, target, overwrite: true);
            return sha;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file is harmless.
        }
    }

    /// <summary>Deletes a folder tree; read-only files inside (Windows) are made writable first. Missing = done.</summary>
    public static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (OperatingSystem.IsWindows() && File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Deletes the folder if it is empty; never throws.</summary>
    public static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind is harmless.
        }
    }

    /// <summary>
    /// <see cref="DriveInfo.GetDrives"/> one call at a time: on macOS it reads <c>getmntinfo</c>'s shared buffer, and two
    /// calls at once crash the process (AccessViolationException).
    /// </summary>
    private static readonly Lock DrivesLock = new();

    /// <summary>The drive (longest matching mount point) holding <paramref name="path"/>; <c>null</c> when unknown.</summary>
    public static DriveInfo? DriveOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            lock (DrivesLock)
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, comparison))
                    .MaxBy(d => d.RootDirectory.FullName.Length);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
