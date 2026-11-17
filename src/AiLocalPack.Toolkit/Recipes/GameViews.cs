using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Toolkit.Recipes;

/// <summary>
/// How generation sees a game folder (_Live game folder_ in <c>docs/generation.md</c>): the folder itself, without the
/// pack installed there (its replaced files read from the install's backup, the files it added hidden:
/// <see cref="InstalledOriginals"/>, <see cref="PristineGameFiles"/>), so the game's own text is read, never a
/// translation over it, and the game is never touched to read it.
/// </summary>
public sealed class GameViews(AppPaths paths)
{
    /// <summary>The game as it is without its installed pack; the folder as it is when none is installed. Never throws for disk state.</summary>
    public IGameFiles WithoutPack(string gameFolder) => Of(gameFolder, Installed(gameFolder));

    /// <summary>A pack is installed in <paramref name="gameFolder"/> and still has its files there.</summary>
    public bool HasPack(string gameFolder) => !Installed(gameFolder).IsEmpty;

    /// <summary><paramref name="gameFolder"/> without <paramref name="originals"/>' pack (the folder as it is when there is none).</summary>
    public static IGameFiles Of(string gameFolder, InstalledOriginals originals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(originals);
        var folder = GameFiles.Folder(gameFolder);
        return originals.IsEmpty ? folder : new PristineGameFiles(folder, originals.Restored, originals.Added);
    }

    private InstalledOriginals Installed(string gameFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        return InstalledOriginals.Find(paths, gameFolder);
    }
}
