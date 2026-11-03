using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Covers;

/// <summary>A stored cover of a manual game: the file and whether it is the game's icon (drawn small on the placeholder).</summary>
public sealed record StoredCover(string Path, bool IsIcon);

/// <summary>
/// Covers of manually added games in <see cref="AppPaths.GameCovers"/>: <c>&lt;id&gt;.cover.&lt;ext&gt;</c> for an image the
/// player picked, <c>&lt;id&gt;.icon.&lt;ext&gt;</c> for the game's icon, where <c>&lt;id&gt;</c> is the game ID with
/// <c>:</c> → <c>-</c> (<c>fp-&lt;hex&gt;</c>). One cover per game: saving replaces the old one. Best effort: failures are
/// logged, never thrown.
/// </summary>
public sealed class ManualCoverStore(AppPaths paths, ILogger<ManualCoverStore> logger)
{
    private readonly string _folder = paths.GameCovers;

    /// <param name="extension">Image file extension with the dot (<c>.png</c>, <c>.jpg</c>, <c>.ico</c>).</param>
    /// <returns>Whether the cover was written.</returns>
    public bool Save(GameId id, byte[] image, string extension, bool isIcon)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var ext = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        if (ext.Length > 6 || ext[1..].Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new ArgumentException($"Not an image extension: '{extension}'.", nameof(extension));
        }

        var target = System.IO.Path.Combine(_folder, $"{Key(id)}.{(isIcon ? "icon" : "cover")}{ext}");
        try
        {
            Delete(id, except: target);
            AtomicFile.WriteAllBytes(target, image);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Can't save the cover of {Id}", id);
            return false;
        }
    }

    /// <summary>The stored cover, or <c>null</c>. A picked image wins over an icon.</summary>
    public StoredCover? Find(GameId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var files = Files(id);
        return files.FirstOrDefault(f => Kind(f) == "cover") is { } cover ? new StoredCover(cover, false)
            : files.FirstOrDefault(f => Kind(f) == "icon") is { } icon ? new StoredCover(icon, true)
            : null;
    }

    /// <summary>Deletes the game's stored cover (the game left the library).</summary>
    public void Delete(GameId id) => Delete(id, except: null);

    private void Delete(GameId id, string? except)
    {
        foreach (var file in Files(id).Where(f => f != except))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Can't delete cover {Path}", file);
            }
        }
    }

    private List<string> Files(GameId id)
    {
        try
        {
            return Directory.Exists(_folder)
                ? [.. Directory.EnumerateFiles(_folder, Key(id) + ".*").Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Can't list covers in {Folder}", _folder);
            return [];
        }
    }

    /// <summary>"cover" or "icon" from <c>&lt;key&gt;.&lt;kind&gt;.&lt;ext&gt;</c>.</summary>
    private static string Kind(string file) => System.IO.Path.GetExtension(System.IO.Path.GetFileNameWithoutExtension(file)).TrimStart('.');

    private static string Key(GameId id) => id.ToString().Replace(':', '-');
}
