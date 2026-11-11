using System.Security.Cryptography;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The game's files as they are without the pack installed in its folder (task 56): generation reads and copies the
/// game's own text, never a translation installed over it. For each file the pack wrote that still holds what it wrote,
/// the original comes from the install's backup; a file the pack added is left out. A file the game changed since (an
/// update or a Steam file check put its own back) is the game's again and stays as it is.
/// </summary>
/// <param name="Restored">Relative path (<c>/</c>-separated) → the backed-up original to use instead of the game's file.</param>
/// <param name="Added">Files the pack added, which the game doesn't have.</param>
public sealed record InstalledOriginals(IReadOnlyDictionary<string, string> Restored, IReadOnlySet<string> Added)
{
    public static InstalledOriginals None { get; } = new(new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>The game folder holds no installed pack's files.</summary>
    public bool IsEmpty => Restored.Count == 0 && Added.Count == 0;

    /// <summary>
    /// The originals of the pack installed in <paramref name="gameFolder"/> (the install record whose folder it is), or
    /// <see cref="None"/>. Never throws for disk state: a file that can't be read, or whose backup is gone, stays as the
    /// game has it.
    /// </summary>
    public static InstalledOriginals Find(AppPaths paths, string gameFolder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        var root = Path.GetFullPath(gameFolder);
        var record = new InstalledPackStore(paths).All.FirstOrDefault(r => PathNames.Same(Path.GetFullPath(r.InstallPath), root));
        return record is null ? None : Of(paths, record, root);
    }

    /// <summary>The originals of <paramref name="record"/>'s files in <paramref name="gameFolder"/>.</summary>
    public static InstalledOriginals Of(AppPaths paths, InstalledPack record, string gameFolder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(record);
        var backups = BackupLocation.RootOf(paths);
        var restored = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in record.Files)
        {
            try
            {
                var path = InstallPaths.Resolve(gameFolder, file.Path);
                if (!File.Exists(path) || !string.Equals(Sha256(path), file.InstalledSha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.OriginalSha256 is null)
                {
                    added.Add(file.Path);
                }
                else if (PackInstaller.BackupFile(backups, record, file.Path) is var backup && File.Exists(backup))
                {
                    restored[file.Path] = backup;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable: the game's file stays.
            }
        }

        return restored.Count == 0 && added.Count == 0 ? None : new InstalledOriginals(restored, added);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
