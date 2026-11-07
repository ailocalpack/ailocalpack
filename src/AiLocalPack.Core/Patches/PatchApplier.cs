using System.IO.Compression;
using System.Security.Cryptography;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Patches;

/// <summary>Why a binary patch couldn't be applied.</summary>
public enum PatchProblem
{
    /// <summary>The patch archive is not a ZIP, or doesn't hold exactly one <c>&lt;path&gt;.vcdiff</c> entry per touched file and nothing else (but folder entries).</summary>
    ArchiveInvalid,

    /// <summary>A file the patch applies to is not in the game folder (<see cref="PatchFailure.File"/>).</summary>
    SourceMissing,

    /// <summary>A file the patch applies to differs from the one it was made from (<see cref="PatchFailure.File"/>): incompatible.</summary>
    SourceMismatch,

    /// <summary>A game file can't be read (<see cref="PatchFailure.File"/>).</summary>
    SourceUnreadable,

    /// <summary>The delta can't be decoded (<see cref="PatchFailure.Vcdiff"/>): damaged, or made with options this app doesn't read.</summary>
    Decode,

    /// <summary>The patched file doesn't have the hash the manifest gives for it.</summary>
    ResultMismatch,

    /// <summary>Writing into the output folder failed.</summary>
    DiskError,
}

/// <param name="File">The touched file (game-relative, <c>/</c>-separated) the problem is about, when there is one.</param>
/// <param name="Detail">The decoder's or the OS's message.</param>
/// <param name="ExpectedSha256">For <see cref="PatchProblem.SourceMismatch"/>: the hash the patch was made from; for <see cref="PatchProblem.ResultMismatch"/>: the hash the result should have.</param>
/// <param name="ActualSha256">The hash found.</param>
public sealed record PatchFailure(
    PatchProblem Problem,
    string? File = null,
    string? Detail = null,
    VcdiffProblem? Vcdiff = null,
    string? ExpectedSha256 = null,
    string? ActualSha256 = null);

/// <param name="Files">The touched files written into the output folder (game-relative, ordinal order); empty on failure.</param>
/// <param name="AlreadyPatched">Of those, the files the game already had in their patched form (copied as they are).</param>
public sealed record PatchResult(IReadOnlyList<string> Files, IReadOnlyList<string> AlreadyPatched, PatchFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>Files done of all the patch touches.</summary>
public sealed record PatchProgress(int Done, int Total);

/// <summary>
/// Applies a patch pack's archive (CLAUDE.md → Pack format: a ZIP with exactly one <c>&lt;path&gt;.vcdiff</c> entry per
/// touched file, stored or deflated, nothing else; empty folder entries are ignored) to the player's files, writing the patched files into an output
/// folder laid out like the game (the install's staging; the game folder is never written). Entries are looked up by the
/// names the manifest implies, never extracted by the names inside, so no path in the archive reaches the disk. Each game
/// file must have the hash of the manifest's only build (else <see cref="PatchProblem.SourceMismatch"/>: a delta applies
/// only to the exact file it was made from); one that already has the patched hash is copied as is (a re-apply over an
/// intact pack). Every result is hashed against <c>patch.files</c>. Never throws for disk or archive state; on failure
/// the output folder may hold partial files (the caller deletes it).
/// </summary>
public sealed class PatchApplier(FileHashCache hashes, VcdiffOptions? options = null)
{
    /// <summary>Suffix of a delta's entry in the archive: <c>www/data/Map001.json.vcdiff</c>.</summary>
    public const string EntrySuffix = ".vcdiff";

    private readonly VcdiffOptions _options = options ?? VcdiffOptions.Default;

    public PatchResult Apply(
        PackManifest manifest,
        string archive,
        string gameDir,
        string outputDir,
        IProgress<PatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        if (manifest.Patch is not { } patch || manifest.Builds.Count != 1)
        {
            throw new ArgumentException("The pack is not a binary patch pack.", nameof(manifest));
        }

        var originals = manifest.Builds[0].Files;
        var paths = patch.Files.Keys.Order(StringComparer.Ordinal).ToList();
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(archive);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Fail(new PatchFailure(PatchProblem.ArchiveInvalid, Detail: $"The patch archive can't be opened: {e.Message}"));
        }

        using (zip)
        {
            if (CheckEntries(zip, paths) is { } invalid)
            {
                return Fail(invalid);
            }

            var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
            var written = new List<string>();
            var already = new List<string>();
            progress?.Report(new PatchProgress(0, paths.Count));
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameFile = Path.Combine(gameDir, path.Replace('/', Path.DirectorySeparatorChar));
                var outputFile = Path.Combine(outputDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(gameFile))
                {
                    return Fail(new PatchFailure(PatchProblem.SourceMissing, path));
                }

                string current;
                try
                {
                    current = hashes.Hash(gameFile, cancellationToken);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return Fail(new PatchFailure(PatchProblem.SourceUnreadable, path, e.Message));
                }

                var failure = current == patch.Files[path]
                    ? CopyAsIs(path, gameFile, outputFile)
                    : current != originals.GetValueOrDefault(path)
                        ? new PatchFailure(PatchProblem.SourceMismatch, path, ExpectedSha256: originals.GetValueOrDefault(path), ActualSha256: current)
                        : Decode(path, entries[path + EntrySuffix], gameFile, outputFile, patch.Files[path], cancellationToken);
                if (failure is not null)
                {
                    return Fail(failure);
                }

                written.Add(path);
                if (current == patch.Files[path])
                {
                    already.Add(path);
                }

                progress?.Report(new PatchProgress(written.Count, paths.Count));
            }

            return new PatchResult(written, already, null);
        }
    }

    /// <summary>Exactly the expected entries: one per touched file, no duplicates, nothing else but empty folder entries.</summary>
    private static PatchFailure? CheckEntries(ZipArchive zip, List<string> paths)
    {
        var expected = paths.Select(p => p + EntrySuffix).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') && entry.Length == 0)
            {
                // A folder entry (zip -r writes them): nothing to look up, nothing ever extracted.
                continue;
            }

            if (!expected.Contains(entry.FullName))
            {
                return new PatchFailure(PatchProblem.ArchiveInvalid, Detail: $"The patch archive holds {entry.FullName}, which the pack doesn't name.");
            }

            if (!seen.Add(entry.FullName))
            {
                return new PatchFailure(PatchProblem.ArchiveInvalid, Detail: $"The patch archive holds {entry.FullName} twice.");
            }
        }

        var missing = expected.Where(e => !seen.Contains(e)).Order(StringComparer.Ordinal).FirstOrDefault();
        return missing is null
            ? null
            : new PatchFailure(PatchProblem.ArchiveInvalid, missing[..^EntrySuffix.Length], $"The patch archive has no {missing}.");
    }

    private static PatchFailure? CopyAsIs(string path, string gameFile, string outputFile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
            File.Copy(gameFile, outputFile, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PatchFailure(PatchProblem.DiskError, path, e.Message);
        }
    }

    private PatchFailure? Decode(string path, ZipArchiveEntry entry, string gameFile, string outputFile, string expected, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PatchFailure(PatchProblem.DiskError, path, e.Message);
        }

        string actual;
        try
        {
            using var source = new FileStream(gameFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var delta = entry.Open();
            using var target = new FileStream(outputFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            VcdiffDecoder.Decode(source, delta, target, _options, cancellationToken);
            target.Position = 0;
            actual = Convert.ToHexStringLower(SHA256.HashData(target));
        }
        catch (VcdiffException e)
        {
            return new PatchFailure(PatchProblem.Decode, path, e.Message, e.Problem);
        }
        catch (InvalidDataException e)
        {
            return new PatchFailure(PatchProblem.Decode, path, $"The patch archive entry is damaged: {e.Message}", VcdiffProblem.Corrupt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The game file was opened (it hashed); a failure now is most likely the output.
            return new PatchFailure(PatchProblem.DiskError, path, e.Message);
        }

        return actual == expected
            ? null
            : new PatchFailure(PatchProblem.ResultMismatch, path, ExpectedSha256: expected, ActualSha256: actual);
    }

    private static PatchResult Fail(PatchFailure failure) => new([], [], failure);
}
