using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.Generation;

/// <summary>A job and its folder.</summary>
public sealed record GenerationJobEntry(string Folder, GenerationJob Job);

public enum GenerationJobProblem
{
    /// <summary>The game already has an open job (one job per game).</summary>
    AlreadyExists,

    /// <summary>A runner (this app, the CLI) works on the job right now.</summary>
    Busy,

    /// <summary>The job folder or its <c>job.json</c> is missing.</summary>
    NotFound,

    /// <summary><c>job.json</c> can't be read or holds something invalid.</summary>
    Unreadable,

    /// <summary>The job's state doesn't allow what was asked (resume a completed job…).</summary>
    WrongState,
}

/// <summary>A job can't be created, opened or changed.</summary>
public sealed class GenerationJobException : Exception
{
    public GenerationJobException(GenerationJobProblem problem, string message, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
    }

    public GenerationJobException()
    {
    }

    public GenerationJobException(string message)
        : base(message)
    {
    }

    public GenerationJobException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public GenerationJobProblem Problem { get; }
}

/// <summary>
/// Held while a runner works on a job: <c>job.lock</c> in the job folder opened with <see cref="FileShare.None"/>, so the
/// app and the CLI never run the same job twice, and a job found <see cref="GenerationJobStatus.Running"/> whose lock can
/// be taken has no runner (its process died).
/// </summary>
public sealed class GenerationJobLock : IDisposable
{
    private readonly FileStream _stream;

    private GenerationJobLock(string folder, FileStream stream)
    {
        Folder = folder;
        _stream = stream;
    }

    public string Folder { get; }

    /// <summary>The lock, or <c>null</c> when another runner holds it.</summary>
    /// <exception cref="IOException">The job folder can't be written.</exception>
    public static GenerationJobLock? TryAcquire(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        try
        {
            var stream = new FileStream(Path.Combine(folder, GenerationJobStore.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new GenerationJobLock(Path.GetFullPath(folder), stream);
        }
        catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException))
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Jobs on disk (task 47): <c>job.json</c> in each job folder <c>&lt;data&gt;/work/&lt;game folder&gt;/&lt;job id&gt;/</c>
/// (<see cref="WorkingCopies.JobFolder"/>), written atomically after every step, next to the <see cref="GenerationFiles"/>
/// and <c>logs/job.log</c>. One open job per game. Methods block; call them off the UI thread.
/// </summary>
public sealed class GenerationJobStore
{
    public const int CurrentSchemaVersion = 1;

    public const string JobFileName = "job.json";

    public const string LockFileName = "job.lock";

    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public GenerationJobStore(AppPaths paths, TimeProvider? time = null, ILogger<GenerationJobStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public static string JobFile(string folder) => Path.Combine(folder, JobFileName);

    public static string LogFile(string folder) => Path.Combine(folder, "logs", "job.log");

    /// <summary>Every readable job, oldest first (by ID); broken ones are logged and left out.</summary>
    public IReadOnlyList<GenerationJobEntry> List()
    {
        var jobs = new List<GenerationJobEntry>();
        if (!Directory.Exists(_paths.WorkingCopies))
        {
            return jobs;
        }

        try
        {
            foreach (var gameFolder in Directory.EnumerateDirectories(_paths.WorkingCopies).Order(StringComparer.Ordinal))
            {
                foreach (var folder in Directory.EnumerateDirectories(gameFolder).Order(StringComparer.Ordinal))
                {
                    if (!File.Exists(JobFile(folder)))
                    {
                        continue;
                    }

                    try
                    {
                        jobs.Add(new GenerationJobEntry(folder, Read(folder)));
                    }
                    catch (GenerationJobException e)
                    {
                        _logger.LogWarning("Generation job in {Folder} left out: {Reason}", folder, e.Message);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't list the generation jobs in {Folder}", _paths.WorkingCopies);
        }

        return jobs;
    }

    /// <summary>The game's open job (not cancelled, not completed), if any.</summary>
    public GenerationJobEntry? FindOpen(GameId game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return List().LastOrDefault(e => e.Job.Game == game && e.Job.IsOpen);
    }

    /// <summary>The job of <paramref name="folder"/>.</summary>
    /// <exception cref="GenerationJobException"><see cref="GenerationJobProblem.NotFound"/> or <see cref="GenerationJobProblem.Unreadable"/>.</exception>
    public static GenerationJob Read(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var path = JobFile(folder);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new GenerationJobException(GenerationJobProblem.NotFound, $"No generation job in {folder}.", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GenerationJobException(GenerationJobProblem.Unreadable, $"{path}: {e.Message}", e);
        }

        var (job, error) = GenerationJobSerializer.Read(data);
        return job ?? throw new GenerationJobException(GenerationJobProblem.Unreadable, $"{path}: {error}");
    }

    /// <exception cref="IOException">The file can't be written.</exception>
    public static void Write(string folder, GenerationJob job)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(job);
        AtomicFile.WriteAllBytes(JobFile(folder), GenerationJobSerializer.Write(job));
    }

    /// <summary>
    /// A new job folder with <c>job.json</c> (running at Setup), locked for the caller's runner. <paramref name="prepare"/>
    /// writes the job's own files first (an update job's base pack, task 56); when it fails, the folder is deleted.
    /// </summary>
    /// <exception cref="GenerationJobException"><see cref="GenerationJobProblem.AlreadyExists"/>: the game has an open job.</exception>
    /// <exception cref="IOException">The folder or file can't be written.</exception>
    public (GenerationJobEntry Entry, GenerationJobLock Lock) Create(
        Game game,
        string language,
        string agent,
        GenerationOptions options,
        TimeSpan? estimatedTime = null,
        Action<GenerationFiles>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (FindOpen(game.Id) is { } open)
        {
            throw new GenerationJobException(
                GenerationJobProblem.AlreadyExists,
                $"{game.Name} already has a generation job ({open.Job.Id}, {open.Job.Status}): resume or cancel it first.");
        }

        var now = _time.GetUtcNow();
        var id = WorkingCopies.NewJobId(_time);
        var folder = Path.Combine(_paths.WorkingCopies, Registry.RegistryLayout.GameFolder(game.Id), id);
        Directory.CreateDirectory(folder);
        var jobLock = GenerationJobLock.TryAcquire(folder)
            ?? throw new GenerationJobException(GenerationJobProblem.Busy, $"The new job folder {folder} is locked.");
        try
        {
            prepare?.Invoke(new GenerationFiles(folder));
        }
        catch
        {
            jobLock.Dispose();
            TryDelete(folder);
            throw;
        }

        try
        {
            var job = GenerationJobMachine.Create(id, game.Id, game.Name, language, agent, options, now, estimatedTime);
            Write(folder, job);
            return (new GenerationJobEntry(folder, job), jobLock);
        }
        catch
        {
            jobLock.Dispose();
            throw;
        }
    }

    private void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "The job folder {Folder} left by a failed start can't be deleted", folder);
        }
    }

    /// <summary>
    /// Crash recovery, at start-up: every job left <see cref="GenerationJobStatus.Running"/> whose lock is free (no
    /// runner: the app or the machine stopped) becomes <see cref="GenerationJobStatus.Interrupted"/>. Returns them. Never
    /// throws (a job that can't be written is logged and stays as it is).
    /// </summary>
    public IReadOnlyList<GenerationJobEntry> Recover()
    {
        var recovered = new List<GenerationJobEntry>();
        foreach (var entry in List().Where(e => e.Job.Status == GenerationJobStatus.Running))
        {
            try
            {
                using var jobLock = GenerationJobLock.TryAcquire(entry.Folder);
                if (jobLock is null)
                {
                    continue;
                }

                var job = Read(entry.Folder);
                if (job.Status != GenerationJobStatus.Running)
                {
                    continue;
                }

                var interrupted = GenerationJobMachine.Apply(job, new GenerationTrigger.Interrupted(), _time.GetUtcNow());
                Write(entry.Folder, interrupted);
                GenerationJobLog.Append(entry.Folder, _time, $"Interrupted at {job.Stage} ({job.Counters.ChunksDone} of {job.Counters.Chunks} chunks done): no runner after a restart.");
                recovered.Add(entry with { Job = interrupted });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or GenerationJobException)
            {
                _logger.LogWarning(e, "Couldn't recover the generation job in {Folder}", entry.Folder);
            }
        }

        return recovered;
    }
}

/// <summary>The job's own log, <c>logs/job.log</c>: one timestamped line per event. Best effort, never throws.</summary>
public static class GenerationJobLog
{
    public static void Append(string folder, TimeProvider time, string line)
    {
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            var path = GenerationJobStore.LogFile(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stamp = time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            File.AppendAllText(path, $"{stamp} {line.ReplaceLineEndings(" ")}\n", Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is a help for the player and for bug reports; a failed line doesn't stop the job.
        }
    }

    /// <summary>
    /// The log's last <paramref name="count"/> lines (read from the end, at most <paramref name="maxBytes"/>; a line cut
    /// by that is left out), oldest first; empty when there is no log or it can't be read. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Tail(string folder, int count, int maxBytes = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        try
        {
            using var stream = new FileStream(GenerationJobStore.LogFile(folder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // One byte more than asked when cutting: if it is a line break, the first line read is whole.
            var start = Math.Max(0, stream.Length - maxBytes - 1);
            stream.Position = start;
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            if (start > 0)
            {
                text = text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..];
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

            return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary><c>job.json</c> ↔ <see cref="GenerationJob"/>. Never throws for bad content.</summary>
public static class GenerationJobSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new(GenerationJobJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Write(GenerationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var file = new GenerationJobFileJson(
            GenerationJobStore.CurrentSchemaVersion,
            job.Id,
            job.Game.ToString(),
            job.GameName,
            job.Language,
            job.Agent,
            new GenerationOptionsJson(
                job.Options.ReviewGlossary,
                Name(job.Options.Scope),
                job.Options.Model,
                job.Options.Base is { } b ? new GenerationBaseJson(b.PackId, b.Revision, b.Local, b.Title) : null),
            Name(job.Stage),
            Name(job.Status),
            Name(job.PauseReason),
            job.ResumeAt?.ToUniversalTime(),
            job.Failure is { } f ? new GenerationFailureJson(Name(f.Stage), Name(f.Kind), f.Message) : null,
            new GenerationCountersJson(job.Counters.Strings, job.Counters.Translated, job.Counters.Chunks, job.Counters.ChunksDone),
            job.Created.ToUniversalTime(),
            job.Updated.ToUniversalTime(),
            (long)job.ActiveTime.TotalMilliseconds,
            job.RunningSince?.ToUniversalTime(),
            (long)job.ChunkTime.TotalMilliseconds,
            job.EstimatedTime is { } e ? (long)e.TotalMilliseconds : null);
        return JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions.GetTypeInfo(typeof(GenerationJobFileJson)));
    }

    public static (GenerationJob? Job, string? Error) Read(ReadOnlySpan<byte> data)
    {
        GenerationJobFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(data, GenerationJobJsonContext.Default.GenerationJobFileJson);
        }
        catch (JsonException e)
        {
            return (null, $"not valid JSON ({e.Message})");
        }

        if (file is null)
        {
            return (null, "is empty");
        }

        if (file.SchemaVersion != GenerationJobStore.CurrentSchemaVersion)
        {
            return (null, file.SchemaVersion > GenerationJobStore.CurrentSchemaVersion
                ? $"schemaVersion {file.SchemaVersion} is newer than this app reads; update the app"
                : $"schemaVersion {file.SchemaVersion} is not supported");
        }

        if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Language) || string.IsNullOrWhiteSpace(file.Agent))
        {
            return (null, "id, language and agent are required");
        }

        if (!GameId.TryParse(file.Game, out var game))
        {
            return (null, $"game '{file.Game}' is not a game ID");
        }

        if (!TryParse<GenerationStage>(file.Stage, out var stage) || !TryParse<GenerationJobStatus>(file.Status, out var status))
        {
            return (null, $"stage '{file.Stage}' or status '{file.Status}' is unknown");
        }

        if (file.Created is not { } created || file.Updated is not { } updated)
        {
            return (null, "created and updated are required");
        }

        GenerationPauseReason? pause = null;
        if (file.PauseReason is not null)
        {
            if (!TryParse<GenerationPauseReason>(file.PauseReason, out var reason))
            {
                return (null, $"pauseReason '{file.PauseReason}' is unknown");
            }

            pause = reason;
        }

        GenerationFailure? failure = null;
        if (file.Failure is { } f)
        {
            if (!TryParse<GenerationStage>(f.Stage, out var failedStage))
            {
                return (null, $"failure.stage '{f.Stage}' is unknown");
            }

            // A kind from a newer app reads as unexpected: the message still says what happened.
            failure = new GenerationFailure(
                failedStage,
                TryParse<GenerationFailureKind>(f.Kind, out var kind) ? kind : GenerationFailureKind.Unexpected,
                f.Message ?? "");
        }

        WorkingCopyScope? scope = null;
        if (file.Options?.Scope is { } scopeName && TryParse<WorkingCopyScope>(scopeName, out var parsedScope))
        {
            scope = parsedScope;
        }

        GenerationBase? baseRef = null;
        if (file.Options?.Base is { } b)
        {
            if (!PackFormat.IsSlug(b.PackId) || b.Revision < 1)
            {
                return (null, "options.base needs a pack ID and a revision");
            }

            baseRef = new GenerationBase(b.PackId!, b.Revision, b.Local, b.Title ?? b.PackId!);
        }

        var counters = file.Counters ?? new GenerationCountersJson(0, 0, 0, 0);
        if (counters.Strings < 0 || counters.Translated < 0 || counters.Chunks < 0 || counters.ChunksDone < 0 || counters.ChunksDone > counters.Chunks)
        {
            return (null, "counters are out of range");
        }

        return (new GenerationJob
        {
            Id = file.Id,
            Game = game,
            GameName = file.GameName ?? game.ToString(),
            Language = file.Language,
            Agent = file.Agent,
            Options = new GenerationOptions(file.Options?.ReviewGlossary ?? false, scope, file.Options?.Model, baseRef),
            Stage = stage,
            Status = status,
            PauseReason = pause,
            ResumeAt = file.ResumeAt,
            Failure = failure,
            Counters = new GenerationCounters(counters.Strings, counters.Translated, counters.Chunks, counters.ChunksDone),
            Created = created,
            Updated = updated,
            ActiveTime = TimeSpan.FromMilliseconds(Math.Max(0, file.ActiveMs)),
            RunningSince = file.RunningSince,
            ChunkTime = TimeSpan.FromMilliseconds(Math.Max(0, file.ChunkMs)),
            EstimatedTime = file.EstimatedMs is { } ms ? TimeSpan.FromMilliseconds(Math.Max(0, ms)) : null,
        }, null);
    }

    private static string Name<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string? Name<T>(T? value)
        where T : struct, Enum => value is { } v ? Name(v) : null;

    private static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;
        return text is not null && !text.Any(char.IsDigit) && Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}

internal sealed record GenerationJobFileJson(
    int SchemaVersion,
    string? Id,
    string? Game,
    string? GameName,
    string? Language,
    string? Agent,
    GenerationOptionsJson? Options,
    string? Stage,
    string? Status,
    string? PauseReason,
    DateTimeOffset? ResumeAt,
    GenerationFailureJson? Failure,
    GenerationCountersJson? Counters,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    long ActiveMs,
    DateTimeOffset? RunningSince,
    long ChunkMs,
    long? EstimatedMs);

internal sealed record GenerationOptionsJson(bool ReviewGlossary, string? Scope, string? Model, GenerationBaseJson? Base = null);

internal sealed record GenerationBaseJson(string? PackId, int Revision, bool Local, string? Title);

internal sealed record GenerationFailureJson(string? Stage, string? Kind, string? Message);

internal sealed record GenerationCountersJson(int Strings, int Translated, int Chunks, int ChunksDone);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GenerationJobFileJson))]
internal sealed partial class GenerationJobJsonContext : JsonSerializerContext;
