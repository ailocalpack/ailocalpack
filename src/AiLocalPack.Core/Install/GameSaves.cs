namespace AiLocalPack.Core.Install;

/// <summary>
/// The player's saves among what a game writes (keys of <see cref="GameWriteGuard"/>): an uninstall undoes the caches
/// and settings the game rewrote since the install, never these (_Live game folder_ in <c>docs/generation.md</c>). A file
/// is a save when a folder on its path is a save folder (<c>save</c>, <c>saves</c>, <c>savegames</c>, <c>savedata</c>,
/// <c>saved games</c>: Ren'Py's <c>game/saves</c>, RPG Maker MV's <c>www/save</c>, Unreal's <c>Saved/SaveGames</c>), its
/// name starts with <c>save</c>, or it has a save extension (<c>.sav</c>, <c>.save</c>, <c>.rpgsave</c>,
/// <c>.rmmzsave</c>, <c>.rvdata2</c>, <c>.lsd</c>, <c>.es3</c>); Unity's <c>prefs</c> file (PlayerPrefs on Linux) and
/// Ren'Py's <c>persistent</c> are kept too.
/// </summary>
public static class GameSaves
{
    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        "save", "saves", "savegame", "savegames", "savedata", "saved games", "savegame_data",
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav", ".save", ".rpgsave", ".rmmzsave", ".rvdata2", ".lsd", ".es3",
    };

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "prefs", "persistent" };

    /// <summary>Whether the file of <paramref name="key"/> (<c>&lt;path&gt;</c> or <c>~&lt;root&gt;/&lt;path&gt;</c>) is the player's save.</summary>
    public static bool IsSave(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = key.Split('/');
        var name = parts[^1];
        return parts[..^1].Any(Folders.Contains)
            || name.StartsWith("save", StringComparison.OrdinalIgnoreCase)
            || Extensions.Contains(Path.GetExtension(name))
            || Names.Contains(name);
    }
}
