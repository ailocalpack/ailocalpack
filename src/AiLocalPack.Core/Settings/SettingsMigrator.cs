using System.Text.Json.Nodes;

namespace AiLocalPack.Core.Settings;

/// <summary>
/// Brings a settings document from the version it was written with up to <see cref="CurrentVersion"/>, one step at a
/// time: the step registered for version <c>n</c> turns an <c>n</c> document into an <c>n + 1</c> one.
/// </summary>
public sealed class SettingsMigrator
{
    internal const string VersionProperty = "schemaVersion";

    private readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> _steps;

    public SettingsMigrator(int currentVersion, IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        ArgumentNullException.ThrowIfNull(steps);
        CurrentVersion = currentVersion;
        _steps = steps;
    }

    /// <summary>The app's migrations. Add <c>[n] = MigrateFromN</c> here when bumping <see cref="AppSettings.CurrentSchemaVersion"/>.</summary>
    public static SettingsMigrator Default { get; } = new(AppSettings.CurrentSchemaVersion, new Dictionary<int, Func<JsonObject, JsonObject>>());

    public int CurrentVersion { get; }

    /// <summary>The version <paramref name="document"/> was written with; a document without one is version 1.</summary>
    public static int VersionOf(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document[VersionProperty] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 1;
    }

    /// <summary>
    /// The document at <see cref="CurrentVersion"/>. Documents already current, or from a newer app, come back unchanged
    /// (a newer one is read best-effort: unknown properties are ignored).
    /// </summary>
    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        for (var version = VersionOf(document); version < CurrentVersion; version++)
        {
            if (!_steps.TryGetValue(version, out var step))
            {
                throw new InvalidOperationException($"No settings migration from schema version {version} to {version + 1}.");
            }

            document = step(document);
            document[VersionProperty] = version + 1;
        }

        return document;
    }
}
