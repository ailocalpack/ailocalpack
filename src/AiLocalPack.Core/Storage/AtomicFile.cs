namespace AiLocalPack.Core.Storage;

/// <summary>Writes a file so that readers see either the old content or the complete new one, never a half-written file.</summary>
public static class AtomicFile
{
    /// <summary>
    /// Runs <paramref name="write"/> against a temp file next to <paramref name="path"/>, flushes it to disk, then renames it
    /// over <paramref name="path"/>. If <paramref name="write"/> or the flush throws, the old file is untouched and the
    /// temp file is removed. Creates the parent folder.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        // Unique name: two app instances saving at once must not share a temp file.
        var temp = Path.Combine(folder, $"{Path.GetFileName(full)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, full);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="bytes"/>.</summary>
    public static void WriteAllBytes(string path, ReadOnlyMemory<byte> bytes) => Write(path, s => s.Write(bytes.Span));

    /// <summary>How long a rename over a file another reader has open is retried (Windows refuses it meanwhile).</summary>
    internal static readonly TimeSpan ReplaceRetryLimit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Renames <paramref name="temp"/> over <paramref name="target"/>. On Windows a reader holding the target open
    /// without delete sharing (any <c>File.ReadAllBytes</c>, an antivirus scan) makes the rename fail for a moment; it is
    /// retried with growing waits for up to <see cref="ReplaceRetryLimit"/>, then the last error is thrown.
    /// </summary>
    private static void Replace(string temp, string target)
    {
        var waited = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && waited < ReplaceRetryLimit && File.Exists(temp))
            {
                var wait = TimeSpan.FromMilliseconds(Math.Min(10 * attempt, 200));
                Thread.Sleep(wait);
                waited += wait;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is harmless; the original error matters more.
        }
    }
}
