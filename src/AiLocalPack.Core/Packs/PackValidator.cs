using System.Security.Cryptography;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Packs;

/// <summary>One problem in a pack folder.</summary>
/// <param name="File">The file, relative to the pack folder and '/'-separated (<c>manifest.json</c>); empty for the folder itself.</param>
/// <param name="Field">The field inside the file (<c>authors[1].github</c>), or empty.</param>
public sealed record PackProblem(string File, string Field, string Message)
{
    public override string ToString() =>
        string.Join(": ", new[] { File, Field, Message }.Where(p => p.Length > 0));
}

/// <summary>Result of <see cref="PackValidator.Validate"/>.</summary>
/// <param name="Manifest">The manifest when it could be read, even if other checks failed.</param>
public sealed record PackValidation(string Folder, PackManifest? Manifest, IReadOnlyList<PackProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// The checks registry CI runs on a pack folder (<c>games/&lt;game&gt;/&lt;language&gt;/&lt;pack id&gt;/</c>): the
/// manifest reads, the folder sits where its manifest says, the string table matches its size and hash and reads, and the
/// folder holds nothing else: no executable, script or archive (by extension and content), no links, no file over
/// <see cref="MaxFileSize"/>, no more than <see cref="MaxPackSize"/> in all; every font it names is on the app's font
/// whitelist (<see cref="FontWhitelist"/>). Never throws for disk state.
/// </summary>
public static class PackValidator
{
    /// <summary>GitHub warns about files over 50 MB in a repository.</summary>
    public const long MaxFileSize = 50L * 1024 * 1024;

    public const long MaxPackSize = 100L * 1024 * 1024;

    public static PackValidation Validate(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var problems = new List<PackProblem>();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(full))
        {
            problems.Add(new PackProblem("", "", "The pack folder doesn't exist."));
            return new PackValidation(full, null, problems);
        }

        var files = CheckFiles(full, problems, cancellationToken);
        var manifest = ReadManifest(full, files, problems);
        CheckLayout(full, manifest, problems);
        CheckFonts(manifest, problems);
        if (manifest?.Strings is { } strings)
        {
            CheckStrings(full, manifest, strings, files, problems);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal) { RegistryLayout.ManifestFileName };
        if (manifest?.Strings is { } named)
        {
            allowed.Add(named.Path);
        }

        // A file already refused (executable, too large, unreadable) isn't reported twice.
        var reported = problems.Select(p => p.File).ToHashSet(StringComparer.Ordinal);
        // Without a manifest the string table can't be told apart: only files outside strings/ are surely stray.
        bool IsStray(string file) => !allowed.Contains(file) && (manifest is not null || !file.StartsWith("strings/", StringComparison.Ordinal));
        foreach (var file in files.Where(f => IsStray(f) && !reported.Contains(f)))
        {
            problems.Add(new PackProblem(file, "", "is not part of the pack: a pack folder holds only manifest.json and the string table it names."));
        }

        return new PackValidation(full, manifest, problems);
    }

    /// <summary>Every file (relative, '/'-separated, ordinal order) after the per-file checks.</summary>
    private static List<string> CheckFiles(string folder, List<PackProblem> problems, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        long total = 0;
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(current).EnumerateFileSystemInfos()];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add(new PackProblem(Relative(folder, current), "", $"can't be read: {e.Message}"));
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Relative(folder, entry.FullName);
                if (entry.LinkTarget is not null)
                {
                    problems.Add(new PackProblem(relative, "", "is a link; a pack holds plain files only."));
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                files.Add(relative);
                var size = ((FileInfo)entry).Length;
                total += size;
                if (size > MaxFileSize)
                {
                    problems.Add(new PackProblem(relative, "", $"is {Megabytes(size)}, over the {Megabytes(MaxFileSize)} limit for one file."));
                }

                try
                {
                    if (ExecutableFiles.Check(entry.FullName) is { } reason)
                    {
                        problems.Add(new PackProblem(relative, "", $"{reason} A pack holds data only, never anything that can run."));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new PackProblem(relative, "", $"can't be read: {e.Message}"));
                }
            }
        }

        if (total > MaxPackSize)
        {
            problems.Add(new PackProblem("", "", $"The pack is {Megabytes(total)}, over the {Megabytes(MaxPackSize)} limit."));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static PackManifest? ReadManifest(string folder, List<string> files, List<PackProblem> problems)
    {
        const string name = RegistryLayout.ManifestFileName;
        if (!files.Contains(name, StringComparer.Ordinal))
        {
            problems.Add(new PackProblem(name, "", "is missing."));
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(folder, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(new PackProblem(name, "", $"can't be read: {e.Message}"));
            return null;
        }

        var read = PackManifestSerializer.Read(text);
        problems.AddRange(read.Errors.Select(e => new PackProblem(name, e.Field, e.Message)));
        return read.Value;
    }

    /// <summary>The folder must be <c>games/&lt;game folder&gt;/&lt;language&gt;/&lt;pack id&gt;</c> of its manifest.</summary>
    private static void CheckLayout(string folder, PackManifest? manifest, List<PackProblem> problems)
    {
        var pack = new DirectoryInfo(folder);
        var language = pack.Parent;
        var game = language?.Parent;
        var games = game?.Parent;
        var actual = string.Join("/", new[] { games?.Name, game?.Name, language?.Name, pack.Name }.Where(n => n is not null));
        if (manifest is null)
        {
            if (games?.Name != RegistryLayout.GamesFolder)
            {
                problems.Add(new PackProblem("", "", $"The pack folder must be games/<game>/<language>/<pack id>/, not {actual}/."));
            }

            return;
        }

        var expected = RegistryLayout.ManifestPath(manifest.Game, manifest.Language, manifest.Id)[..^(RegistryLayout.ManifestFileName.Length + 1)];
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            problems.Add(new PackProblem("", "", $"The pack folder must be {expected}/ (game, language and ID from its manifest), not {actual}/."));
        }
    }

    /// <summary>Packs reference whitelisted fonts by ID, never ship them.</summary>
    private static void CheckFonts(PackManifest? manifest, List<PackProblem> problems)
    {
        for (var i = 0; i < (manifest?.Fonts.Count ?? 0); i++)
        {
            var id = manifest!.Fonts[i];
            if (FontWhitelist.Bundled.Find(id) is null)
            {
                problems.Add(new PackProblem(
                    RegistryLayout.ManifestFileName,
                    $"fonts[{i}]",
                    $"'{id}' is not on the font whitelist ({string.Join(", ", FontWhitelist.Bundled.Fonts.Select(f => f.Id))})."));
            }
        }
    }

    private static void CheckStrings(string folder, PackManifest manifest, PackStringsFile strings, List<string> files, List<PackProblem> problems)
    {
        if (!files.Contains(strings.Path, StringComparer.Ordinal))
        {
            problems.Add(new PackProblem(strings.Path, "", "is missing (named in manifest.json strings.path)."));
            return;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(Path.Combine(folder, strings.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            problems.Add(new PackProblem(strings.Path, "", $"can't be read: {e.Message}"));
            return;
        }

        if (data.LongLength != strings.Size)
        {
            problems.Add(new PackProblem(RegistryLayout.ManifestFileName, "strings.size", $"is {strings.Size}, but {strings.Path} is {data.LongLength} bytes."));
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        if (sha != strings.Sha256)
        {
            problems.Add(new PackProblem(RegistryLayout.ManifestFileName, "strings.sha256", $"doesn't match {strings.Path} (its SHA-256 is {sha})."));
        }

        var table = StringTableSerializer.Read(data);
        problems.AddRange(table.Errors.Select(e => new PackProblem(strings.Path, e.Field, e.Message)));
        if (table.Value is { } value && value.Language != manifest.Language)
        {
            problems.Add(new PackProblem(strings.Path, "language", $"is '{value.Language}', but the manifest's language is '{manifest.Language}'."));
        }
    }

    private static string Relative(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0.#} MB";
}
