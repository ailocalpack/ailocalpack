using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Library;

/// <summary>
/// The anti-cheat policy the app uses now: the bundled list with the registry's <c>policy/anti-cheat.json</c> on top,
/// from the latest pack list (fresh or the saved copy; the bundled list alone before the first one). Remembers which
/// detection rules the catalog was last checked with (<see cref="AppPaths.AntiCheatRulesFile"/>), so the library checks
/// every game again when the rules change (<see cref="RulesChanged"/>): a new marker may find an anti-cheat in a game
/// that had none. Never throws for disk state.
/// </summary>
public sealed class AntiCheatRules
{
    private readonly AntiCheatPolicy _bundled;
    private readonly PackIndex _index;
    private readonly string _file;
    private readonly ILogger<AntiCheatRules> _logger;
    private readonly Lock _lock = new();
    private string? _checked;
    private bool _loaded;

    public AntiCheatRules(AntiCheatPolicy bundled, PackIndex index, AppPaths paths, ILogger<AntiCheatRules> logger)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(paths);
        _bundled = bundled;
        _index = index;
        _file = paths.AntiCheatRulesFile;
        _logger = logger;
        index.Changed += (_, _) =>
        {
            if (!IsChecked(Current.RulesStamp))
            {
                RulesChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Raised (on the refreshing thread) when a new pack list brings detection rules the catalog wasn't checked with.</summary>
    public event EventHandler? RulesChanged;

    /// <summary>The bundled policy with the registry's on top (<see cref="AntiCheatPolicy.WithRegistry"/>).</summary>
    public AntiCheatPolicy Current => _bundled.WithRegistry(_index.Current?.Index);

    /// <summary>The catalog was checked with the rules of <paramref name="stamp"/> (<see cref="AntiCheatPolicy.RulesStamp"/>).</summary>
    public bool IsChecked(string stamp)
    {
        lock (_lock)
        {
            return Load() == stamp;
        }
    }

    /// <summary>Remembers that every available game was checked with the rules of <paramref name="stamp"/>.</summary>
    public void MarkChecked(string stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);
        lock (_lock)
        {
            if (Load() == stamp)
            {
                return;
            }

            _checked = stamp;
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(new AntiCheatRulesJson(1, stamp), AntiCheatRulesJsonContext.Default.AntiCheatRulesJson);
                AtomicFile.WriteAllBytes(_file, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not saved: the next start checks every game again, nothing worse.
                _logger.LogWarning(e, "Can't save {File}", _file);
            }
        }
    }

    private string? Load()
    {
        if (_loaded)
        {
            return _checked;
        }

        _loaded = true;
        try
        {
            if (File.Exists(_file))
            {
                var file = JsonSerializer.Deserialize(File.ReadAllBytes(_file), AntiCheatRulesJsonContext.Default.AntiCheatRulesJson);
                _checked = file is { SchemaVersion: 1 } ? file.Rules : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cache: unreadable means "not checked", so every game is checked again.
            _logger.LogWarning(e, "Can't read {File}", _file);
        }

        return _checked;
    }
}

/// <param name="Rules">The <see cref="AntiCheatPolicy.RulesStamp"/> the catalog was last checked with.</param>
internal sealed record AntiCheatRulesJson(int? SchemaVersion, string? Rules);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AntiCheatRulesJson))]
internal sealed partial class AntiCheatRulesJsonContext : JsonSerializerContext;
