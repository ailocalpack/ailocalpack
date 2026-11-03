namespace AiLocalPack.Toolkit.Folders;

/// <summary>How many files a folder holds and their total size (fact chip "1 248 files · 812 MB", design §2).</summary>
public readonly record struct FolderSize(long Files, long Bytes)
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = true,
    };

    /// <summary>
    /// Counts every file under <paramref name="folder"/> (symlinked folders not followed). Best effort: unreadable
    /// folders are skipped, a missing folder is empty; throws only <see cref="OperationCanceledException"/>.
    /// </summary>
    public static FolderSize Measure(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        long files = 0, bytes = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files++;
                bytes += file.Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Missing or vanished folder: what was counted so far.
        }

        return new FolderSize(files, bytes);
    }
}
