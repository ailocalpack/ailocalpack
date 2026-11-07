using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Install;

/// <summary>How the files of an installed pack look now, against its install record (task 33).</summary>
/// <param name="Reverted">Back to the original the pack replaced (a Steam file check, an update that left them as they were).</param>
/// <param name="Changed">Neither the pack's nor the original: an update rewrote them.</param>
/// <param name="Missing">Gone from the game folder.</param>
/// <param name="Unreadable">Couldn't be read (in use, access): the re-check can't tell.</param>
public sealed record PackFileStates(
    IReadOnlyList<string> Reverted,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>Every file is as the pack wrote it.</summary>
    public bool IsIntact => Reverted.Count == 0 && Changed.Count == 0 && Missing.Count == 0 && Unreadable.Count == 0;

    /// <summary>
    /// For the compatibility check of the game as it would be without the pack: each file still as the pack wrote it is
    /// taken as its recorded original (files the pack added have none and aren't in a manifest's builds).
    /// </summary>
    public IReadOnlyDictionary<string, string> Originals { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>What the re-check after a game update decided for an installed pack.</summary>
public enum PackRecheckDecision
{
    /// <summary>Every file is as the pack wrote it: nothing to do.</summary>
    Intact,

    /// <summary>A file couldn't be read: nothing is decided (the next check tries again).</summary>
    Unchecked,

    /// <summary>Files were put back or rewritten and the pack still fits: re-apply it (with a fresh backup).</summary>
    Reapply,

    /// <summary>
    /// The pack still fits but isn't re-applied: automatic re-apply is off, or the fit can't be checked (manifest
    /// unavailable). The player re-applies it by hand.
    /// </summary>
    Wiped,

    /// <summary>The update made the installed revision incompatible: needs a newer revision or generating the new strings.</summary>
    Incompatible,
}

/// <summary>
/// The re-check of an installed pack after a game update (task 33): <see cref="Inspect"/> hashes the files the record
/// lists, <see cref="Decide"/> (pure) turns that and the installed revision's compatibility with the game into what to
/// do.
/// </summary>
public static class PackRecheck
{
    /// <summary>
    /// Each recorded file compared with what the pack wrote and with the original (hashes through
    /// <paramref name="hashes"/>, so unchanged files cost nothing). Never throws for disk state.
    /// </summary>
    public static PackFileStates Inspect(InstalledPack record, string gameFolder, FileHashCache hashes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameFolder);
        ArgumentNullException.ThrowIfNull(hashes);
        List<string> reverted = [], changed = [], missing = [], unreadable = [];
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in record.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var path = Path.Combine(gameFolder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            string hash;
            try
            {
                if (!File.Exists(path))
                {
                    missing.Add(file.Path);
                    continue;
                }

                hash = hashes.Hash(path, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(file.Path);
                continue;
            }

            if (hash == file.InstalledSha256)
            {
                if (file.OriginalSha256 is { } original)
                {
                    originals[file.Path] = original;
                }
            }
            else if (hash == file.OriginalSha256)
            {
                reverted.Add(file.Path);
            }
            else
            {
                changed.Add(file.Path);
            }
        }

        return new PackFileStates(reverted, changed, missing, unreadable) { Originals = originals };
    }

    /// <summary>
    /// Intact files → nothing; an unreadable one → undecided; else the installed revision's fit with the game as it is
    /// without the pack (<paramref name="compatibility"/>, <c>null</c> = its manifest can't be had): incompatible →
    /// needs an update; fits → re-apply when <paramref name="autoReapply"/> (the setting), else left for the player.
    /// </summary>
    public static PackRecheckDecision Decide(PackFileStates files, PackCompatibilityVerdict? compatibility, bool autoReapply)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.IsIntact)
        {
            return PackRecheckDecision.Intact;
        }

        if (files.Unreadable.Count > 0)
        {
            return PackRecheckDecision.Unchecked;
        }

        return compatibility switch
        {
            null => PackRecheckDecision.Wiped,
            PackCompatibilityVerdict.Incompatible => PackRecheckDecision.Incompatible,
            _ => autoReapply ? PackRecheckDecision.Reapply : PackRecheckDecision.Wiped,
        };
    }
}
