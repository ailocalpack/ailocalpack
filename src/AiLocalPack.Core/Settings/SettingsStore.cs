using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Saves are atomic (<see cref="AtomicFile"/>), so a crash mid-save keeps the
/// previous file. A file that can't be parsed is moved aside as <c>settings.json.&lt;time&gt;.bak</c> and the app starts
/// with defaults; nothing about settings ever stops the app from starting.
/// </summary>
public sealed class SettingsStore
{
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SettingsMigrator _migrator;

    public SettingsStore(AppPaths paths, ILogger<SettingsStore> logger, TimeProvider time)
        : this(paths?.SettingsFile!, logger, time, SettingsMigrator.Default)
    {
    }

    internal SettingsStore(string filePath, ILogger logger, TimeProvider time, SettingsMigrator migrator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(migrator);
        FilePath = filePath;
        _logger = logger;
        _time = time;
        _migrator = migrator;
    }

    public string FilePath { get; }

    /// <summary>The settings in effect: <see cref="AppSettings.Defaults"/> until <see cref="Load"/>.</summary>
    public AppSettings Current { get; private set; } = AppSettings.Defaults;

    /// <summary>Raised after <see cref="Update"/> saved a change; the argument is the new settings.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>
    /// Reads the file into <see cref="Current"/>. Missing → defaults, written out. Corrupted → backed up, defaults written.
    /// Older schema → migrated and rewritten. Unreadable (locked, no access) → defaults in memory only, the file is left alone.
    /// </summary>
    public AppSettings Load()
    {
        lock (_lock)
        {
            var (settings, rewrite) = Read();
            Current = settings;
            if (rewrite)
            {
                TrySave(settings);
            }

            return settings;
        }
    }

    /// <summary>Applies <paramref name="change"/> to <see cref="Current"/> and saves. If the save throws, <see cref="Current"/> stays as it was.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        lock (_lock)
        {
            updated = change(Current) ?? throw new InvalidOperationException("A settings change returned null.");
            if (updated == Current)
            {
                return updated;
            }

            Save(updated);
            Current = updated;
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    private (AppSettings Settings, bool Rewrite) Read()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogInformation("No settings file at {Path}; starting with defaults", FilePath);
            return (AppSettings.Defaults, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't read settings file {Path}; using defaults without touching it", FilePath);
            return (AppSettings.Defaults, false);
        }

        try
        {
            var document = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new JsonException("The settings file is not a JSON object.");

            var version = SettingsMigrator.VersionOf(document);
            if (version > _migrator.CurrentVersion)
            {
                _logger.LogWarning(
                    "Settings file {Path} has schema version {Version}, newer than {Current}; reading what this version knows",
                    FilePath, version, _migrator.CurrentVersion);
            }

            var migrated = _migrator.Migrate(document);
            var settings = migrated.Deserialize(SettingsJsonContext.Default.AppSettings)
                ?? throw new JsonException("The settings file is empty.");
            if (version < _migrator.CurrentVersion)
            {
                _logger.LogInformation("Migrated settings from schema version {From} to {To}", version, _migrator.CurrentVersion);
                return (settings with { SchemaVersion = _migrator.CurrentVersion }, true);
            }

            return (settings, false);
        }
        catch (JsonException e)
        {
            var backup = BackUpCorrupted();
            _logger.LogWarning(e, "Settings file {Path} is corrupted; moved it to {Backup} and starting with defaults", FilePath, backup);
            return (AppSettings.Defaults, true);
        }
    }

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
            _logger.LogError(e, "Can't back up corrupted settings file {Path}", FilePath);
            return null;
        }
    }

    private void Save(AppSettings settings) =>
        AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings));

    private void TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't write settings file {Path}", FilePath);
        }
    }
}
