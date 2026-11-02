namespace AiLocalPack.Core.Games;

/// <summary>
/// The installed build of a game: a store build ID (Steam <c>buildid</c>) or, for manual games, a file-hash set
/// (relative path with '/' → SHA-256 lowercase hex, of the files packs touch). Packs can break between versions.
/// Two versions are equal when they are the same kind with the same content.
/// </summary>
public sealed class GameVersion : IEquatable<GameVersion>
{
    private GameVersion(string? buildId, SortedDictionary<string, string>? fileHashes)
    {
        BuildId = buildId;
        FileHashes = fileHashes;
    }

    /// <summary>Store build ID, or <c>null</c> for a file-hash version.</summary>
    public string? BuildId { get; }

    /// <summary>Relative path ('/' separators, ordinal order) → SHA-256 hex, or <c>null</c> for a build-ID version.</summary>
    public IReadOnlyDictionary<string, string>? FileHashes { get; }

    public static GameVersion FromBuildId(string buildId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        return new GameVersion(buildId, null);
    }

    /// <summary>A file-hash version. Backslashes in paths become '/'; hashes must be 64 lowercase hex characters.</summary>
    public static GameVersion FromFileHashes(IEnumerable<KeyValuePair<string, string>> fileHashes)
    {
        ArgumentNullException.ThrowIfNull(fileHashes);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, hash) in fileHashes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(fileHashes));
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw new ArgumentException($"Not a relative file path: '{path}'.", nameof(fileHashes));
            }

            if (hash is null || hash.Length != GameId.FingerprintLength || !hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                throw new ArgumentException($"Not a SHA-256 lowercase hex hash for '{path}': '{hash}'.", nameof(fileHashes));
            }

            if (!files.TryAdd(normalized, hash))
            {
                throw new ArgumentException($"File listed twice: '{normalized}'.", nameof(fileHashes));
            }
        }

        return files.Count > 0
            ? new GameVersion(null, files)
            : throw new ArgumentException("A file-hash version needs at least one file.", nameof(fileHashes));
    }

    public bool Equals(GameVersion? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (BuildId is not null
                ? BuildId == other.BuildId
                : other.FileHashes is not null
                  && FileHashes!.Count == other.FileHashes.Count
                  && FileHashes.All(f => other.FileHashes.TryGetValue(f.Key, out var h) && h == f.Value)));

    public override bool Equals(object? obj) => Equals(obj as GameVersion);

    public override int GetHashCode()
    {
        if (BuildId is not null)
        {
            return HashCode.Combine(1, BuildId);
        }

        var hash = new HashCode();
        foreach (var (path, value) in FileHashes!)
        {
            hash.Add(path);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => BuildId is not null ? $"build {BuildId}" : $"{FileHashes!.Count} file hashes";

    public static bool operator ==(GameVersion? left, GameVersion? right) => Equals(left, right);

    public static bool operator !=(GameVersion? left, GameVersion? right) => !Equals(left, right);
}
