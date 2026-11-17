using System.Text.Json;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Install;

/// <summary>
/// A folder whose files a game writes when it runs: the game folder itself (<see cref="Id"/> empty) or one of the
/// engine's data folders (Unity's <c>LocalLow/&lt;company&gt;/&lt;product&gt;</c>, Unreal's <c>Saved/</c>…).
/// </summary>
/// <param name="Id">A short name (a slug), part of the keys of its files (<see cref="GameWriteGuard.Key"/>); empty = the game folder.</param>
/// <param name="Path">The folder (absolute; it may not exist yet).</param>
public sealed record GameWriteRoot(string Id, string Path)
{
    /// <summary>The game folder.</summary>
    public static GameWriteRoot Game(string path) => new(string.Empty, path);
}

/// <summary>What <see cref="GameWriteGuard.Undo"/> did.</summary>
/// <param name="Undone">Files deleted or put back.</param>
/// <param name="Left">Keys of files the game changed or deleted that have no backup, so stay as the game left them.</param>
/// <param name="Written">Keys of files the game changed or deleted (backed up ahead of its next runs).</param>
public sealed record GameWriteUndo(int Undone, IReadOnlyList<string> Left, IReadOnlyList<string> Written)
{
    public static GameWriteUndo None { get; } = new(0, [], []);
}

/// <summary>
/// Undoes what a game writes when it runs (_Live game folder_ in <c>docs/generation.md</c>): caches it builds from the
/// files it read (Barony's compiled books), settings, logs, saves. <see cref="Take"/> notes every file of each
/// <see cref="GameWriteRoot"/> (path, size, last write time; links never followed) into <c>snapshot.json</c> of its
/// folder and backs up into <c>saved/</c> the files the game wrote in earlier runs (any size up to
/// <see cref="MaxKnownFile"/>) and every small file (not media, images, fonts or programs; at most
/// <see cref="MaxSmallFile"/> each and <see cref="SmallFilesBudget"/> copied in all, text files and the smallest first;
/// a clone costs nothing and isn't counted). <see cref="Undo"/> deletes the files and folders the game added (a data
/// folder that didn't exist, whole), puts the changed and deleted ones back from the backups (their old write time
/// kept) and lists those it can't. The snapshot is on disk, so a crash is undone on the next start (<see cref="Load"/>).
/// Best effort: never throws for disk state.
/// </summary>
public sealed class GameWriteGuard
{
    /// <summary>A small file is backed up ahead of a run when at most this size.</summary>
    public const long MaxSmallFile = 16L * 1024 * 1024;

    /// <summary>At most this much is copied for small files per run (clones are free).</summary>
    public const long SmallFilesBudget = 256L * 1024 * 1024;

    /// <summary>A file the game is known to write is backed up when at most this size.</summary>
    public const long MaxKnownFile = 1024L * 1024 * 1024;

    private const string SnapshotName = "snapshot.json";
    private const string SavedName = "saved";

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images.
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".dds", ".ktx", ".ktx2", ".psd", ".gif", ".tif", ".tiff", ".exr", ".hdr", ".ico", ".icns", ".svg",

        // Programs and libraries (a game doesn't rewrite its own code).
        ".exe", ".dll", ".so", ".dylib", ".pdb", ".a", ".lib", ".node", ".sys", ".msi",

        // Fonts.
        ".ttf", ".otf", ".ttc", ".woff", ".woff2",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".csv", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".log", ".lua", ".js", ".rpy", ".tscn", ".tres", ".plist", ".sav", ".save",
    };

    private readonly string _folder;
    private readonly IReadOnlyList<RootState> _roots;

    private GameWriteGuard(string folder, IReadOnlyList<RootState> roots, string? version)
    {
        _folder = folder;
        _roots = roots;
        Version = version;
    }

    /// <summary>The game's version when the guard was taken (<see cref="GameVersionKey"/>), when known.</summary>
    public string? Version { get; }

    /// <summary>
    /// A key of a game's version (Steam build ID, else a hash of the file-hash set), or <c>null</c> when unknown: an
    /// update between <see cref="Take"/> and <see cref="Undo"/> changes the game's own files, which the undo then leaves.
    /// </summary>
    public static string? GameVersionKey(Games.GameVersion? version)
    {
        if (version?.BuildId is { Length: > 0 } build)
        {
            return "build:" + build;
        }

        if (version?.FileHashes is { Count: > 0 } files)
        {
            var text = string.Join('\n', files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"));
            return "files:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        }

        return null;
    }

    /// <summary>The key of a file of a root in the known writes (<c>&lt;path&gt;</c> in the game folder, else <c>~&lt;root&gt;/&lt;path&gt;</c>).</summary>
    public static string Key(string rootId, string relative) => rootId.Length == 0 ? relative : $"~{rootId}/{relative}";

    /// <summary>Notes the roots as they are and backs files up into <paramref name="folder"/> (made; a trial's own folder).</summary>
    /// <param name="known">Keys of files the game wrote in earlier runs (<see cref="Key"/>).</param>
    /// <param name="cloner">Clones a backup where the file system can; <c>null</c> = copies.</param>
    /// <param name="version">The game's version now (<see cref="GameVersionKey"/>), kept with the snapshot.</param>
    public static GameWriteGuard Take(string folder, IReadOnlyList<GameWriteRoot> roots, IEnumerable<string> known, IFileCloner? cloner = null, Action<string>? log = null, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(known);
        var known2 = known.ToHashSet(StringComparer.Ordinal);
        var states = roots.Select((r, i) => List(r, $"r{i}")).ToList();
        var guard = new GameWriteGuard(Path.GetFullPath(folder), states, version);
        Directory.CreateDirectory(guard._folder);
        guard.WriteSnapshot();
        var (saved, copied) = guard.Backup(known2, cloner);
        log?.Invoke($"Game writes: {states.Sum(s => s.Files.Count)} files noted in {states.Count} folder{(states.Count == 1 ? "" : "s")}, {saved} backed up ({copied:N0} bytes copied).");
        return guard;
    }

    /// <summary>The guard a crash left in <paramref name="folder"/>, or <c>null</c> (none, or unreadable).</summary>
    public static GameWriteGuard? Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var path = Path.Combine(folder, SnapshotName);
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1)
            {
                return null;
            }

            var roots = new List<RootState>();
            foreach (var item in root.GetProperty("roots").EnumerateArray())
            {
                var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
                foreach (var file in item.GetProperty("files").EnumerateArray())
                {
                    files[file[0].GetString()!] = new FileState(file[1].GetInt64(), new DateTime(file[2].GetInt64(), DateTimeKind.Utc));
                }

                roots.Add(new RootState(
                    new GameWriteRoot(item.GetProperty("id").GetString()!, item.GetProperty("path").GetString()!),
                    item.GetProperty("saved").GetString()!,
                    item.GetProperty("existed").GetBoolean(),
                    files,
                    [.. item.GetProperty("folders").EnumerateArray().Select(f => f.GetString()!)]));
            }

            return new GameWriteGuard(Path.GetFullPath(folder), roots, root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Puts every root back as <see cref="Take"/> saw it (safe to repeat; the backups stay for the next run).</summary>
    /// <param name="keep">Keys of files left as they are, whatever the game did to them (an uninstall keeps the player's saves, <see cref="GameSaves.IsSave"/>).</param>
    /// <param name="skipGameFolder">Leaves the game folder alone (the game was updated since: its own files changed).</param>
    /// <param name="onlyCacheLikeAdded">
    /// Deletes an added file only when it is small and could be a cache, a setting or a log (at most
    /// <see cref="MaxSmallFile"/>, not media, images, fonts or programs): an uninstall long after the install must not
    /// take content the game got since (a DLC's files).
    /// </param>
    public GameWriteUndo Undo(Action<string>? log = null, Func<string, bool>? keep = null, bool skipGameFolder = false, bool onlyCacheLikeAdded = false)
    {
        var undone = 0;
        var left = new List<string>();
        var written = new List<string>();
        foreach (var root in _roots)
        {
            var path = root.Root.Path;
            if (skipGameFolder && root.Root.Id.Length == 0)
            {
                continue;
            }

            if (!root.Existed && keep is null)
            {
                // The run made the whole folder: it goes.
                if (Directory.Exists(path) && TryDeleteTree(path))
                {
                    undone++;
                }

                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            var (now, nowFolders) = Walk(path);
            foreach (var (relative, state) in now)
            {
                if (keep?.Invoke(Key(root.Root.Id, relative)) == true)
                {
                    continue;
                }

                if (!root.Files.TryGetValue(relative, out var before))
                {
                    if (!onlyCacheLikeAdded || IsCacheLike(relative, state.Length))
                    {
                        undone += Try(() => File.Delete(Full(path, relative)));
                    }
                }
                else if (state != before)
                {
                    written.Add(Key(root.Root.Id, relative));
                    undone += Restore(root, relative, before, left);
                }
            }

            foreach (var (relative, before) in root.Files.Where(f => !now.ContainsKey(f.Key) && keep?.Invoke(Key(root.Root.Id, f.Key)) != true))
            {
                written.Add(Key(root.Root.Id, relative));
                undone += Restore(root, relative, before, left);
            }

            foreach (var folder in nowFolders.Where(f => !root.Folders.Contains(f)).OrderByDescending(f => f.Length))
            {
                Try(() => Directory.Delete(Full(path, folder)));
            }

            foreach (var folder in root.Folders.Where(f => !nowFolders.Contains(f)))
            {
                Try(() => Directory.CreateDirectory(Full(path, folder)));
            }

            if (!root.Existed && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Try(() => Directory.Delete(path));
            }
        }

        if (undone > 0)
        {
            log?.Invoke($"Game writes: undid {undone} file{(undone == 1 ? "" : "s")} the game wrote.");
        }

        if (left.Count > 0)
        {
            log?.Invoke($"Game writes: the game changed {left.Count} file{(left.Count == 1 ? "" : "s")} that had no backup, so they stay as it left them: {string.Join(", ", left.Take(10))}{(left.Count > 10 ? ", …" : "")}.");
        }

        return new GameWriteUndo(undone, left, written);
    }

    private static bool IsCacheLike(string relative, long length) =>
        length <= MaxSmallFile && !SkippedExtensions.Contains(Path.GetExtension(relative)) && !MediaFiles.IsMedia(relative);

    private (int Saved, long Copied) Backup(HashSet<string> known, IFileCloner? cloner)
    {
        var saved = 0;
        long copied = 0;
        var small = new List<(RootState Root, string Relative, FileState State)>();
        foreach (var root in _roots)
        {
            foreach (var (relative, state) in root.Files)
            {
                if (known.Contains(Key(root.Root.Id, relative)) && state.Length <= MaxKnownFile)
                {
                    if (Save(root, relative, cloner) is { } cost)
                    {
                        saved++;
                        copied += cost;
                    }
                }
                else if (state.Length <= MaxSmallFile && !SkippedExtensions.Contains(Path.GetExtension(relative)) && !MediaFiles.IsMedia(relative))
                {
                    small.Add((root, relative, state));
                }
            }
        }

        long budget = 0;
        foreach (var (root, relative, state) in small.OrderBy(s => TextExtensions.Contains(Path.GetExtension(s.Relative)) ? 0 : 1).ThenBy(s => s.State.Length).ThenBy(s => s.Relative, StringComparer.Ordinal))
        {
            if (budget + state.Length > SmallFilesBudget && cloner is null)
            {
                break;
            }

            if (Save(root, relative, cloner, budget + state.Length > SmallFilesBudget) is { } cost)
            {
                saved++;
                copied += cost;
                budget += cost;
            }
        }

        return (saved, copied);
    }

    /// <summary>Backs one file up; its copied bytes (0 for a clone), or <c>null</c> when it couldn't be (or only a copy would do and <paramref name="cloneOnly"/>).</summary>
    private long? Save(RootState root, string relative, IFileCloner? cloner, bool cloneOnly = false)
    {
        var source = Full(root.Root.Path, relative);
        var target = Full(Path.Combine(_folder, SavedName, root.Saved), relative);
        if (File.Exists(target))
        {
            return 0;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (cloner?.TryClone(source, target) == true)
            {
                return 0;
            }

            if (cloneOnly)
            {
                return null;
            }

            File.Copy(source, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            return new FileInfo(target).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Try(() => File.Delete(target));
            return null;
        }
    }

    private int Restore(RootState root, string relative, FileState before, List<string> left)
    {
        var saved = Full(Path.Combine(_folder, SavedName, root.Saved), relative);
        if (!File.Exists(saved))
        {
            left.Add(Key(root.Root.Id, relative));
            return 0;
        }

        var target = Full(root.Root.Path, relative);
        var done = Try(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(saved, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, before.Written);
        });
        if (done == 0)
        {
            left.Add(Key(root.Root.Id, relative));
        }

        return done;
    }

    private void WriteSnapshot()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            if (Version is { } version)
            {
                writer.WriteString("version", version);
            }

            writer.WriteStartArray("roots");
            foreach (var root in _roots)
            {
                writer.WriteStartObject();
                writer.WriteString("id", root.Root.Id);
                writer.WriteString("path", root.Root.Path);
                writer.WriteString("saved", root.Saved);
                writer.WriteBoolean("existed", root.Existed);
                writer.WriteStartArray("files");
                foreach (var (relative, state) in root.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(relative);
                    writer.WriteNumberValue(state.Length);
                    writer.WriteNumberValue(state.Written.Ticks);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("folders");
                foreach (var folder in root.Folders.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(folder);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(Path.Combine(_folder, SnapshotName), buffer.ToArray());
    }

    private static RootState List(GameWriteRoot root, string saved)
    {
        var path = Path.GetFullPath(root.Path);
        var existed = Directory.Exists(path);
        var (files, folders) = existed ? Walk(path) : ([], []);
        return new RootState(root with { Path = path }, saved, existed, files, folders);
    }

    /// <summary>Every file and folder under <paramref name="folder"/> ('/'-separated), links neither listed nor followed.</summary>
    private static (Dictionary<string, FileState> Files, HashSet<string> Folders) Walk(string folder)
    {
        var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(DirectoryInfo Dir, string Relative)>();
        pending.Push((new DirectoryInfo(folder), string.Empty));
        while (pending.TryPop(out var current))
        {
            FileSystemInfo[] entries;
            try
            {
                entries = current.Dir.GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null)
                {
                    continue;
                }

                var relative = current.Relative.Length == 0 ? entry.Name : $"{current.Relative}/{entry.Name}";
                if (entry is DirectoryInfo dir)
                {
                    folders.Add(relative);
                    pending.Push((dir, relative));
                }
                else if (entry is FileInfo file)
                {
                    files[relative] = new FileState(file.Length, file.LastWriteTimeUtc);
                }
            }
        }

        return (files, folders);
    }

    private static bool TryDeleteTree(string path)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo dir && entry.LinkTarget is null)
                {
                    TryDeleteTree(dir.FullName);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }

            Directory.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static int Try(Action action)
    {
        try
        {
            action();
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private readonly record struct FileState(long Length, DateTime Written);

    /// <param name="Saved">The root's folder under <c>saved/</c>.</param>
    /// <param name="Existed">The folder was there when the guard was taken.</param>
    private sealed record RootState(GameWriteRoot Root, string Saved, bool Existed, Dictionary<string, FileState> Files, HashSet<string> Folders);
}
