using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Generation;

/// <summary>Why the installed pack can't be the base of an update job (task 56).</summary>
public enum UpdateBaseProblem
{
    /// <summary>The installed revision isn't on this machine: not in the pack cache (a registry pack), gone or replaced (a local one).</summary>
    Missing,

    /// <summary>A binary patch pack without a string table: nothing to build on, a new translation is made instead.</summary>
    NoStrings,

    /// <summary>The pack's string table can't be read.</summary>
    Unreadable,
}

/// <summary>The base pack's files, or why there are none (English detail for the log).</summary>
/// <param name="Local">The base is a local pack (Packs → Mine), not a registry one.</param>
public sealed record UpdateBaseLoad(UpdateBase? Base, UpdateBaseProblem? Problem = null, string? Detail = null, bool Local = false)
{
    /// <summary>The job options' entry for the base, when there is one.</summary>
    public GenerationBase? Reference => Base?.Reference(Local);
}

/// <summary>
/// Reads the installed pack an update job builds on (task 56): a local pack from <see cref="LocalPackStore"/> (with the
/// glossary it was made with), a registry pack from the <see cref="PackCache"/>, which keeps the installed revision
/// since the install. Only a recipe pack (with a string table) can be a base. Never throws for disk state.
/// </summary>
public static class UpdateBaseLoader
{
    public static UpdateBaseLoad Load(InstalledPack record, LocalPackStore localPacks, PackCache cache)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localPacks);
        ArgumentNullException.ThrowIfNull(cache);
        if (record.Local)
        {
            if (localPacks.Find(record.Game, record.Language, record.PackId) is not { } local || local.Manifest.Revision != record.Revision)
            {
                return new UpdateBaseLoad(null, UpdateBaseProblem.Missing, $"The local pack {record.PackId} rev {record.Revision} is gone.");
            }

            return Read(local.Manifest, local.Folder, localPacks.ReadGlossary(record.Game, record.Language, record.PackId)) with { Local = true };
        }

        return cache.Find(record.Game, record.Language, record.PackId, record.Revision) is { } cached
            ? Read(cached.Manifest, cached.Folder, null)
            : new UpdateBaseLoad(null, UpdateBaseProblem.Missing, $"{record.PackId} rev {record.Revision} isn't in the pack cache.");
    }

    /// <summary>The base from a pack folder holding the manifest's string table (a cache entry, a local pack).</summary>
    public static UpdateBaseLoad Read(PackManifest manifest, string folder, Glossary? glossary)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(folder);
        if (manifest.Recipe is null || manifest.Strings is not { } strings)
        {
            return new UpdateBaseLoad(null, UpdateBaseProblem.NoStrings, $"{manifest.Id} is a binary patch pack without strings.");
        }

        try
        {
            using var stream = File.OpenRead(PackCache.PathIn(folder, strings.Path));
            var table = StringTableSerializer.Read(stream);
            if (table.Value is not { } value)
            {
                return new UpdateBaseLoad(null, UpdateBaseProblem.Unreadable, string.Join("; ", table.Errors.Select(e => $"{e.Field}: {e.Message}")));
            }

            return new UpdateBaseLoad(new UpdateBase(manifest, value, glossary?.Language == manifest.Language ? glossary : null));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new UpdateBaseLoad(null, UpdateBaseProblem.Unreadable, e.Message);
        }
    }
}
