using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline;

/// <summary>
/// How the untranslated game behaves when the test run starts it from its folder, measured once per game version:
/// how long it takes to load (<see cref="Ready"/>), how long a translated run is then watched, whether it kept
/// running all that time, and its screenshot once loaded.
/// </summary>
/// <param name="Version">The game version it was measured on (<see cref="TestRunBaselines.VersionKey"/>).</param>
/// <param name="Ready">When it finished loading; <c>null</c> = never seen (no picture and no calm in disk reads before the cap).</param>
/// <param name="Watch">How long a translated run must keep running (<see cref="TestRunOptions.WatchFor"/>).</param>
/// <param name="Survived">It kept running for all of <paramref name="Watch"/>; otherwise the game can't be tried from a copy here.</param>
/// <param name="Ran">How long it ran (all of the watch, or until it closed).</param>
/// <param name="ExitCode">Its exit code when it closed by itself.</param>
/// <param name="Screenshot">Its picture once loaded (PNG), if one was taken.</param>
public sealed record TestRunBaseline(
    string Version,
    TimeSpan? Ready,
    TimeSpan Watch,
    bool Survived,
    TimeSpan Ran,
    DateTimeOffset Measured,
    int? ExitCode = null,
    string? Screenshot = null);

/// <summary>
/// The measured baselines, one file per game in <c>&lt;data&gt;/test-runs/</c> (<c>&lt;game folder&gt;.json</c> and
/// its screenshot <c>&lt;game folder&gt;.png</c>, its output <c>&lt;game folder&gt;.output.log</c>); a baseline of another game version doesn't count. Beside them
/// <c>&lt;game folder&gt;.writes.json</c>: the files the game's test runs write (<see cref="Writes"/>). Never throws for
/// disk state: a broken file reads as none.
/// </summary>
public sealed class TestRunBaselines(string folder)
{
    /// <summary>The most files remembered per game in <see cref="Writes"/>.</summary>
    public const int MaxWrites = 200;

    private const int SchemaVersion = 1;

    public string Folder { get; } = folder;

    /// <summary>The game's version as a short stable text: the store build, else a hash of the file-hash set, else <c>unknown</c>.</summary>
    public static string VersionKey(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Version?.BuildId is { Length: > 0 } build)
        {
            return "build:" + build;
        }

        if (game.Version?.FileHashes is { Count: > 0 } files)
        {
            var text = string.Join('\n', files.Select(f => f.Key + " " + f.Value));
            return "files:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }

        return "unknown";
    }

    /// <summary>The baseline of this game version, or <c>null</c>.</summary>
    public TestRunBaseline? Find(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        try
        {
            var path = JsonPath(game.Id);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = JsonSerializer.Deserialize(File.ReadAllBytes(path), BaselineJsonContext.Default.BaselineJson);
            if (json is not { SchemaVersion: SchemaVersion, Version: { Length: > 0 } version, WatchMs: > 0 } || version != VersionKey(game))
            {
                return null;
            }

            var shot = json.Screenshot is { Length: > 0 } name && File.Exists(Path.Combine(Folder, name)) ? Path.Combine(Folder, name) : null;
            return new TestRunBaseline(
                version,
                json.ReadyMs is >= 0 and var ready ? TimeSpan.FromMilliseconds(ready) : null,
                TimeSpan.FromMilliseconds(json.WatchMs),
                json.Survived,
                TimeSpan.FromMilliseconds(Math.Max(0, json.RanMs)),
                json.Measured,
                json.ExitCode,
                shot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The watch measured for the game on any version (for the crash window of a player's session), or <c>null</c>.</summary>
    public TimeSpan? WatchOf(GameId game)
    {
        try
        {
            var path = JsonPath(game);
            return File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllBytes(path), BaselineJsonContext.Default.BaselineJson) is { SchemaVersion: SchemaVersion, WatchMs: > 0, Survived: true } json
                ? TimeSpan.FromMilliseconds(json.WatchMs)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Where the baseline's screenshot goes.</summary>
    public string ScreenshotPath(GameId game) => Path.Combine(Folder, Name(game) + ".png");

    /// <summary>Saves the baseline (best effort: a disk error leaves the game measured again next time).</summary>
    public void Save(GameId game, TestRunBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        try
        {
            Directory.CreateDirectory(Folder);
            var json = new BaselineJson(
                SchemaVersion,
                baseline.Version,
                baseline.Ready is { } ready ? (long)ready.TotalMilliseconds : null,
                (long)baseline.Watch.TotalMilliseconds,
                baseline.Survived,
                (long)baseline.Ran.TotalMilliseconds,
                baseline.Measured.ToUniversalTime(),
                baseline.ExitCode,
                baseline.Screenshot is { } shot ? Path.GetFileName(shot) : null);
            AtomicFile.WriteAllBytes(JsonPath(game), JsonSerializer.SerializeToUtf8Bytes(json, BaselineJsonContext.Default.BaselineJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Where the untranslated game's output of its measured run goes (one line per line it printed, both streams).</summary>
    public string OutputPath(GameId game) => Path.Combine(Folder, Name(game) + ".output.log");

    /// <summary>
    /// The untranslated game's output of its measured run (to compare a crashed translation's with), or <c>null</c>
    /// when none is kept (measured before the output was kept: the game is measured again).
    /// </summary>
    public IReadOnlyList<string>? ReadOutput(GameId game)
    {
        try
        {
            var path = OutputPath(game);
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps the untranslated game's output with its baseline (best effort).</summary>
    public void SaveOutput(GameId game, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        try
        {
            Directory.CreateDirectory(Folder);
            AtomicFile.WriteAllBytes(OutputPath(game), Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The files of the game's folder its test runs changed or deleted (relative, '/'-separated; any version): the next
    /// runs back them up first (<see cref="Core.Install.GameWriteGuard"/>). Empty when none are known.
    /// </summary>
    public IReadOnlyList<string> Writes(GameId game)
    {
        try
        {
            var path = WritesPath(game);
            return File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllBytes(path), BaselineJsonContext.Default.WritesJson) is { SchemaVersion: SchemaVersion, Files: { } files }
                ? [.. files.Where(f => !string.IsNullOrWhiteSpace(f))]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds to <see cref="Writes"/> (at most <see cref="MaxWrites"/> kept; best effort: a disk error forgets them).</summary>
    public void AddWrites(GameId game, IReadOnlyCollection<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var known = Writes(game);
        var added = files.Where(f => !known.Contains(f, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (added.Count == 0 || known.Count >= MaxWrites)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var all = known.Concat(added).Take(MaxWrites).Order(StringComparer.Ordinal).ToList();
            AtomicFile.WriteAllBytes(WritesPath(game), JsonSerializer.SerializeToUtf8Bytes(new WritesJson(SchemaVersion, all), BaselineJsonContext.Default.WritesJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string JsonPath(GameId game) => Path.Combine(Folder, Name(game) + ".json");

    private string WritesPath(GameId game) => Path.Combine(Folder, Name(game) + ".writes.json");

    private static string Name(GameId game) => game.ToString().Replace(':', '-');
}

internal sealed record BaselineJson(
    int SchemaVersion,
    string? Version,
    long? ReadyMs,
    long WatchMs,
    bool Survived,
    long RanMs,
    DateTimeOffset Measured,
    int? ExitCode = null,
    string? Screenshot = null);

internal sealed record WritesJson(int SchemaVersion, IReadOnlyList<string>? Files);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BaselineJson))]
[JsonSerializable(typeof(WritesJson))]
internal sealed partial class BaselineJsonContext : JsonSerializerContext;
