using System.Text.Json;
using System.Text.Json.Serialization;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Generation;

/// <summary>The agent's <c>test_run</c> call as the toolkit server hands it to the app.</summary>
/// <param name="Number">The call's number in this fix run (1, 2, …).</param>
/// <param name="Seconds">Keep the translated game running at least this long (the agent asked), or <c>null</c>.</param>
public sealed record TestRunAsk(int Number, int? Seconds);

/// <summary>What the agent gets back from <c>test_run</c> (camelCase JSON, nulls left out).</summary>
/// <param name="Outcome"><c>passed</c>, <c>crashed</c>, <c>skipped</c>, <c>rebuildFirst</c> (an unknown engine: run your rebuild script, then call again; not counted), <c>refused</c> (no runs left) or <c>failed</c> (the app couldn't build or start it).</param>
/// <param name="Message">What happened, in plain English.</param>
/// <param name="RunsLeft">How many test runs this fix may still make.</param>
public sealed record TestRunAnswer(
    string Outcome,
    string Message,
    int RunsLeft,
    double? RanSeconds = null,
    double? LoadedAfterSeconds = null,
    double? WatchedSeconds = null,
    string? ExitCode = null,
    TestRunPictures? Screenshots = null,
    string? GameLogFile = null,
    IReadOnlyList<string>? GameLog = null,
    string? CrashReport = null);

/// <summary>Screenshot files the agent can read: the original once loaded, the translation once loaded, the translation's last picture.</summary>
public sealed record TestRunPictures(string? Original, string? Translated, string? Last);

/// <summary>
/// How the toolkit server (another process, started by the agent) asks the app for a test run during a fix (task 81):
/// files in the job's <c>fix/test-runs/</c>: <c>&lt;n&gt;.ask.json</c> written by the server, <c>&lt;n&gt;.answer.json</c>
/// by the app once the game ran. Both write atomically; the folder is emptied when a fix run starts and ends.
/// </summary>
public sealed class TestRunMailbox(string jobFolder)
{
    public string Folder { get; } = Path.Combine(jobFolder, "fix", "test-runs");

    /// <exception cref="IOException">The ask can't be written.</exception>
    public void Ask(TestRunAsk ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllBytes(AskPath(ask.Number), JsonSerializer.SerializeToUtf8Bytes(ask, TestRunMailboxJson.Default.TestRunAsk));
    }

    /// <summary>The next number to ask with (one more than the highest ask so far).</summary>
    public int NextNumber() => Numbers(".ask.json").DefaultIfEmpty(0).Max() + 1;

    /// <summary>Asks without an answer yet, oldest first; unreadable ones are left out.</summary>
    public IReadOnlyList<TestRunAsk> Pending()
    {
        var answered = Numbers(".answer.json").ToHashSet();
        var list = new List<TestRunAsk>();
        foreach (var number in Numbers(".ask.json").Where(n => !answered.Contains(n)).Order())
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(AskPath(number)), TestRunMailboxJson.Default.TestRunAsk) is { } ask)
                {
                    list.Add(ask with { Number = number });
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return list;
    }

    /// <exception cref="IOException">The answer can't be written.</exception>
    public void Reply(int number, TestRunAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllBytes(AnswerPath(number), JsonSerializer.SerializeToUtf8Bytes(answer, TestRunMailboxJson.Default.TestRunAnswer));
    }

    /// <summary>The answer to ask <paramref name="number"/>, or <c>null</c> while there is none.</summary>
    public TestRunAnswer? Answer(int number)
    {
        try
        {
            var path = AnswerPath(number);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), TestRunMailboxJson.Default.TestRunAnswer) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Deletes the folder (best effort).</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string AskPath(int number) => Path.Combine(Folder, $"{number}.ask.json");

    private string AnswerPath(int number) => Path.Combine(Folder, $"{number}.answer.json");

    private IEnumerable<int> Numbers(string suffix)
    {
        if (!Directory.Exists(Folder))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(Folder, "*" + suffix))
        {
            var name = Path.GetFileName(file);
            if (int.TryParse(name.AsSpan(0, name.Length - suffix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                yield return n;
            }
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TestRunAsk))]
[JsonSerializable(typeof(TestRunAnswer))]
internal sealed partial class TestRunMailboxJson : JsonSerializerContext;
