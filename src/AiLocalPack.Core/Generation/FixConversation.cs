using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>Who said a turn of the fix conversation (task 75).</summary>
public enum FixTurnKind
{
    /// <summary>The player's request (or answer to the agent's question).</summary>
    Player,

    /// <summary>The agent's answer: words, the steps it took, a change (a new draft), a plan or a question.</summary>
    Agent,
}

/// <summary>What the player sent with a request (design §6e: chips with ✕).</summary>
public enum FixAttachmentKind
{
    /// <summary>The game session that ended too soon: how long it ran, its exit code.</summary>
    Session,

    /// <summary>The end of the game's own log file.</summary>
    GameLog,

    /// <summary>What the current draft changes in the game (files, strings).</summary>
    DraftChanges,

    /// <summary>An image the player added (copied into the job folder).</summary>
    Screenshot,

    /// <summary>Strings the player picked by key.</summary>
    Strings,
}

/// <param name="Name">The file's name (log, screenshot); empty for the others.</param>
/// <param name="Path">The file the agent reads (log, screenshot), in the job folder's <c>fix/attachments/</c>.</param>
/// <param name="Keys">The picked strings' keys (<see cref="FixAttachmentKind.Strings"/>).</param>
/// <param name="Seconds">How long the game ran (<see cref="FixAttachmentKind.Session"/>).</param>
/// <param name="ExitCode">Its exit code, when known.</param>
/// <param name="Lines">Lines of the log kept.</param>
public sealed record FixAttachment(
    FixAttachmentKind Kind,
    string Name = "",
    string? Path = null,
    IReadOnlyList<string>? Keys = null,
    double? Seconds = null,
    int? ExitCode = null,
    int? Lines = null)
{
    public static FixAttachment Session(TimeSpan lasted, int? exitCode) => new(FixAttachmentKind.Session, Seconds: Math.Round(lasted.TotalSeconds, 1), ExitCode: exitCode);

    public static FixAttachment Changes() => new(FixAttachmentKind.DraftChanges);

    public static FixAttachment ForStrings(IReadOnlyList<string> keys) => new(FixAttachmentKind.Strings, Keys: keys);
}

/// <summary>How an agent turn ended.</summary>
public enum FixTurnOutcome
{
    /// <summary>The agent only answered (or asked): nothing changed.</summary>
    Answered,

    /// <summary>Its changes were built into a new draft.</summary>
    Changed,

    /// <summary>Its change touches more than <see cref="FixRules.PlanThreshold"/> strings: held as a plan for the player.</summary>
    Planned,

    /// <summary>The run or the build failed; nothing changed.</summary>
    Failed,

    /// <summary>The player stopped it; nothing changed.</summary>
    Stopped,
}

/// <summary>One string a fix changed: before → after (empty = no translation).</summary>
public sealed record FixStringChange(string Key, string Original, string Before, string After);

/// <summary>What a fix changed and the draft it made.</summary>
/// <param name="Draft">The new draft's number.</param>
/// <param name="Strings">Translations changed (added, changed or removed).</param>
/// <param name="Terms">Glossary entries changed.</param>
/// <param name="Scripts">The agent's own extract / rebuild scripts changed (unknown engines).</param>
/// <param name="TestRun">The Build stage's test run of the new draft.</param>
/// <param name="Samples">A few of the changed strings, for the before / after rows.</param>
public sealed record FixChange(int Draft, int Strings, int Terms, bool Scripts, TestRunOutcome TestRun, IReadOnlyList<FixStringChange> Samples);

/// <summary>Whether the player acted on a plan.</summary>
public enum FixPlanState
{
    Waiting,
    Applied,
    Dropped,
}

/// <summary>A change too large to apply without asking (design §6e "Plan first"): held in <c>fix/pending/</c> until the player applies or drops it.</summary>
public sealed record FixPlan(int Strings, int Terms, bool Scripts, IReadOnlyList<FixStringChange> Samples, FixPlanState State = FixPlanState.Waiting);

/// <summary>A turn of the fix conversation.</summary>
/// <param name="Id">1, 2, 3… in order.</param>
/// <param name="Text">The player's words, or the agent's reply without its choices line.</param>
/// <param name="Steps">What the agent did, one line each (its tool calls), for the ✓ list.</param>
/// <param name="Choices">Answers the agent offers to its question (the chips).</param>
/// <param name="Problem">Why it failed, in English (<see cref="FixTurnOutcome.Failed"/>).</param>
public sealed record FixTurn(
    int Id,
    FixTurnKind Kind,
    DateTimeOffset At,
    string Text,
    IReadOnlyList<FixAttachment> Attachments,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Choices,
    FixTurnOutcome? Outcome = null,
    FixChange? Change = null,
    FixPlan? Plan = null,
    string? Problem = null,
    string? Model = null)
{
    public static FixTurn FromPlayer(int id, DateTimeOffset at, string text, IReadOnlyList<FixAttachment> attachments) =>
        new(id, FixTurnKind.Player, at, text, attachments, [], []);
}

/// <summary>A draft of the job's translation: the first build, then one per fix.</summary>
/// <param name="Number">1, 2, 3…</param>
/// <param name="Created">When it was built.</param>
/// <param name="Turn">The agent turn that made it (none for Draft 1).</param>
public sealed record FixDraft(int Number, DateTimeOffset Created, int? Turn = null);

/// <summary>The fix conversation of a completed job and its drafts (task 75); <see cref="Current"/> is the one in Packs → Mine.</summary>
public sealed record FixState(IReadOnlyList<FixTurn> Turns, IReadOnlyList<FixDraft> Drafts, int Current)
{
    public static FixState Empty { get; } = new([], [], 1);

    public int NextTurnId => Turns.Count == 0 ? 1 : Turns.Max(t => t.Id) + 1;

    public int NextDraft => Drafts.Count == 0 ? 2 : Drafts.Max(d => d.Number) + 1;

    /// <summary>The plan waiting for the player, if any (only the last one can).</summary>
    public FixTurn? WaitingPlan => Turns.LastOrDefault(t => t.Plan is { State: FixPlanState.Waiting });

    public FixState With(FixTurn turn) => this with { Turns = [.. Turns.Where(t => t.Id != turn.Id), turn] };
}

/// <summary>Constants of fixing with AI.</summary>
public static class FixRules
{
    /// <summary>A change of more strings than this comes as a plan to approve first (decided 2026-11-14).</summary>
    public const int PlanThreshold = 50;

    /// <summary>Before / after rows a change card or a plan shows.</summary>
    public const int Samples = 3;

    /// <summary>Longest request the player may send.</summary>
    public const int MaxRequestLength = 4_000;
}

/// <summary>
/// The fix files of a job folder (task 75): <c>fix/state.json</c> (the conversation and the drafts),
/// <c>fix/drafts/&lt;n&gt;/</c> (a draft that isn't current: its local pack and the job's strings, glossary and agent
/// scripts), <c>fix/before/</c> (the job's state while a fix runs), <c>fix/pending/</c> (a plan's changes) and
/// <c>fix/attachments/</c> (logs and screenshots the player sent). Reading never throws for disk state: a broken
/// <c>state.json</c> is moved aside (<c>state.json.&lt;utc&gt;.bak</c>) and reads as empty; a newer one reads as empty
/// and is never overwritten.
/// </summary>
public sealed class FixFiles(string jobFolder)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new(FixJsonContext.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string JobFolder { get; } = jobFolder;

    public string Folder => Path.Combine(JobFolder, "fix");

    public string StatePath => Path.Combine(Folder, "state.json");

    public string BeforeFolder => Path.Combine(Folder, "before");

    public string PendingFolder => Path.Combine(Folder, "pending");

    public string AttachmentsFolder => Path.Combine(Folder, "attachments");

    public string DraftFolder(int number) => Path.Combine(Folder, "drafts", number.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The pack copy of a draft that isn't current.</summary>
    public string DraftPackFolder(int number) => Path.Combine(DraftFolder(number), "pack");

    /// <summary>The job's strings, glossary and agent scripts of a draft that isn't current.</summary>
    public string DraftJobFolder(int number) => Path.Combine(DraftFolder(number), "job");

    /// <summary>Copies a file the player attached (a screenshot) into <c>fix/attachments/</c> under a free name; returns the copy.</summary>
    /// <exception cref="IOException">It can't be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">It can't be copied.</exception>
    public FixAttachment AddScreenshot(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var target = FreePath(System.IO.Path.GetFileName(source));
        File.Copy(source, target);
        return new FixAttachment(FixAttachmentKind.Screenshot, System.IO.Path.GetFileName(source), target);
    }

    /// <summary>Keeps the lines of a game log in <c>fix/attachments/</c> (the game may write it again); returns the attachment.</summary>
    /// <exception cref="IOException">It can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">It can't be written.</exception>
    public FixAttachment AddLog(string name, IReadOnlyList<string> lines)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(lines);
        var target = FreePath(name);
        AtomicFile.WriteAllBytes(target, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        return new FixAttachment(FixAttachmentKind.GameLog, name, target, Lines: lines.Count);
    }

    private string FreePath(string name)
    {
        Directory.CreateDirectory(AttachmentsFolder);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var extension = System.IO.Path.GetExtension(name);
        if (stem.Length == 0)
        {
            stem = "file";
        }

        var path = System.IO.Path.Combine(AttachmentsFolder, stem + extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(AttachmentsFolder, $"{stem}-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}{extension}");
        }

        return path;
    }

    /// <summary>The state, <see cref="FixState.Empty"/> when there is none (or it is broken or newer).</summary>
    public FixState Read() => ReadOrNull() ?? FixState.Empty;

    private FixState? ReadOrNull()
    {
        byte[] data;
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            data = File.ReadAllBytes(StatePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        FixStateJson? json;
        try
        {
            json = JsonSerializer.Deserialize(data, FixJsonContext.Default.FixStateJson);
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is { SchemaVersion: > SchemaVersion })
        {
            return null;
        }

        if (json is not { SchemaVersion: SchemaVersion })
        {
            MoveAside();
            return null;
        }

        return FixJson.ToState(json);
    }

    /// <summary>Whether <c>state.json</c> is of a newer app (then it is never written).</summary>
    public bool IsNewer
    {
        get
        {
            try
            {
                return File.Exists(StatePath)
                    && JsonSerializer.Deserialize(File.ReadAllBytes(StatePath), FixJsonContext.Default.FixStateJson) is { SchemaVersion: > SchemaVersion };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>Writes the state atomically.</summary>
    /// <exception cref="IOException">It can't be written, or the stored one is of a newer app.</exception>
    /// <exception cref="UnauthorizedAccessException">It can't be written.</exception>
    public void Write(FixState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsNewer)
        {
            throw new IOException($"{StatePath} was written by a newer version of the app.");
        }

        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllBytes(StatePath, JsonSerializer.SerializeToUtf8Bytes(FixJson.ToJson(state), WriteOptions.GetTypeInfo(typeof(FixStateJson))));
    }

    private void MoveAside()
    {
        try
        {
            File.Move(StatePath, $"{StatePath}.{DateTime.UtcNow:yyyyMMddTHHmmssZ}.bak", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read as empty anyway; the next write replaces it.
        }
    }
}

internal static class FixJson
{
    public static FixStateJson ToJson(FixState state) => new(
        FixFiles.SchemaVersion,
        state.Current,
        [.. state.Drafts.Select(d => new FixDraftJson(d.Number, d.Created.ToUniversalTime(), d.Turn))],
        [.. state.Turns.Select(t => new FixTurnJson(
            t.Id,
            Name(t.Kind),
            t.At.ToUniversalTime(),
            t.Text,
            t.Attachments.Count == 0 ? null : [.. t.Attachments.Select(a => new FixAttachmentJson(Name(a.Kind), a.Name.Length == 0 ? null : a.Name, a.Path, a.Keys, a.Seconds, a.ExitCode, a.Lines))],
            t.Steps.Count == 0 ? null : t.Steps,
            t.Choices.Count == 0 ? null : t.Choices,
            t.Outcome is { } o ? Name(o) : null,
            t.Change is { } c ? new FixChangeJson(c.Draft, c.Strings, c.Terms, c.Scripts ? true : null, Name(c.TestRun), Samples(c.Samples)) : null,
            t.Plan is { } p ? new FixPlanJson(p.Strings, p.Terms, p.Scripts ? true : null, Samples(p.Samples), Name(p.State)) : null,
            t.Problem,
            t.Model))]);

    public static FixState ToState(FixStateJson json)
    {
        var drafts = (json.Drafts ?? []).OfType<FixDraftJson>().Where(d => d.Number >= 1).GroupBy(d => d.Number).Select(g => g.First())
            .Select(d => new FixDraft(d.Number, d.Created, d.Turn)).OrderBy(d => d.Number).ToList();
        var turns = new List<FixTurn>();
        foreach (var t in (json.Turns ?? []).OfType<FixTurnJson>())
        {
            if (t.Id < 1 || !TryParse<FixTurnKind>(t.Kind, out var kind) || turns.Any(x => x.Id == t.Id))
            {
                continue;
            }

            turns.Add(new FixTurn(
                t.Id,
                kind,
                t.At,
                t.Text ?? string.Empty,
                [.. (t.Attachments ?? []).OfType<FixAttachmentJson>()
                    .Where(a => TryParse<FixAttachmentKind>(a.Kind, out _))
                    .Select(a => new FixAttachment(Enum.Parse<FixAttachmentKind>(a.Kind!, ignoreCase: true), a.Name ?? string.Empty, a.Path, a.Keys, a.Seconds, a.ExitCode, a.Lines))],
                [.. (t.Steps ?? []).OfType<string>()],
                [.. (t.Choices ?? []).OfType<string>()],
                TryParse<FixTurnOutcome>(t.Outcome, out var outcome) ? outcome : null,
                t.Change is { Draft: >= 1 } c ? new FixChange(c.Draft, c.Strings, c.Terms, c.Scripts == true,
                    TryParse<TestRunOutcome>(c.TestRun, out var run) ? run : TestRunOutcome.Skipped, Samples(c.Samples)) : null,
                t.Plan is { } p ? new FixPlan(p.Strings, p.Terms, p.Scripts == true, Samples(p.Samples),
                    TryParse<FixPlanState>(p.State, out var state) ? state : FixPlanState.Dropped) : null,
                t.Problem,
                t.Model));
        }

        var current = drafts.Any(d => d.Number == json.Current) || json.Current == 1 ? json.Current : 1;
        return new FixState([.. turns.OrderBy(t => t.Id)], drafts, current);
    }

    private static List<FixStringChangeJson>? Samples(IReadOnlyList<FixStringChange> samples) =>
        samples.Count == 0 ? null : [.. samples.Select(s => new FixStringChangeJson(s.Key, s.Original, s.Before, s.After))];

    private static List<FixStringChange> Samples(IReadOnlyList<FixStringChangeJson?>? samples) =>
        [.. (samples ?? []).OfType<FixStringChangeJson>().Where(s => s.Key is { Length: > 0 })
            .Select(s => new FixStringChange(s.Key!, s.Original ?? string.Empty, s.Before ?? string.Empty, s.After ?? string.Empty))];

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static bool TryParse<T>(string? text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !int.TryParse(text, out _);
}

internal sealed record FixStateJson(int SchemaVersion, int Current, IReadOnlyList<FixDraftJson?>? Drafts, IReadOnlyList<FixTurnJson?>? Turns);

internal sealed record FixDraftJson(int Number, DateTimeOffset Created, int? Turn = null);

internal sealed record FixTurnJson(
    int Id,
    string? Kind,
    DateTimeOffset At,
    string? Text,
    IReadOnlyList<FixAttachmentJson?>? Attachments = null,
    IReadOnlyList<string?>? Steps = null,
    IReadOnlyList<string?>? Choices = null,
    string? Outcome = null,
    FixChangeJson? Change = null,
    FixPlanJson? Plan = null,
    string? Problem = null,
    string? Model = null);

internal sealed record FixAttachmentJson(string? Kind, string? Name = null, string? Path = null, IReadOnlyList<string>? Keys = null, double? Seconds = null, int? ExitCode = null, int? Lines = null);

internal sealed record FixChangeJson(int Draft, int Strings, int Terms, bool? Scripts, string? TestRun, IReadOnlyList<FixStringChangeJson?>? Samples = null);

internal sealed record FixPlanJson(int Strings, int Terms, bool? Scripts, IReadOnlyList<FixStringChangeJson?>? Samples, string? State);

internal sealed record FixStringChangeJson(string? Key, string? Original, string? Before, string? After);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FixStateJson))]
internal sealed partial class FixJsonContext : JsonSerializerContext;
