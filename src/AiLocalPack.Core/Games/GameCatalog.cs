using System.Globalization;
using System.Text.Json;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Games;

/// <summary>
/// The player's games (Steam + manual), persisted as <c>games.json</c> in <see cref="AppPaths.Data"/> so identity,
/// version and detected details survive restarts and missing drives. Best effort like settings: a corrupted file is
/// moved aside as <c>games.json.&lt;utc time&gt;.bak</c> and the catalog starts empty; an unreadable one gives an empty
/// catalog that is never written this session (so manual games aren't overwritten); a failed save is logged and the
/// catalog keeps working in memory. Thread-safe.
/// </summary>
public sealed class GameCatalog
{
    public const int CurrentSchemaVersion = 1;

    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _directoryExists;
    private IReadOnlyList<Game> _games = [];
    private bool _canSave = true;

    public GameCatalog(AppPaths paths, ILogger<GameCatalog> logger, TimeProvider time)
        : this(paths?.GamesFile!, logger, time, Directory.Exists)
    {
    }

    internal GameCatalog(string filePath, ILogger logger, TimeProvider time, Func<string, bool> directoryExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(directoryExists);
        FilePath = filePath;
        _logger = logger;
        _time = time;
        _directoryExists = directoryExists;
    }

    public string FilePath { get; }

    /// <summary>What the last <see cref="Load"/> found; <see cref="GameCatalogLoadOutcome.NotLoaded"/> before it.</summary>
    public GameCatalogLoadOutcome LoadOutcome { get; private set; }

    /// <summary>Where a corrupted file was moved by the last <see cref="Load"/>; <c>null</c> otherwise or when the move failed.</summary>
    public string? CorruptedBackupPath { get; private set; }

    /// <summary>All games, sorted by name. Empty until <see cref="Load"/>.</summary>
    public IReadOnlyList<Game> Games
    {
        get
        {
            lock (_lock)
            {
                return _games;
            }
        }
    }

    /// <summary>Raised after the catalog changed (outside the lock), with the new list and the added / removed / version-changed games.</summary>
    public event EventHandler<GameCatalogChangedEventArgs>? Changed;

    public Game? Find(GameId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Games.FirstOrDefault(g => g.Id == id);
    }

    /// <summary>
    /// Reads <c>games.json</c>. Missing → empty. Broken entries are skipped with a warning. Raises no event.
    /// <see cref="LoadOutcome"/> tells what was found.
    /// </summary>
    public IReadOnlyList<Game> Load()
    {
        lock (_lock)
        {
            CorruptedBackupPath = null;
            _games = GameCatalogMerge.Sort(Read());
            return _games;
        }
    }

    /// <summary>Merges a fresh Steam scan (see <see cref="GameCatalogMerge.Merge"/>), saves, and returns what changed.</summary>
    public IReadOnlyList<GameChange> Refresh(SteamScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return Apply(games => GameCatalogMerge.Merge(games, scan, _directoryExists));
    }

    /// <summary>Adds a game the player picked by hand. Throws if it is not a manual game or its ID is already in the catalog.</summary>
    public GameAdded AddManual(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Source != GameSource.Manual)
        {
            throw new ArgumentException("Only manual games are added by hand; Steam games come from the scan.", nameof(game));
        }

        var added = new GameAdded(game);
        Apply(games => games.Any(g => g.Id == game.Id)
            ? throw new InvalidOperationException($"{game.Id} is already in the catalog.")
            : (GameCatalogMerge.Sort([.. games, game]), [added]));
        return added;
    }

    /// <summary>Removes a manual game (its folder and installed pack stay on disk). Steam games can't be removed. False if not found.</summary>
    public bool RemoveManual(GameId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var changes = Apply(games =>
        {
            var game = games.FirstOrDefault(g => g.Id == id);
            if (game is null)
            {
                return (games, []);
            }

            return game.Source == GameSource.Manual
                ? ([.. games.Where(g => g != game)], [new GameRemoved(game)])
                : throw new InvalidOperationException($"{id} is a Steam game; it leaves the catalog when uninstalled in Steam.");
        });
        return changes.Count > 0;
    }

    /// <summary>
    /// Removes the Steam games of a library folder the player removed from the list (Settings → Libraries), so they don't
    /// stay as unavailable. Their folders and installed packs stay on disk. Returns how many games left.
    /// </summary>
    public int RemoveSteamLibrary(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        var changes = Apply(games =>
        {
            var gone = games.Where(g => g.Source == GameSource.Steam && PathNames.Same(g.LibraryPath, libraryPath)).ToList();
            return gone.Count == 0
                ? (games, [])
                : ([.. games.Except(gone)], [.. gone.Select(g => new GameRemoved(g))]);
        });
        return changes.Count;
    }

    /// <summary>Replaces details of a game found by later steps (engine, anti-cheat, file-hash version) and saves. False if not found.</summary>
    public bool Update(GameId id, Func<Game, Game> change)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(change);
        var found = false;
        Apply(games =>
        {
            var list = games.ToList();
            var index = list.FindIndex(g => g.Id == id);
            if (index < 0)
            {
                return (games, []);
            }

            found = true;
            var before = list[index];
            var after = change(before) ?? throw new InvalidOperationException("A game change returned null.");
            if (after.Id != id || after.Source != before.Source)
            {
                throw new InvalidOperationException("A game change can't change the game's ID or source.");
            }

            list[index] = after;
            IReadOnlyList<GameChange> changes = before.Version is not null && after.Version is not null && before.Version != after.Version
                ? [new GameVersionChanged(after, before.Version)]
                : [];
            return (GameCatalogMerge.Sort(list), changes);
        });
        return found;
    }

    private IReadOnlyList<GameChange> Apply(
        Func<IReadOnlyList<Game>, (IReadOnlyList<Game> Games, IReadOnlyList<GameChange> Changes)> change)
    {
        IReadOnlyList<Game> games;
        IReadOnlyList<GameChange> changes;
        lock (_lock)
        {
            (games, changes) = change(_games);
            if (changes.Count == 0 && games.SequenceEqual(_games))
            {
                return changes;
            }

            _games = games;
            TrySave(games);
        }

        Changed?.Invoke(this, new GameCatalogChangedEventArgs(games, changes));
        return changes;
    }

    private List<Game> Read()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogInformation("No game catalog at {Path}; starting empty", FilePath);
            LoadOutcome = GameCatalogLoadOutcome.Missing;
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _canSave = false;
            _logger.LogError(e, "Can't read game catalog {Path}; starting empty and not saving this session", FilePath);
            LoadOutcome = GameCatalogLoadOutcome.Unreadable;
            return [];
        }

        GameCatalogFile file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, GameCatalogJsonContext.Default.GameCatalogFile)
                ?? throw new JsonException("The game catalog is empty.");
        }
        catch (JsonException e)
        {
            var backup = BackUpCorrupted();
            _logger.LogWarning(e, "Game catalog {Path} is corrupted; moved it to {Backup} and starting empty", FilePath, backup);
            LoadOutcome = GameCatalogLoadOutcome.Corrupted;
            CorruptedBackupPath = backup;
            return [];
        }

        if (file.SchemaVersion > CurrentSchemaVersion)
        {
            _logger.LogWarning(
                "Game catalog {Path} has schema version {Version}, newer than {Current}; reading what this version knows",
                FilePath, file.SchemaVersion, CurrentSchemaVersion);
        }

        LoadOutcome = GameCatalogLoadOutcome.Loaded;
        var games = new List<Game>();
        var ids = new HashSet<GameId>();
        foreach (var entry in file.Games ?? [])
        {
            if (FromJson(entry) is { } game && ids.Add(game.Id))
            {
                games.Add(game);
            }
        }

        return games;
    }

    private Game? FromJson(GameEntryJson? entry)
    {
        try
        {
            if (entry is null
                || !GameId.TryParse(entry.Id, out var id)
                || string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.InstallPath)
                || !Enum.TryParse<GameSource>(entry.Source, ignoreCase: true, out var source)
                || !Enum.IsDefined(source)
                || (source == GameSource.Steam) != (id.Kind == GameIdKind.Steam))
            {
                throw new FormatException("missing or invalid id, name, source or install path");
            }

            var version = entry.Version switch
            {
                null => null,
                { BuildId: { } build, Files: null } => GameVersion.FromBuildId(build),
                { BuildId: null, Files: { } files } => GameVersion.FromFileHashes(files),
                _ => throw new FormatException("a version has exactly one of buildId and files"),
            };

            return new Game(
                id,
                entry.Name,
                source,
                entry.InstallPath,
                entry.LibraryPath,
                version,
                entry.Engine,
                entry.AntiCheat,
                entry.LastPlayed,
                entry.Available);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            _logger.LogWarning("Skipping game catalog entry {Id}: {Reason}", entry?.Id, e.Message);
            return null;
        }
    }

    private static GameEntryJson ToJson(Game game) => new(
        game.Id.ToString(),
        game.Name,
        game.Source.ToString().ToLowerInvariant(),
        game.InstallPath,
        game.LibraryPath,
        game.Version is null ? null : new GameVersionJson(game.Version.BuildId, game.Version.FileHashes),
        game.Engine,
        game.AntiCheat,
        game.LastPlayed?.ToUniversalTime(),
        game.IsAvailable);

    private string? BackUpCorrupted()
    {
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = $"{FilePath}.{stamp}.bak";
        try
        {
            File.Move(FilePath, backup, overwrite: true);
            return backup;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't back up corrupted game catalog {Path}", FilePath);
            return null;
        }
    }

    private void TrySave(IReadOnlyList<Game> games)
    {
        if (!_canSave)
        {
            return;
        }

        var file = new GameCatalogFile(CurrentSchemaVersion, [.. games.Select(ToJson)]);
        try
        {
            AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, file, GameCatalogJsonContext.Default.GameCatalogFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't write game catalog {Path}; keeping changes in memory", FilePath);
        }
    }
}
