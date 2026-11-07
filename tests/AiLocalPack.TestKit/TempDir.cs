namespace AiLocalPack.TestKit;

/// <summary>
/// A unique temporary folder that is deleted (with everything inside, read-only files included) on dispose.
/// Build a file tree with <see cref="File(string, string)"/> and <see cref="Dir(string)"/>; paths are relative
/// to <see cref="Path"/> and use '/' as the separator on every OS.
/// </summary>
public sealed class TempDir : IDisposable
{
    private bool _disposed;

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ailocalpack-tests", System.IO.Path.GetRandomFileName());
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of the folder.</summary>
    public string Path { get; }

    /// <summary>Absolute path of <paramref name="relativePath"/> inside the folder. Rejects paths that escape it.</summary>
    public string Combine(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (System.IO.Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Path must be relative: '{relativePath}'.", nameof(relativePath));
        }

        var root = System.IO.Path.GetFullPath(Path);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
        var rootWithSeparator = System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Path escapes the temp folder: '{relativePath}'.", nameof(relativePath));
        }

        return full;
    }

    /// <summary>Creates a text file (UTF-8, no BOM) and its parent folders.</summary>
    public TempDir File(string relativePath, string content = "")
    {
        var full = PrepareFile(relativePath);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    /// <summary>Creates a binary file and its parent folders.</summary>
    public TempDir File(string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = PrepareFile(relativePath);
        System.IO.File.WriteAllBytes(full, content);
        return this;
    }

    /// <summary>Creates a folder (and its parents).</summary>
    public TempDir Dir(string relativePath)
    {
        Directory.CreateDirectory(Combine(relativePath));
        return this;
    }

    /// <summary>Creates a text file for each entry: key = relative path, value = content.</summary>
    public TempDir Files(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var (path, content) in files)
        {
            File(path, content);
        }

        return this;
    }

    /// <summary>Copies the folder <paramref name="source"/> (absolute) with everything inside to <paramref name="relativePath"/> (files of the same name are replaced).</summary>
    public TempDir CopyFolder(string source, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = Combine(relativePath);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            System.IO.File.Copy(file, to, overwrite: true);
        }

        Directory.CreateDirectory(target);
        return this;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!Directory.Exists(Path))
        {
            return;
        }

        // Windows refuses to delete read-only files; a file closed a moment ago can also still be locked briefly.
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearReadOnly(Path);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception e) when (attempt < attempts && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private string PrepareFile(string relativePath)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    private static void ClearReadOnly(string folder)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            // A file a background task deletes meanwhile reads as -1 (every flag set): skip it.
            try
            {
                if (entry.Exists && (int)entry.Attributes != -1 && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
            }
        }
    }
}
