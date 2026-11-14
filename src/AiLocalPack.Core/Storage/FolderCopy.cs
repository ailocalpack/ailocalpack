namespace AiLocalPack.Core.Storage;

/// <summary>Copies and replaces small folders of the app's own files (task 75: drafts of a translation). Links are not followed.</summary>
public static class FolderCopy
{
    /// <summary>
    /// Makes <paramref name="destination"/> a copy of <paramref name="source"/>: written into a temp folder next to it,
    /// then renamed into place (an earlier folder there is deleted first). A missing source deletes the destination.
    /// </summary>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be read or written.</exception>
    public static void Replace(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        if (!Directory.Exists(source))
        {
            Delete(destination);
            return;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            CopyInto(source, staging);
            Delete(destination);
            Directory.Move(staging, destination);
        }
        finally
        {
            Delete(staging);
        }
    }

    /// <summary>Deletes the folder with read-only files (nothing when absent; links are removed, never followed).</summary>
    /// <exception cref="IOException">A file can't be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be deleted.</exception>
    public static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }

    /// <summary>A hash of the folder's file names and contents (ordinal order), or <c>null</c> when it is missing; tells a changed folder apart.</summary>
    /// <exception cref="IOException">A file can't be read.</exception>
    public static string? Fingerprint(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/') + "\n"));
            hash.AppendData(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void CopyInto(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo dir)
            {
                CopyInto(dir.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }
}
