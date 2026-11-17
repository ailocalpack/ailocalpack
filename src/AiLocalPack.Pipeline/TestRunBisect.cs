using System.Globalization;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>What narrowing a crash down found.</summary>
/// <param name="Crashing">The smallest set of rebuilt files found that still crashes the game (one file when it went well); empty = not narrowed.</param>
/// <param name="Runs">Quick runs made.</param>
/// <param name="Summary">Plain English for the agent's prompt and the log.</param>
public sealed record TestRunBisectResult(IReadOnlyList<string> Crashing, int Runs, string Summary);

/// <summary>
/// Narrows a crashed test run down to the rebuilt file that crashes the game (decided 2026-11-17), before the agent
/// repairs it: quick runs (<see cref="TestRunRequest.Quick"/>) with half of the remaining files laid over the game and
/// the rest original; crashed = the culprit is in that half, passed = in the other one. The one file left is run alone
/// to confirm, unless the last run already did. At most <paramref name="maxRuns"/> runs; a skipped run (the game
/// couldn't be started) stops it. Assumes one file is enough to crash the game; when the confirming run passes, the
/// summary says so and names the last set that crashed.
/// </summary>
public sealed class TestRunBisect(IGameTestRunner runner, int maxRuns = TestRunBisect.DefaultMaxRuns)
{
    public const int DefaultMaxRuns = 8;

    /// <param name="files">The rebuilt files of the crashed run (relative, '/'-separated).</param>
    /// <param name="onRun">Told before each run with its number and the most.</param>
    public async Task<TestRunBisectResult> RunAsync(
        Game game,
        string gameFolder,
        string buildFolder,
        IReadOnlyList<string> files,
        string jobFolder,
        Action<string> log,
        Action<int, int>? onRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(log);
        var candidates = files.Order(StringComparer.Ordinal).ToList();
        if (candidates.Count <= 1)
        {
            return new TestRunBisectResult(candidates, 0, candidates.Count == 1 ? $"Only one file was rebuilt: `{candidates[0]}`." : "No rebuilt files.");
        }

        var runs = 0;
        var notes = new List<string>();
        var confirmed = false;
        while (candidates.Count > 1 && runs < maxRuns)
        {
            var half = candidates.Take(candidates.Count / 2).ToList();
            var outcome = await RunAsync(half, ++runs).ConfigureAwait(false);
            if (outcome is null)
            {
                return Stopped();
            }

            notes.Add(Note(half, outcome));
            candidates = outcome.Outcome == TestRunOutcome.Crashed ? half : [.. candidates.Skip(half.Count)];
            confirmed = outcome.Outcome == TestRunOutcome.Crashed && candidates.Count == 1;
        }

        if (candidates.Count == 1 && !confirmed && runs < maxRuns)
        {
            var outcome = await RunAsync(candidates, ++runs).ConfigureAwait(false);
            if (outcome is null)
            {
                return Stopped();
            }

            notes.Add(Note(candidates, outcome));
            if (outcome.Outcome != TestRunOutcome.Crashed)
            {
                return Done([], "No single file crashes the game on its own: it takes several rebuilt files together (or the crash doesn't come every time).");
            }

            confirmed = true;
        }

        return candidates.Count == 1 && confirmed
            ? Done(candidates, $"The game crashes with only `{candidates[0]}` laid over it, and gets past loading without it.")
            : Done(candidates, $"Narrowed down to {candidates.Count} files (out of quick runs): {string.Join(", ", candidates.Take(20).Select(f => $"`{f}`"))}{(candidates.Count > 20 ? ", …" : "")}.");

        async Task<TestRunResult?> RunAsync(List<string> laid, int number)
        {
            onRun?.Invoke(number, maxRuns);
            log($"Narrowing the crash down: quick run {number} with {laid.Count} of {files.Count} rebuilt files.");
            var result = await runner.RunAsync(new TestRunRequest(game, gameFolder, buildFolder, laid, jobFolder, log) { Quick = true }, cancellationToken).ConfigureAwait(false);
            return result.Outcome == TestRunOutcome.Skipped ? null : result;
        }

        TestRunBisectResult Stopped() => Done([], "The game couldn't be started for a quick run, so the crash wasn't narrowed down.");

        TestRunBisectResult Done(IReadOnlyList<string> crashing, string verdict)
        {
            var summary = verdict + (notes.Count > 0 ? "\nQuick runs:\n" + string.Join('\n', notes) : string.Empty);
            log($"Narrowing the crash down: {verdict}");
            return new TestRunBisectResult(crashing, runs, summary);
        }
    }

    private static string Note(IReadOnlyList<string> laid, TestRunResult result)
    {
        var what = laid.Count == 1 ? $"`{laid[0]}` alone" : $"{laid.Count} files ({string.Join(", ", laid.Take(3).Select(f => $"`{f}`"))}{(laid.Count > 3 ? ", …" : "")})";
        var ran = result.Ran is { } t ? string.Create(CultureInfo.InvariantCulture, $" after {t.TotalSeconds:0.#} s") : "";
        return result.Outcome == TestRunOutcome.Crashed ? $"- {what}: crashed{ran}" : $"- {what}: got past loading";
    }
}
