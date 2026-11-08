using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Steam;

/// <summary>
/// Watches the <c>steamapps</c> folders of the Steam libraries for app manifest changes (<c>appmanifest_*.acf</c> written,
/// created, renamed or deleted: an update, an install, a file check), so the tray app re-checks installed packs soon after
/// (task 39). <see cref="Watch"/> replaces the watched set (after each library scan). Never throws for disk state: a
/// folder that can't be watched is logged and skipped. <see cref="Changed"/> comes on a pool thread, possibly many times
/// per update.
/// </summary>
public sealed class SteamManifestWatcher : IDisposable
{
    public const string Filter = "appmanifest_*.acf";

    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private bool _disposed;

    public SteamManifestWatcher(ILogger<SteamManifestWatcher>? logger = null) =>
        _logger = logger ?? NullLogger<SteamManifestWatcher>.Instance;

    /// <summary>A manifest changed (full path of the file).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>The folders watched now.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_lock)
            {
                return [.. _watchers.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Watches exactly these <c>steamapps</c> folders (missing ones skipped).</summary>
    public void Watch(IEnumerable<string> steamappsFolders)
    {
        ArgumentNullException.ThrowIfNull(steamappsFolders);
        var wanted = steamappsFolders.Select(Path.GetFullPath).Where(Directory.Exists).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var gone in _watchers.Keys.Where(f => !wanted.Contains(f)).ToList())
            {
                _watchers[gone].Dispose();
                _watchers.Remove(gone);
            }

            foreach (var folder in wanted.Where(f => !_watchers.ContainsKey(f)))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder, Filter)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Changed += OnEvent;
                    watcher.Created += OnEvent;
                    watcher.Deleted += OnEvent;
                    watcher.Renamed += OnEvent;
                    watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Watching {Folder} failed", folder);
                    watcher.EnableRaisingEvents = true;
                    _watchers[folder] = watcher;
                }
                catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    _logger.LogWarning(e, "Can't watch {Folder} for Steam app manifest changes", folder);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, e.FullPath);
}
