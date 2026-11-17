using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>
/// The rebuilt files laid over the game folder for a test run (the live game, _Live game folder_; a full working copy
/// for a job set up before), undone afterwards so the game is as it was. In the job folder's <c>test-run/</c>:
/// <c>added.txt</c> (files the run adds, listed before they are written) and <c>originals/</c> (the game's files the
/// rebuild replaces, moved aside). <see cref="Recover"/> undoes what a crash left, and deleting the job does it first
/// (<see cref="GenerationJobFolders.Delete"/>); every step is safe to repeat.
/// </summary>
public sealed class TestRunOverlay
{
    private readonly string _folder;
    private readonly string _copy;

    private TestRunOverlay(string jobFolder, string copyFolder)
    {
        _folder = Path.Combine(jobFolder, "test-run");
        _copy = copyFolder;
    }

    /// <param name="extra">Files the run writes itself besides the rebuild (<c>steam_appid.txt</c>), removed afterwards.</param>
    /// <exception cref="IOException">A file can't be moved or copied (what was done is undone first).</exception>
    public static TestRunOverlay Apply(string jobFolder, string copyFolder, string buildFolder, IReadOnlyList<string> buildFiles, IReadOnlyList<string> extra)
    {
        ArgumentNullException.ThrowIfNull(buildFiles);
        ArgumentNullException.ThrowIfNull(extra);
        var overlay = new TestRunOverlay(jobFolder, copyFolder);
        overlay.Restore();
        try
        {
            var added = buildFiles.Where(f => !File.Exists(overlay.CopyPath(f))).Concat(extra).ToList();
            Directory.CreateDirectory(overlay._folder);
            AtomicFile.WriteAllBytes(overlay.AddedList, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', added)));
            foreach (var file in buildFiles)
            {
                var target = overlay.CopyPath(file);
                if (File.Exists(target))
                {
                    var original = overlay.OriginalPath(file);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(target, original, overwrite: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(buildFolder, file.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: true);
            }
        }
        catch
        {
            overlay.Restore();
            throw;
        }

        return overlay;
    }

    /// <summary>Undoes an overlay a crash or a killed app left in the job folder (nothing when there is none).</summary>
    /// <exception cref="IOException">A file can't be moved back.</exception>
    public static void Recover(string jobFolder, string copyFolder) => new TestRunOverlay(jobFolder, copyFolder).Restore();

    private string AddedList => Path.Combine(_folder, "added.txt");

    private string Originals => Path.Combine(_folder, "originals");

    /// <summary>The game's own files back in place; the rebuilt ones they replaced are gone, the added ones stay.</summary>
    public void RestoreOriginals()
    {
        if (!Directory.Exists(Originals))
        {
            return;
        }

        foreach (var original in Directory.EnumerateFiles(Originals, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_copy, Path.GetRelativePath(Originals, original));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(original, target, overwrite: true);
        }

        Directory.Delete(Originals, recursive: true);
    }

    /// <summary>The game as before the overlay: originals back, added files deleted, <c>test-run/</c> removed.</summary>
    public void Restore()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        RestoreOriginals();
        if (File.Exists(AddedList))
        {
            foreach (var file in File.ReadAllText(AddedList).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                File.Delete(CopyPath(file));
            }
        }

        Directory.Delete(_folder, recursive: true);
    }

    private string CopyPath(string relative) => Inside(_copy, relative);

    private string OriginalPath(string relative) => Inside(Originals, relative);

    private static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"'{relative}' is outside the game folder.");
        }

        return full;
    }
}
