using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>
/// <c>build-failure.json</c> in the job folder: everything the Build stage's crashed test run found, in one file the
/// agent (fixing the stage) and the player read: the run (exit code, times, the OS's fault and report, where the
/// translated game's output parted from the untranslated game's, its last lines, screenshots, the logs) and the
/// rebuilt files (size before and after, how many translations each holds, the ones longest in UTF-8 bytes beside
/// their originals: a game that keeps text in fixed-size buffers overruns them first). Written on a crash, deleted
/// when the stage starts again; best effort, never throws.
/// </summary>
public static class BuildFailureReport
{
    public const int SchemaVersion = 1;

    /// <summary>The translations listed per file, and in all (longest in bytes first).</summary>
    public const int LongestPerFile = 5;

    public const int LongestInAll = 20;

    /// <summary>Indented, letters of every script as they are (the agent and the player read it).</summary>
    private static readonly JsonSerializerOptions Options = new(BuildFailureJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the report of a crashed <paramref name="testRun"/>.
    /// </summary>
    /// <param name="files">The rebuilt files laid over the game (relative, '/'-separated).</param>
    /// <param name="originalSize">The game's own size of a file, <c>null</c> = the game has none (added by the build).</param>
    /// <param name="originals">Each translated key's original text.</param>
    /// <param name="translations">The translations that went into the build.</param>
    public static void Write(
        GenerationFiles job,
        DateTimeOffset at,
        TestRunResult testRun,
        IReadOnlyList<string> files,
        Func<string, long?> originalSize,
        IReadOnlyDictionary<string, string> originals,
        IReadOnlyDictionary<string, string> translations)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(testRun);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(originalSize);
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentNullException.ThrowIfNull(translations);
        try
        {
            var strings = translations
                .Select(t => new LongString(t.Key, originals.GetValueOrDefault(t.Key), t.Value, Bytes(originals.GetValueOrDefault(t.Key)), Bytes(t.Value)))
                .ToList();
            var json = new BuildFailureJson(
                SchemaVersion,
                at.ToUniversalTime(),
                Run(testRun, job.Folder),
                [.. files.Select(f => File(job, f, originalSize, strings))],
                [.. Longest(strings).Take(LongestInAll)]);
            AtomicFile.WriteAllBytes(job.BuildFailurePath, JsonSerializer.SerializeToUtf8Bytes(json, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The report's rebuilt files as prompt lines, those holding the longest translations first (at most
    /// <paramref name="max"/>); <c>"(none)"</c> when the report can't be read.
    /// </summary>
    public static string DescribeFiles(GenerationFiles job, int max = 15)
    {
        ArgumentNullException.ThrowIfNull(job);
        BuildFailureJson? json;
        try
        {
            json = System.IO.File.Exists(job.BuildFailurePath)
                ? JsonSerializer.Deserialize(System.IO.File.ReadAllBytes(job.BuildFailurePath), BuildFailureJsonContext.Default.BuildFailureJson)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            json = null;
        }

        if (json?.Files is not { Count: > 0 } files)
        {
            return "(none)";
        }

        var lines = files
            .OrderByDescending(f => f.Longest is { Count: > 0 } l ? l[0].TranslationBytes : 0)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(max)
            .Select(f =>
            {
                var line = new StringBuilder($"- `{f.Path}`: ");
                line.Append(f.Strings.ToString("N0", CultureInfo.InvariantCulture)).Append(f.Strings == 1 ? " translation" : " translations");
                if (f.OriginalBytes is { } before && f.BuiltBytes is { } after)
                {
                    line.Append(CultureInfo.InvariantCulture, $", {before:N0} → {after:N0} bytes");
                }
                else if (f.BuiltBytes is { } added)
                {
                    line.Append(CultureInfo.InvariantCulture, $", added ({added:N0} bytes)");
                }

                if (f.Longest is { Count: > 0 } longest)
                {
                    var top = longest[0];
                    line.Append(CultureInfo.InvariantCulture, $"; longest translation {top.TranslationBytes:N0} bytes (original {top.OriginalBytes:N0}): `{top.Key}`");
                }

                return line.ToString();
            });
        var text = string.Join('\n', lines);
        return files.Count > max ? text + $"\n- and {files.Count - max} more files" : text;
    }

    /// <summary>The report's rebuilt files (relative, '/'-separated); empty when it can't be read.</summary>
    public static IReadOnlyList<string> Files(GenerationFiles job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            return System.IO.File.Exists(job.BuildFailurePath)
                && JsonSerializer.Deserialize(System.IO.File.ReadAllBytes(job.BuildFailurePath), BuildFailureJsonContext.Default.BuildFailureJson) is { Files: { } files }
                ? [.. files.Select(f => f.Path)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Deletes the report (the stage starts again); best effort.</summary>
    public static void Delete(GenerationFiles job)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            System.IO.File.Delete(job.BuildFailurePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static RunJson Run(TestRunResult run, string jobFolder) => new(
        run.ExitCode,
        run.ExitCode is { } code ? CrashReports.ExitCodeText(code) : null,
        run.SecondLaunch,
        Ms(run.Ran),
        Ms(run.Ready),
        Ms(run.Watch),
        run.Detail,
        run.Fault is { } fault ? new FaultJson(fault.Module, fault.ModuleVersion, fault.ExceptionCode, fault.Offset, fault.InGame) : null,
        run.OutputDiff is { } diff ? new OutputJson(diff.LastShared, diff.OriginalNext, diff.OnlyTranslated, diff.TranslatedLines, diff.OriginalLines) : null,
        run.LastOutput,
        run.CrashReport?.Split('\n'),
        "logs/test-run.log",
        run.Shots?.Select(s => new ShotJson(s.Kind.ToString().ToLowerInvariant(), Relative(jobFolder, s.Path))).ToList());

    private static FileJson File(GenerationFiles job, string file, Func<string, long?> originalSize, List<LongString> strings)
    {
        var built = Path.Combine(job.BuildFolder, file.Replace('/', Path.DirectorySeparatorChar));
        long? builtSize = System.IO.File.Exists(built) ? new FileInfo(built).Length : null;
        var inFile = strings.Where(s => s.Key.StartsWith(file + ":", StringComparison.Ordinal)).ToList();
        return new FileJson(file, originalSize(file), builtSize, inFile.Count, [.. Longest(inFile).Take(LongestPerFile)]);
    }

    /// <summary>Longest translations in bytes first, then those that grew most against their original.</summary>
    private static IEnumerable<StringJson> Longest(IEnumerable<LongString> strings) => strings
        .OrderByDescending(s => s.TranslationBytes)
        .ThenByDescending(s => s.TranslationBytes - s.OriginalBytes)
        .ThenBy(s => s.Key, StringComparer.Ordinal)
        .Select(s => new StringJson(s.Key, s.OriginalBytes, s.TranslationBytes, s.Original, s.Translation));

    private static int Bytes(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

    private static long? Ms(TimeSpan? time) => time is { } t ? (long)t.TotalMilliseconds : null;

    private static string Relative(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
    }

    private sealed record LongString(string Key, string? Original, string Translation, int OriginalBytes, int TranslationBytes);
}

internal sealed record BuildFailureJson(int SchemaVersion, DateTimeOffset At, RunJson TestRun, IReadOnlyList<FileJson> Files, IReadOnlyList<StringJson> LongestStrings);

internal sealed record RunJson(
    int? ExitCode,
    string? ExitCodeText,
    bool SecondLaunch,
    long? RanMs,
    long? ReadyMs,
    long? WatchMs,
    string? Detail,
    FaultJson? Fault,
    OutputJson? Output,
    IReadOnlyList<string> LastOutput,
    IReadOnlyList<string>? CrashReport,
    string Log,
    IReadOnlyList<ShotJson>? Shots);

internal sealed record FaultJson(string Module, string? ModuleVersion, string? ExceptionCode, string? Offset, bool InGame);

internal sealed record OutputJson(string? LastShared, IReadOnlyList<string> OriginalNext, IReadOnlyList<string> OnlyTranslated, int TranslatedLines, int OriginalLines);

internal sealed record ShotJson(string Kind, string Path);

internal sealed record FileJson(string Path, long? OriginalBytes, long? BuiltBytes, int Strings, IReadOnlyList<StringJson> Longest);

internal sealed record StringJson(string Key, int OriginalBytes, int TranslationBytes, string? Original, string Translation);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuildFailureJson))]
internal sealed partial class BuildFailureJsonContext : JsonSerializerContext;
