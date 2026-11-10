using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Packs;

/// <summary>A pack generated on this machine (task 51): its folder (a registry pack folder), manifest and build report.</summary>
public sealed record LocalPack(string Folder, PackManifest Manifest, LocalPackReport Report);

/// <summary>
/// What the Build stage found besides the manifest (task 51), for the Generation result and Packs → Mine (task 55).
/// Original texts appear here (warnings, what stays untranslated): the report stays on this machine, never in a pack.
/// </summary>
/// <param name="Job">ID of the generation job that made the pack (its folder may be gone).</param>
/// <param name="Fonts">The font check and swap.</param>
/// <param name="Warnings">Validator warnings of the stored translations, in key order.</param>
/// <param name="Untranslated">Strings without a translation (they stay in the game's language), in key order.</param>
/// <param name="KeptAsIs">Translations the agent left the same as the original (names, "HP"): counted as translated.</param>
/// <param name="SizeBytes">The pack folder's size (manifest + string table).</param>
/// <param name="InGameLanguage">The language the player has to pick in the game's settings, when not the pack's own.</param>
/// <param name="Tried">When the player first closed the game after playing it with the pack installed (task 55); publishing is offered from then on.</param>
/// <param name="ShareDismissed">The player answered "Not now" to the result's "Share it" strip.</param>
/// <param name="Update">Made by an update job (task 56): the revision it builds on and what the game update changed.</param>
public sealed record LocalPackReport(
    string Job,
    DateTimeOffset Created,
    TestRunResult TestRun,
    LocalPackFonts Fonts,
    IReadOnlyList<LocalPackWarning> Warnings,
    IReadOnlyList<LocalPackString> Untranslated,
    int KeptAsIs,
    long SizeBytes = 0,
    string? InGameLanguage = null,
    DateTimeOffset? Tried = null,
    bool ShareDismissed = false,
    LocalPackUpdate? Update = null)
{
    /// <summary>The player has played the game with the pack installed.</summary>
    public bool IsTried => Tried is not null;
}

/// <summary>What an update job (task 56) changed against the revision it builds on.</summary>
/// <param name="BaseRevision">The base pack's revision; the pack is the next one.</param>
/// <param name="Added">Strings translated that the base lacked (new in the game, or untranslated by the base).</param>
/// <param name="Changed">Strings translated again because the game changed their original.</param>
/// <param name="Removed">The base's strings the game no longer has.</param>
public sealed record LocalPackUpdate(int BaseRevision, int Added, int Changed, int Removed);

/// <param name="Checked">Whether the engine's fonts could be checked at all (no font hook = not checked).</param>
/// <param name="Replacement">The whitelisted font swapped in, or <c>null</c>.</param>
/// <param name="Swapped">The game fonts it replaces (family or file names).</param>
/// <param name="Uncovered">Letters the replacement lacks too (code points).</param>
public sealed record LocalPackFonts(bool Checked, string? Replacement, IReadOnlyList<string> Swapped, IReadOnlyList<int> Uncovered)
{
    public static LocalPackFonts NotChecked { get; } = new(false, null, [], []);
}

/// <summary>A validator warning (<c>too-long</c>, <c>whitespace</c>…) of one stored translation.</summary>
public sealed record LocalPackWarning(string Key, string Rule, string Message, string Original, string Translation);

public sealed record LocalPackString(string Key, string Original);

/// <summary>
/// Packs generated on this machine, under <see cref="AppPaths.LocalPacks"/>: each pack folder is laid out exactly like
/// the registry's (<c>games/&lt;game folder&gt;/&lt;language&gt;/&lt;id&gt;/manifest.json</c> +
/// <c>strings/strings.json.gz</c>), so <c>pack validate</c> checks it as registry CI would and publishing (task 60)
/// commits it as is; the build report is apart, in <c>reports/&lt;game folder&gt;/&lt;language&gt;/&lt;id&gt;.json</c>
/// (<c>{schemaVersion: 1, job, created, testRun {outcome, skip?, detail?, ranMs?, exitCode?}, fonts {checked,
/// replacement?, swapped, uncovered}, warnings [{key, rule, message, original, translation}], untranslated [{key,
/// original}], keptAsIs, sizeBytes, inGameLanguage?, tried?, shareDismissed?, update? {baseRevision, added, changed,
/// removed}}</c>; tried and shareDismissed are the player's, task 55; update is task 56's), since a pack folder holds
/// nothing else; a binary patch pack's archive (task 57) is in <c>releases/&lt;Release asset name&gt;</c>
/// (<see cref="PatchArchiveName"/>), as a local registry keeps it (<c>PackFetcher.LocalReleasesFolder</c>); beside the
/// report <c>&lt;id&gt;.glossary.json</c>, the glossary the pack was made with (task 56: an update of
/// the pack starts from it). Until GitHub sign-in
/// (task 58) the author is <see cref="PlaceholderAuthor"/>, replaced when publishing. Listing never throws for disk
/// state (a broken pack is logged and left out).
/// </summary>
public sealed class LocalPackStore
{
    /// <summary>The author of a pack made before the player signed in to GitHub; publishing puts the player's login.</summary>
    public const string PlaceholderAuthor = "local-author";

    public const int ReportSchemaVersion = 1;

    private const string ReportsFolder = "reports";

    /// <summary>Where patch archives go, named as their Release asset (the local registry's folder name).</summary>
    public const string ReleasesFolder = "releases";

    /// <summary>
    /// The Release download folder of patch archives in the manifest's <c>patch.url</c>, until publishing (task 60) uploads
    /// the archive and names its real asset; a local pack's archive is always read from <see cref="ReleasesFolder"/>.
    /// </summary>
    public const string PatchUrlPrefix = "https://github.com/ailocalpack/registry/releases/download/patches/";

    private static readonly JsonSerializerOptions WriteOptions = new(LocalPackJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger _logger;

    public LocalPackStore(AppPaths paths, ILogger<LocalPackStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Root = paths.LocalPacks;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string Root { get; }

    public string PackFolder(GameId game, string language, string id) =>
        Path.Combine(Root, RegistryLayout.GamesFolder, RegistryLayout.GameFolder(game), language, id);

    public string ReportPath(GameId game, string language, string id) =>
        Path.Combine(Root, ReportsFolder, RegistryLayout.GameFolder(game), language, id + ".json");

    /// <summary>The glossary the pack was made with (task 56), next to its report.</summary>
    public string GlossaryPath(GameId game, string language, string id) =>
        Path.Combine(Root, ReportsFolder, RegistryLayout.GameFolder(game), language, id + ".glossary.json");

    /// <summary>The archive's Release asset name: <c>&lt;game folder&gt;-&lt;language&gt;-&lt;pack id&gt;-&lt;revision&gt;.zip</c>.</summary>
    public static string PatchArchiveName(GameId game, string language, string id, int revision)
    {
        ArgumentNullException.ThrowIfNull(game);
        return $"{RegistryLayout.GameFolder(game)}-{language}-{id}-{revision.ToString(CultureInfo.InvariantCulture)}.zip";
    }

    /// <summary>The <c>patch.url</c> of a local patch pack.</summary>
    public static Uri PatchUrl(GameId game, string language, string id, int revision) => new(PatchUrlPrefix + PatchArchiveName(game, language, id, revision));

    /// <summary>The local archive of a patch pack (named by its <c>patch.url</c>), or <c>null</c> for a recipe pack.</summary>
    public string? PatchArchivePath(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Patch is not { } patch)
        {
            return null;
        }

        var name = Uri.UnescapeDataString(patch.Url.Segments[^1]);
        return name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 ? null : Path.Combine(Root, ReleasesFolder, name);
    }

    /// <summary>
    /// <paramref name="preferred"/> when no local pack of the game + language has it, else the first free
    /// <c>&lt;preferred&gt;-2</c>, <c>-3</c>…
    /// </summary>
    public string FreeId(GameId game, string language, string preferred)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrEmpty(preferred);
        var id = preferred;
        for (var n = 2; Directory.Exists(PackFolder(game, language, id)) || File.Exists(ReportPath(game, language, id)); n++)
        {
            id = $"{preferred}-{n.ToString(CultureInfo.InvariantCulture)}";
        }

        return id;
    }

    /// <summary>
    /// Writes the pack (replacing a local pack with the same game, language and ID): the string table compressed, the
    /// manifest with its <c>strings</c> entry (path, hash, size), then the report with the pack's size. The pack folder
    /// is made next to its place and renamed into it, so a crash leaves the old pack or the new one.
    /// </summary>
    /// <param name="manifest">The manifest; its <see cref="PackManifest.Strings"/> is set here.</param>
    /// <param name="glossary">The glossary the pack was made with, kept for its updates (none deletes an earlier one).</param>
    /// <param name="patchArchive">A patch pack's archive (task 57), moved into <see cref="ReleasesFolder"/> first; required when the manifest has a <c>patch</c>.</param>
    /// <exception cref="IOException">A file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be written.</exception>
    public LocalPack Save(PackManifest manifest, StringTable table, LocalPackReport report, Glossary? glossary = null, string? patchArchive = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(report);
        long archiveBytes = 0;
        if (manifest.Patch is not null)
        {
            var archivePath = PatchArchivePath(manifest) ?? throw new ArgumentException("The patch URL names no archive file.", nameof(manifest));
            if (patchArchive is null)
            {
                throw new ArgumentException("A patch pack needs its archive.", nameof(patchArchive));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            if (!string.Equals(Path.GetFullPath(patchArchive), archivePath, StringComparison.Ordinal))
            {
                File.Move(patchArchive, archivePath, overwrite: true);
            }

            archiveBytes = new FileInfo(archivePath).Length;
        }

        using var compressed = new MemoryStream();
        StringTableSerializer.Write(table, compressed, compress: true);
        var strings = compressed.ToArray();
        var named = manifest with
        {
            Strings = new PackStringsFile(PackStringsFile.DefaultPath, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(strings)), strings.LongLength),
        };
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(PackManifestSerializer.Write(named));
        var folder = PackFolder(manifest.Game, manifest.Language, manifest.Id);
        var parent = Path.GetDirectoryName(folder)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{manifest.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stringsPath = Path.Combine(staging, PackStringsFile.DefaultPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stringsPath)!);
            AtomicFile.WriteAllBytes(stringsPath, strings);
            AtomicFile.WriteAllBytes(Path.Combine(staging, RegistryLayout.ManifestFileName), manifestBytes);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(staging, folder);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        var sized = report with { SizeBytes = strings.LongLength + manifestBytes.LongLength + archiveBytes };
        var glossaryPath = GlossaryPath(manifest.Game, manifest.Language, manifest.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(glossaryPath)!);
        if (glossary is not null)
        {
            AtomicFile.WriteAllBytes(glossaryPath, GlossarySerializer.Write(glossary));
        }
        else
        {
            File.Delete(glossaryPath);
        }

        WriteReport(ReportPath(manifest.Game, manifest.Language, manifest.Id), sized);
        return new LocalPack(folder, named, sized);
    }

    /// <summary>Local packs (of <paramref name="game"/> only, when given), by game, language and ID. Never throws for disk state.</summary>
    public IReadOnlyList<LocalPack> List(GameId? game = null)
    {
        var list = new List<LocalPack>();
        var games = Path.Combine(Root, RegistryLayout.GamesFolder);
        try
        {
            if (!Directory.Exists(games))
            {
                return list;
            }

            var gameFolders = game is null
                ? Directory.EnumerateDirectories(games).Order(StringComparer.Ordinal).ToList()
                : [Path.Combine(games, RegistryLayout.GameFolder(game))];
            foreach (var gameFolder in gameFolders.Where(Directory.Exists))
            {
                foreach (var languageFolder in Directory.EnumerateDirectories(gameFolder).Order(StringComparer.Ordinal))
                {
                    foreach (var packFolder in Directory.EnumerateDirectories(languageFolder).Order(StringComparer.Ordinal))
                    {
                        if (!Path.GetFileName(packFolder).StartsWith('.') && Read(packFolder) is { } pack)
                        {
                            list.Add(pack);
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Local packs in {Folder} can't be listed", games);
        }

        return list;
    }

    /// <summary>The local pack the job made, or <c>null</c>.</summary>
    public LocalPack? FindByJob(GameId game, string jobId) => List(game).FirstOrDefault(p => p.Report.Job == jobId);

    /// <summary>The local pack of the game, language and ID, or <c>null</c> (missing, broken). Never throws for disk state.</summary>
    public LocalPack? Find(GameId game, string language, string id)
    {
        ArgumentNullException.ThrowIfNull(game);
        return PackFormat.IsSlug(id) && Languages.GameLanguages.IsKnown(language) ? Read(PackFolder(game, language, id)) : null;
    }

    /// <summary>The glossary the pack was made with, or <c>null</c> (none kept, unreadable: logged). Never throws for disk state.</summary>
    public Glossary? ReadGlossary(GameId game, string language, string id)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!PackFormat.IsSlug(id) || !Languages.GameLanguages.IsKnown(language))
        {
            return null;
        }

        var path = GlossaryPath(game, language, id);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var (glossary, error) = GlossarySerializer.Read(File.ReadAllBytes(path));
            if (glossary is null)
            {
                _logger.LogWarning("The glossary {Path} of a local pack can't be read: {Error}", path, error);
            }

            return glossary;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "The glossary {Path} of a local pack can't be read", path);
            return null;
        }
    }

    /// <summary>
    /// Changes the pack's report (the player's marks: tried in game, "Not now"); the pack itself stays as it is.
    /// Returns the pack as saved, or <c>null</c> when it is gone.
    /// </summary>
    /// <exception cref="IOException">The report can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The report can't be written.</exception>
    public LocalPack? UpdateReport(GameId game, string language, string id, Func<LocalPackReport, LocalPackReport> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Find(game, language, id) is not { } pack)
        {
            return null;
        }

        var report = change(pack.Report);
        WriteReport(ReportPath(game, language, id), report);
        return pack with { Report = report };
    }

    /// <summary>Deletes the pack folder, its patch archive and its report (nothing when absent).</summary>
    /// <exception cref="IOException">A file can't be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file can't be deleted.</exception>
    public void Delete(GameId game, string language, string id)
    {
        if (Find(game, language, id) is { } pack && PatchArchivePath(pack.Manifest) is { } archive)
        {
            File.Delete(archive);
        }

        var folder = PackFolder(game, language, id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        File.Delete(ReportPath(game, language, id));
        File.Delete(GlossaryPath(game, language, id));
    }

    private LocalPack? Read(string packFolder)
    {
        var manifestPath = Path.Combine(packFolder, RegistryLayout.ManifestFileName);
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifest = PackManifestSerializer.Read(File.ReadAllText(manifestPath));
            if (manifest.Value is not { } value)
            {
                _logger.LogWarning("Local pack {Folder} has an invalid manifest: {Errors}", packFolder, string.Join("; ", manifest.Errors.Select(e => $"{e.Field}: {e.Message}")));
                return null;
            }

            var reportPath = ReportPath(value.Game, value.Language, value.Id);
            if (ReadReport(reportPath) is not { } report)
            {
                _logger.LogWarning("Local pack {Folder} has no readable report {Report}", packFolder, reportPath);
                return null;
            }

            return new LocalPack(packFolder, value, report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Local pack {Folder} can't be read", packFolder);
            return null;
        }
    }

    private static void WriteReport(string path, LocalPackReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var run = report.TestRun;
        var json = new LocalPackReportJson(
            ReportSchemaVersion,
            report.Job,
            report.Created.ToUniversalTime(),
            new TestRunJson(
                JsonNamingPolicy.CamelCase.ConvertName(run.Outcome.ToString()),
                run.Skip is { } skip ? JsonNamingPolicy.CamelCase.ConvertName(skip.ToString()) : null,
                run.Detail,
                run.Ran is { } ran ? (long)ran.TotalMilliseconds : null,
                run.ExitCode),
            new FontsJson(report.Fonts.Checked, report.Fonts.Replacement, report.Fonts.Swapped, report.Fonts.Uncovered),
            [.. report.Warnings.Select(w => new WarningJson(w.Key, w.Rule, w.Message, w.Original, w.Translation))],
            [.. report.Untranslated.Select(s => new StringJson(s.Key, s.Original))],
            report.KeptAsIs,
            report.SizeBytes,
            report.InGameLanguage,
            report.Tried?.ToUniversalTime(),
            report.ShareDismissed ? true : null,
            report.Update is { } u ? new UpdateJson(u.BaseRevision, u.Added, u.Changed, u.Removed) : null);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(json, WriteOptions.GetTypeInfo(typeof(LocalPackReportJson))));
    }

    /// <summary>The report, or <c>null</c> when missing, unreadable, invalid or of a newer format.</summary>
    internal static LocalPackReport? ReadReport(string path)
    {
        LocalPackReportJson? json;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            json = JsonSerializer.Deserialize(File.ReadAllBytes(path), LocalPackJsonContext.Default.LocalPackReportJson);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (json is not { SchemaVersion: ReportSchemaVersion, Job: { Length: > 0 } job, TestRun: { } run }
            || !Enum.TryParse<TestRunOutcome>(run.Outcome, ignoreCase: true, out var outcome))
        {
            return null;
        }

        TestRunSkip? skip = run.Skip is { } skipText && Enum.TryParse<TestRunSkip>(skipText, ignoreCase: true, out var parsed) ? parsed : null;
        var fonts = json.Fonts is { } f ? new LocalPackFonts(f.Checked, f.Replacement, f.Swapped ?? [], f.Uncovered ?? []) : LocalPackFonts.NotChecked;
        return new LocalPackReport(
            job,
            json.Created,
            new TestRunResult(outcome, skip, run.Detail, run.RanMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, run.ExitCode),
            fonts,
            [.. (json.Warnings ?? []).OfType<WarningJson>().Select(w => new LocalPackWarning(w.Key ?? "", w.Rule ?? "", w.Message ?? "", w.Original ?? "", w.Translation ?? ""))],
            [.. (json.Untranslated ?? []).OfType<StringJson>().Select(s => new LocalPackString(s.Key ?? "", s.Original ?? ""))],
            json.KeptAsIs,
            json.SizeBytes,
            json.InGameLanguage,
            json.Tried,
            json.ShareDismissed == true,
            json.Update is { BaseRevision: >= 1 } u ? new LocalPackUpdate(u.BaseRevision, Math.Max(0, u.Added), Math.Max(0, u.Changed), Math.Max(0, u.Removed)) : null);
    }
}

internal sealed record LocalPackReportJson(
    int SchemaVersion,
    string? Job,
    DateTimeOffset Created,
    TestRunJson? TestRun,
    FontsJson? Fonts,
    IReadOnlyList<WarningJson?>? Warnings,
    IReadOnlyList<StringJson?>? Untranslated,
    int KeptAsIs,
    long SizeBytes,
    string? InGameLanguage,
    DateTimeOffset? Tried = null,
    bool? ShareDismissed = null,
    UpdateJson? Update = null);

internal sealed record UpdateJson(int BaseRevision, int Added, int Changed, int Removed);

internal sealed record TestRunJson(string? Outcome, string? Skip, string? Detail, long? RanMs, int? ExitCode);

internal sealed record FontsJson(bool Checked, string? Replacement, IReadOnlyList<string>? Swapped, IReadOnlyList<int>? Uncovered);

internal sealed record WarningJson(string? Key, string? Rule, string? Message, string? Original, string? Translation);

internal sealed record StringJson(string? Key, string? Original);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocalPackReportJson))]
internal sealed partial class LocalPackJsonContext : JsonSerializerContext;
