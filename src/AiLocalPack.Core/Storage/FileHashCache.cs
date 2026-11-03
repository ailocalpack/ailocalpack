using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Storage;

/// <summary>
/// SHA-256 of files, remembered by full path with the file's size and last write time: a file whose size and time are
/// unchanged is not read again (game files are large; fingerprints and versions are re-checked on every start). Kept in
/// memory, optionally persisted to a JSON file (<see cref="Load"/> / <see cref="Save"/>, best effort: a broken or
/// unreadable cache file just starts empty). Thread-safe.
/// </summary>
public sealed class FileHashCache
{
    private const int SchemaVersion = 1;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly string? _file;
    private readonly ILogger _logger;
    private bool _dirty;

    /// <param name="file">Where <see cref="Save"/> writes; <c>null</c> = memory only.</param>
    public FileHashCache(string? file = null, ILogger<FileHashCache>? logger = null)
    {
        _file = file;
        _logger = logger ?? NullLogger<FileHashCache>.Instance;
    }

    /// <summary>Number of remembered files.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>A cache backed by <paramref name="file"/>, with its entries if the file exists and is readable.</summary>
    public static FileHashCache Load(string file, ILogger<FileHashCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var cache = new FileHashCache(file, logger);
        try
        {
            if (!File.Exists(file))
            {
                return cache;
            }

            var json = JsonSerializer.Deserialize(File.ReadAllBytes(file), FileHashCacheJsonContext.Default.FileHashCacheFile);
            if (json?.SchemaVersion != SchemaVersion || json.Files is null)
            {
                return cache;
            }

            foreach (var entry in json.Files)
            {
                if (entry is { Path: { Length: > 0 } path, Sha256: { Length: 64 } hash } && Path.IsPathFullyQualified(path))
                {
                    cache._entries[path] = new Entry(entry.Size, entry.Modified, hash);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            cache._logger.LogWarning(e, "File hash cache {File} unreadable, starting empty", file);
        }

        return cache;
    }

    /// <summary>
    /// SHA-256 of <paramref name="path"/> as 64 lowercase hex characters. Read only when the file is new to the cache or its
    /// size or last write time changed. Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> when
    /// the file is missing or unreadable, <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public string Hash(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var before = Stat(full);
        lock (_lock)
        {
            if (_entries.TryGetValue(full, out var known) && known.Size == before.Size && known.Modified == before.Modified)
            {
                return known.Sha256;
            }
        }

        var hash = Compute(full, cancellationToken);
        if (Stat(full) == before)
        {
            // Not remembered when the file changed while it was read: the hash may match neither version.
            lock (_lock)
            {
                _entries[full] = new Entry(before.Size, before.Modified, hash);
                _dirty = true;
            }
        }

        return hash;
    }

    /// <summary>
    /// Writes the cache file (atomically) if anything changed, dropping files that no longer exist. Does nothing for a
    /// memory-only cache. A failed write is logged, never thrown.
    /// </summary>
    public void Save()
    {
        if (_file is null)
        {
            return;
        }

        FileHashCacheFile json;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            foreach (var gone in _entries.Keys.Where(p => !File.Exists(p)).ToList())
            {
                _entries.Remove(gone);
            }

            json = new FileHashCacheFile(SchemaVersion, [
                .. _entries
                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new FileHashEntryJson(e.Key, e.Value.Size, e.Value.Modified, e.Value.Sha256)),
            ]);
            _dirty = false;
        }

        try
        {
            AtomicFile.Write(_file, s => JsonSerializer.Serialize(s, json, FileHashCacheJsonContext.Default.FileHashCacheFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Could not save file hash cache {File}", _file);
            lock (_lock)
            {
                _dirty = true;
            }
        }
    }

    private static (long Size, DateTime Modified) Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : throw new FileNotFoundException("File not found.", path);
    }

    private static string Compute(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private sealed record Entry(long Size, DateTime Modified, string Sha256);
}

internal sealed record FileHashCacheFile(int SchemaVersion, IReadOnlyList<FileHashEntryJson?>? Files);

internal sealed record FileHashEntryJson(string? Path, long Size, DateTime Modified, string? Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FileHashCacheFile))]
internal sealed partial class FileHashCacheJsonContext : JsonSerializerContext;
