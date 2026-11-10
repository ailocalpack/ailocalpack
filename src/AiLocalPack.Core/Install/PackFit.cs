using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Install;

/// <summary>
/// Whether an installed recipe pack was made for the game's files as they are (task 56). A recipe pack installs over
/// files it wasn't made for when the compatibility check finds it <i>probably compatible</i> (an update changed the
/// touched files: the recipe rebuilds them from the player's copy), and the automatic re-apply after a game update does
/// the same; the strings the update added or changed then stay untranslated, which <c>Translate new strings</c> (update
/// mode) fixes. Pure: compares the originals the install backed up (<see cref="InstalledFile.OriginalSha256"/>) with the
/// pack's builds, no disk access.
/// </summary>
public static class PackFit
{
    /// <summary>
    /// <c>true</c> when <paramref name="manifest"/> is a recipe pack (with a string table) and none of its builds matches
    /// the files the install replaced: a file both name has another original hash, or the build names a file the install
    /// found without an original. A binary patch pack only installs on its exact files, so it is never for another version.
    /// </summary>
    public static bool IsForOtherVersion(InstalledPack record, PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Recipe is null || manifest.Strings is null || manifest.Builds.Count == 0)
        {
            return false;
        }

        var originals = record.Files.ToDictionary(f => f.Path, f => f.OriginalSha256, StringComparer.Ordinal);
        return !manifest.Builds.Any(build => Fits(build, originals));
    }

    private static bool Fits(PackBuild build, Dictionary<string, string?> originals) =>
        build.Files.All(file => !originals.TryGetValue(file.Key, out var original)
            || string.Equals(original, file.Value, StringComparison.OrdinalIgnoreCase));
}
