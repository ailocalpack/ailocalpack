using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The pack an update job (task 56, "Translate new strings") builds on: the installed pack a game update made
/// incompatible. Kept in the job's options; the pack's files are copied into the job folder (<see cref="UpdateBase"/>).
/// </summary>
/// <param name="PackId">The pack's ID: the new revision keeps it.</param>
/// <param name="Revision">The installed revision; the job makes revision + 1.</param>
/// <param name="Local">A pack generated on this machine (else one from the registry).</param>
/// <param name="Title">The pack's title, for the UI.</param>
public sealed record GenerationBase(string PackId, int Revision, bool Local, string Title);

/// <summary>
/// The base pack's files as an update job keeps them in <c>base/</c> of its folder (<see cref="GenerationFiles.BaseFolder"/>),
/// so the job doesn't depend on the pack cache or the local pack while it runs: the manifest, the string table and the
/// glossary a local pack was made with (none for a registry pack).
/// </summary>
public sealed record UpdateBase(PackManifest Manifest, StringTable Strings, Glossary? Glossary = null)
{
    /// <summary>The job's options entry for this base.</summary>
    public GenerationBase Reference(bool local) => new(Manifest.Id, Manifest.Revision, local, Manifest.Title);
}

/// <summary>
/// What a game update changed in the strings, against a base pack's string table (task 56). Keys in the game's order;
/// <see cref="Removed"/> in ordinal order.
/// </summary>
/// <param name="Added">Strings the base doesn't translate: new in the game, or left untranslated by the base.</param>
/// <param name="Changed">Strings whose original differs from the one the base translated (its <see cref="StringTable.Originals"/> hash).</param>
/// <param name="Removed">Keys the base translates that the game no longer has.</param>
/// <param name="Unchanged">Strings the base translates and that are the same (or can't be compared: the base has no hash for them).</param>
public sealed record UpdateDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed, IReadOnlyList<string> Unchanged)
{
    /// <summary>The strings the update translates: added and changed ones.</summary>
    public IReadOnlySet<string> ToTranslate { get; } = new HashSet<string>(Added.Concat(Changed), StringComparer.Ordinal);

    /// <summary>
    /// Compares the game's strings (key, original) with the base table: a key without a (non-empty) translation in the
    /// base is added; with one, it is changed when the base's hash of its original differs, else unchanged (a base
    /// without a hash for it, made before task 56, counts it unchanged); the base's keys the game lacks are removed.
    /// </summary>
    public static UpdateDiff Of(IEnumerable<KeyValuePair<string, string>> current, StringTable baseTable)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseTable);
        List<string> added = [], changed = [], unchanged = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, original) in current)
        {
            if (!seen.Add(key))
            {
                continue;
            }

            if (!baseTable.Strings.TryGetValue(key, out var translation) || string.IsNullOrEmpty(translation))
            {
                added.Add(key);
            }
            else if (baseTable.Originals.TryGetValue(key, out var hash) && hash != OriginalHash.Of(original))
            {
                changed.Add(key);
            }
            else
            {
                unchanged.Add(key);
            }
        }

        var removed = baseTable.Strings.Keys.Where(k => !seen.Contains(k)).Order(StringComparer.Ordinal).ToList();
        return new UpdateDiff(added, changed, removed, unchanged);
    }

    /// <summary>
    /// The job's string store with the base's translations (and notes) of the unchanged strings added where the store
    /// has none yet: a store an earlier run filled keeps what it has.
    /// </summary>
    public StringTable Seed(StringTable? stored, StringTable baseTable, string language)
    {
        ArgumentNullException.ThrowIfNull(baseTable);
        var strings = new Dictionary<string, string>(stored?.Strings ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var context = new Dictionary<string, string>(stored?.Context ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var key in Unchanged)
        {
            if (strings.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
            {
                continue;
            }

            strings[key] = baseTable.Strings[key];
            if (baseTable.Context.TryGetValue(key, out var note) && !context.ContainsKey(key))
            {
                context[key] = note;
            }
        }

        return new StringTable(stored?.Language ?? language, strings, context);
    }
}
