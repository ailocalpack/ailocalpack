namespace AiLocalPack.Toolkit.Recipes;

/// <summary>
/// The files of a game as the recipe tools read them: paths relative to the game folder, <c>/</c>-separated (empty =
/// the game folder itself). <see cref="GameFolder"/> is the folder as it is on disk; <see cref="PristineGameFiles"/> is
/// the game as it was before a pack changed it (the files the pack replaced read from its backup, the ones it added
/// hidden), so generation reads the originals without touching the live game (_Live game folder_ in
/// <c>docs/generation.md</c>). Read only. Links (symbolic links, junctions) are never listed or followed.
/// Methods throw the file system's exceptions (<see cref="IOException"/> and its <see cref="FileNotFoundException"/> /
/// <see cref="DirectoryNotFoundException"/>, <see cref="UnauthorizedAccessException"/>); the tools turn them into
/// <see cref="RecipeException"/>s.
/// </summary>
public interface IGameFiles
{
    /// <summary>
    /// The game folder on disk (full path): engine detection (the engine's markers are never a pack's files) and
    /// messages. Never read the files through it: a pack may have changed them.
    /// </summary>
    string Root { get; }

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Files in <paramref name="directory"/> (and its subfolders when <paramref name="recursive"/>), relative to the game folder, ordinal order.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    IReadOnlyList<string> Files(string directory, bool recursive = false);

    /// <summary>Folders directly in <paramref name="directory"/>, relative to the game folder, ordinal order.</summary>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    IReadOnlyList<string> Directories(string directory);

    /// <summary>A read-only, seekable stream of the file (shared: the game may hold it open).</summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    Stream OpenRead(string path);

    /// <summary>The file's size in bytes.</summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    long Length(string path);
}

/// <summary>Helpers over <see cref="IGameFiles"/>.</summary>
public static class GameFiles
{
    /// <summary>The folder <paramref name="gameDir"/> as it is on disk.</summary>
    public static IGameFiles Folder(string gameDir) => new GameFolder(gameDir);

    public static byte[] ReadAllBytes(this IGameFiles files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var stream = files.OpenRead(path);
        var length = stream.Length;
        if (length > Array.MaxLength)
        {
            throw new IOException($"{path} is too large to read whole ({length} bytes).");
        }

        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The file's place under <see cref="IGameFiles.Root"/> on disk (a name for libraries that want one, and messages).</summary>
    public static string FullPath(this IGameFiles files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        var normalized = Normalize(path);
        return normalized.Length == 0 ? files.Root : Path.Combine(files.Root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>The child folder named <paramref name="name"/> of <paramref name="directory"/> whatever its case, or <c>null</c> (also when the folder can't be listed).</summary>
    public static string? FindDirectory(this IGameFiles files, string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            return files.Directories(directory).FirstOrDefault(d => string.Equals(NameOf(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><paramref name="directory"/> and <paramref name="name"/> joined (<paramref name="directory"/> may be empty).</summary>
    public static string Combine(string directory, string name) => directory.Length == 0 ? name : $"{directory}/{name}";

    /// <summary>The last part of a relative path.</summary>
    public static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>The folder of a relative path (empty at the game folder).</summary>
    public static string ParentOf(string path) => path[..Math.Max(path.LastIndexOf('/'), 0)];

    /// <summary>
    /// A relative path in its canonical form: <c>/</c>-separated, no empty, <c>.</c> or <c>..</c> parts, no leading or
    /// trailing separator; empty = the game folder.
    /// </summary>
    /// <exception cref="ArgumentException">Rooted, or a part that leaves the game folder.</exception>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slashed = path.Replace('\\', '/').Trim('/');
        if (slashed.Length == 0)
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(path) || Path.IsPathRooted(slashed))
        {
            throw new ArgumentException($"Not a path relative to the game folder: '{path}'.", nameof(path));
        }

        var parts = slashed.Split('/');
        if (parts.Any(p => p is "" or "." or ".."))
        {
            throw new ArgumentException($"Not a path inside the game folder: '{path}'.", nameof(path));
        }

        return string.Join('/', parts);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it (both canonical).</summary>
    internal static bool IsUnder(string path, string folder, StringComparison comparison) =>
        folder.Length == 0
        || path.Equals(folder, comparison)
        || (path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, comparison));
}

/// <summary>A game folder as it is on disk.</summary>
public sealed class GameFolder : IGameFiles
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    public GameFolder(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public bool FileExists(string path) => File.Exists(this.FullPath(path));

    public bool DirectoryExists(string path) => Directory.Exists(this.FullPath(path));

    public IReadOnlyList<string> Files(string directory, bool recursive = false)
    {
        var start = GameFiles.Normalize(directory);
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in new DirectoryInfo(this.FullPath(folder)).EnumerateFileSystemInfos("*", Options))
            {
                if (IsLink(entry))
                {
                    continue;
                }

                var relative = GameFiles.Combine(folder, entry.Name);
                if (entry is DirectoryInfo)
                {
                    if (recursive)
                    {
                        pending.Push(relative);
                    }
                }
                else
                {
                    result.Add(relative);
                }
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public IReadOnlyList<string> Directories(string directory)
    {
        var folder = GameFiles.Normalize(directory);
        return [.. new DirectoryInfo(this.FullPath(folder)).EnumerateDirectories("*", Options)
            .Where(d => !IsLink(d))
            .Select(d => GameFiles.Combine(folder, d.Name))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>A symbolic link or junction (other reparse points, such as cloud or deduplicated files, are real files).</summary>
    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null;

    public Stream OpenRead(string path) =>
        new FileStream(this.FullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    public long Length(string path)
    {
        var info = new FileInfo(this.FullPath(path));
        return info.Exists ? info.Length : throw new FileNotFoundException($"File not found: {path}", path);
    }
}

/// <summary>
/// The game as it was before a pack changed it: each file the pack replaced read from the file holding its original
/// (the install's backup), the files and folders it added hidden, everything else as in <see cref="Game"/>. Paths
/// match whatever their case (Windows games under Proton keep Windows names).
/// </summary>
public sealed class PristineGameFiles : IGameFiles
{
    private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    private readonly Dictionary<string, string> _originals;
    private readonly HashSet<string> _added;
    private readonly List<string> _addedFolders;

    /// <param name="game">The game folder as it is now.</param>
    /// <param name="originals">Each file the pack replaced (relative path) → the file on disk holding its original.</param>
    /// <param name="added">Files the pack added (relative paths).</param>
    /// <param name="addedFolders">Folders the pack created (relative paths).</param>
    public PristineGameFiles(IGameFiles game, IReadOnlyDictionary<string, string> originals, IEnumerable<string> added, IEnumerable<string>? addedFolders = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentNullException.ThrowIfNull(added);
        Game = game;
        _originals = originals.ToDictionary(p => GameFiles.Normalize(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        _added = added.Select(GameFiles.Normalize).Where(p => !_originals.ContainsKey(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _addedFolders = [.. (addedFolders ?? []).Select(GameFiles.Normalize).Where(p => p.Length > 0)];
    }

    public IGameFiles Game { get; }

    public string Root => Game.Root;

    public bool FileExists(string path)
    {
        var normalized = GameFiles.Normalize(path);
        return _originals.TryGetValue(normalized, out var original) ? File.Exists(original)
            : !Hidden(normalized) && Game.FileExists(normalized);
    }

    public bool DirectoryExists(string path)
    {
        var normalized = GameFiles.Normalize(path);
        return !HiddenFolder(normalized) && (Game.DirectoryExists(normalized) || _originals.Keys.Any(o => o.Length > normalized.Length && GameFiles.IsUnder(o, normalized, Comparison)));
    }

    public IReadOnlyList<string> Files(string directory, bool recursive = false)
    {
        var folder = GameFiles.Normalize(directory);
        var own = _originals.Keys.Where(o => GameFiles.IsUnder(o, folder, Comparison) && o.Length > folder.Length && (recursive || GameFiles.ParentOf(o).Equals(folder, Comparison))).ToList();
        if (HiddenFolder(folder) || (!Game.DirectoryExists(folder) && own.Count == 0))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        }

        var listed = Game.DirectoryExists(folder) ? Game.Files(folder, recursive).Where(f => !Hidden(f)).ToList() : [];
        var names = listed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        listed.AddRange(own.Where(names.Add));
        listed.Sort(StringComparer.Ordinal);
        return listed;
    }

    public IReadOnlyList<string> Directories(string directory)
    {
        var folder = GameFiles.Normalize(directory);
        if (!DirectoryExists(folder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        }

        var listed = Game.DirectoryExists(folder) ? Game.Directories(folder).Where(d => !HiddenFolder(d)).ToList() : [];
        var names = listed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var original in _originals.Keys.Where(o => o.Length > folder.Length && GameFiles.IsUnder(o, folder, Comparison)))
        {
            var rest = folder.Length == 0 ? original : original[(folder.Length + 1)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && names.Add(GameFiles.Combine(folder, rest[..slash])))
            {
                listed.Add(GameFiles.Combine(folder, rest[..slash]));
            }
        }

        listed.Sort(StringComparer.Ordinal);
        return listed;
    }

    public Stream OpenRead(string path)
    {
        var normalized = GameFiles.Normalize(path);
        if (_originals.TryGetValue(normalized, out var original))
        {
            return new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }

        return Hidden(normalized) ? throw new FileNotFoundException($"File not found: {normalized}", normalized) : Game.OpenRead(normalized);
    }

    public long Length(string path)
    {
        var normalized = GameFiles.Normalize(path);
        if (_originals.TryGetValue(normalized, out var original))
        {
            var info = new FileInfo(original);
            return info.Exists ? info.Length : throw new FileNotFoundException($"The original of {normalized} is missing: {original}", original);
        }

        return Hidden(normalized) ? throw new FileNotFoundException($"File not found: {normalized}", normalized) : Game.Length(normalized);
    }

    private bool Hidden(string path) => _added.Contains(path) || _addedFolders.Any(f => GameFiles.IsUnder(path, f, Comparison));

    private bool HiddenFolder(string path) => path.Length > 0 && _addedFolders.Any(f => GameFiles.IsUnder(path, f, Comparison));
}
