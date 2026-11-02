using System.Security;

namespace AiLocalPack.Toolkit.Engines;

/// <summary>
/// Read-only view of one folder inside a game folder, listed once on first use. Name lookups ignore case (Windows games
/// run through Proton keep Windows file names). Never throws for disk state: an unreadable folder looks empty.
/// </summary>
internal sealed class FolderView
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = false,
    };

    private (Dictionary<string, string> Files, Dictionary<string, string> Dirs, string[] FileNames, string[] DirNames)? _entries;

    private FolderView(string fullPath, string relative)
    {
        FullPath = fullPath;
        Relative = relative;
    }

    public static FolderView Root(string fullPath) => new(fullPath, "");

    public string FullPath { get; }

    /// <summary>Path from the game folder, '/'-separated; empty for the game folder itself.</summary>
    public string Relative { get; }

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(FullPath));

    /// <summary>File names, sorted case-insensitively (stable choice on every OS).</summary>
    public IReadOnlyList<string> FileNames => Load().FileNames;

    /// <summary>Subfolder names, sorted case-insensitively.</summary>
    public IReadOnlyList<string> DirNames => Load().DirNames;

    /// <summary>Relative path of the file <paramref name="name"/> in this folder, or <c>null</c>.</summary>
    public string? File(string name) => Load().Files.TryGetValue(name, out var actual) ? RelativeOf(actual) : null;

    public FolderView? Dir(string name) =>
        Load().Dirs.TryGetValue(name, out var actual) ? new FolderView(Path.Combine(FullPath, actual), RelativeOf(actual)) : null;

    /// <summary>Nested folder by a '/'-separated path, or <c>null</c>.</summary>
    public FolderView? At(string path)
    {
        var folder = this;
        foreach (var part in path.Split('/'))
        {
            folder = folder?.Dir(part);
        }

        return folder;
    }

    /// <summary>Relative path of a file given by a '/'-separated path, or <c>null</c>.</summary>
    public string? FileAt(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? File(path) : At(path[..slash])?.File(path[(slash + 1)..]);
    }

    public string RelativeOf(string name) => Relative.Length == 0 ? name : $"{Relative}/{name}";

    public string FullPathOf(string name) => Path.Combine(FullPath, name);

    private (Dictionary<string, string> Files, Dictionary<string, string> Dirs, string[] FileNames, string[] DirNames) Load()
    {
        if (_entries is { } entries)
        {
            return entries;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in new DirectoryInfo(FullPath).EnumerateFileSystemInfos("*", Options))
            {
                // First of two names differing only in case wins.
                (entry is DirectoryInfo ? dirs : files).TryAdd(entry.Name, entry.Name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Missing or unreadable: looks empty.
        }

        entries = (files, dirs, Sorted(files.Values), Sorted(dirs.Values));
        _entries = entries;
        return entries;
    }

    private static string[] Sorted(IEnumerable<string> names) =>
        [.. names.Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal)];
}
