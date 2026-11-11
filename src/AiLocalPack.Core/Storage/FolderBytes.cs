namespace AiLocalPack.Core.Storage;

/// <summary>
/// Bytes a folder takes (Settings → Storage, task 66): the sum of its files' lengths, every level down. Links are never
/// followed or counted (a working copy's recreated links point into the game), unreadable folders are skipped. Never
/// throws for disk state.
/// </summary>
public static class FolderBytes
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,

        // A link (file or folder) is a reparse point on every OS in .NET: skipped, so folder links aren't walked.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>The bytes of the files under <paramref name="folder"/>; a missing folder is 0.</summary>
    public static long Of(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += Length(file);
            }

            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The bytes of one file; 0 when it went away while measured.</summary>
    public static long OfFile(string file)
    {
        try
        {
            return File.Exists(file) ? new FileInfo(file).Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
