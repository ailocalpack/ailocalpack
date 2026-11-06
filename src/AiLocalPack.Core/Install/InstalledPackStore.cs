using System.Globalization;
using System.Text.Json;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Install;

/// <summary>
/// The installed packs (<c>&lt;data&gt;/installed-packs.json</c>), one per game. Loaded on first use. A corrupted
/// file is moved aside as <c>installed-packs.json.&lt;utc&gt;.bak</c> (the backups stay on disk); an unreadable one gives
/// an empty list that is never written this session (<see cref="Put"/> and <see cref="Remove"/> throw, so a transaction
/// rolls back instead of losing records). Writes are atomic. Thread-safe.
/// </summary>
public sealed class InstalledPackStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly Lock _lock = new();
    private readonly string _file;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private Dictionary<GameId, InstalledPack>? _packs;
    private bool _canSave = true;

    public InstalledPackStore(AppPaths paths, ILogger<InstalledPackStore>? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _file = paths.InstalledPacksFile;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a record was added, replaced or removed (outside the lock), with the game.</summary>
    public event EventHandler<GameId>? Changed;

    public IReadOnlyList<InstalledPack> All
    {
        get
        {
            lock (_lock)
            {
                return [.. Loaded().Values.OrderBy(p => p.Game.ToString(), StringComparer.Ordinal)];
            }
        }
    }

    public InstalledPack? Find(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_lock)
        {
            return Loaded().GetValueOrDefault(game);
        }
    }

    /// <summary>Adds or replaces the game's record and saves. Throws <see cref="IOException"/> when it can't be saved.</summary>
    public void Put(InstalledPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        lock (_lock)
        {
            var packs = new Dictionary<GameId, InstalledPack>(Loaded()) { [pack.Game] = pack };
            Save(packs);
            _packs = packs;
        }

        Changed?.Invoke(this, pack.Game);
    }

    /// <summary>Removes the game's record and saves. Throws <see cref="IOException"/> when it can't be saved.</summary>
    public void Remove(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_lock)
        {
            if (!Loaded().ContainsKey(game))
            {
                return;
            }

            var packs = new Dictionary<GameId, InstalledPack>(Loaded());
            packs.Remove(game);
            Save(packs);
            _packs = packs;
        }

        Changed?.Invoke(this, game);
    }

    private Dictionary<GameId, InstalledPack> Loaded() => _packs ??= Load();

    private Dictionary<GameId, InstalledPack> Load()
    {
        string text;
        try
        {
            if (!File.Exists(_file))
            {
                return [];
            }

            text = File.ReadAllText(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't read the installed packs {File}; nothing is saved this session", _file);
            _canSave = false;
            return [];
        }

        InstalledPacksFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, InstallJsonContext.Default.InstalledPacksFile);
        }
        catch (JsonException e)
        {
            MoveAside(e);
            return [];
        }

        if (file is null)
        {
            MoveAside(null);
            return [];
        }

        if (file.SchemaVersion > CurrentSchemaVersion)
        {
            // A newer app wrote it: read what we can, but never overwrite what this app doesn't understand.
            _logger.LogWarning("{File} has schema {Version}, newer than this app; it is read but not written", _file, file.SchemaVersion);
            _canSave = false;
        }

        return Entries(file);
    }

    private Dictionary<GameId, InstalledPack> Entries(InstalledPacksFile file)
    {
        var packs = new Dictionary<GameId, InstalledPack>();
        foreach (var entry in file.Packs ?? [])
        {
            if (FromJson(entry) is { } pack)
            {
                packs.TryAdd(pack.Game, pack);
            }
            else
            {
                _logger.LogWarning("Skipping a broken installed pack entry in {File}", _file);
            }
        }

        return packs;
    }

    private void MoveAside(Exception? e)
    {
        var backup = $"{_file}.{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
        _logger.LogError(e, "The installed packs file {File} is damaged; moved to {Backup}", _file, backup);
        try
        {
            File.Move(_file, backup, overwrite: true);
        }
        catch (Exception move) when (move is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(move, "Can't move {File} aside; nothing is saved this session", _file);
            _canSave = false;
        }
    }

    private void Save(Dictionary<GameId, InstalledPack> packs)
    {
        if (!_canSave)
        {
            throw new IOException($"The installed packs file {_file} couldn't be read, so it isn't written this session.");
        }

        var file = new InstalledPacksFile(
            CurrentSchemaVersion,
            [.. packs.Values.OrderBy(p => p.Game.ToString(), StringComparer.Ordinal).Select(ToJson)]);
        AtomicFile.WriteAllBytes(_file, JsonSerializer.SerializeToUtf8Bytes(file, InstallJsonContext.Default.InstalledPacksFile));
    }

    internal static InstalledPackJson ToJson(InstalledPack pack) => new(
        pack.Game.ToString(),
        pack.Language,
        pack.PackId,
        pack.Revision,
        pack.InstalledAt.ToUniversalTime(),
        pack.InstallPath,
        pack.Backup,
        [.. pack.Files.Select(f => new InstalledFileJson(f.Path, f.OriginalSha256, f.InstalledSha256))],
        pack.AddedFolders,
        pack.GeneratedFiles.Count > 0 ? pack.GeneratedFiles : null);

    internal static InstalledPack? FromJson(InstalledPackJson? json)
    {
        if (json is not { Game: { } gameText, Language: { Length: > 0 } language, Pack: { Length: > 0 } packId, InstallPath: { Length: > 0 } installPath, Backup: { Length: > 0 } backup }
            || !GameId.TryParse(gameText, out var game)
            || json.Revision < 1
            || !InstallPaths.IsSafeName(backup))
        {
            return null;
        }

        var files = new List<InstalledFile>();
        foreach (var file in json.Files ?? [])
        {
            if (file is not { Path: { } path, Installed: { } installed } || !InstallPaths.IsValid(path) || !IsSha(installed)
                || (file.Original is { } original && !IsSha(original)))
            {
                return null;
            }

            files.Add(new InstalledFile(path, file.Original, installed));
        }

        var folders = json.AddedFolders ?? [];
        var generated = json.GeneratedFiles ?? [];
        return folders.All(InstallPaths.IsValid) && generated.All(InstallPaths.IsValid)
            ? new InstalledPack(game, language, packId, json.Revision, json.InstalledAt, installPath, backup, files, [.. folders]) { GeneratedFiles = [.. generated] }
            : null;
    }

    private static bool IsSha(string text) => text.Length == 64 && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
