using System.CommandLine;
using System.Globalization;
using System.Text;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Pipeline;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Trying the job's build by hand (decided 2026-11-17, after Barony's crashes): <c>gen test &lt;game&gt; [--quick]
/// [--files &lt;path&gt;…]</c> runs the test run on the job's current <c>build/</c> folder without rebuilding;
/// <c>gen bisect &lt;game&gt;</c> narrows a crash down to the rebuilt file that causes it with quick runs;
/// <c>gen limits &lt;game&gt; [--set &lt;keys&gt; --bytes &lt;n&gt; --reason "…"] [--remove &lt;keys&gt;]</c> shows and
/// changes the limits learned about the game (<c>limits.json</c>) with the stored translations over them.
/// </summary>
internal static partial class GenJobCommands
{
    private static Command BuildTest(OsEnvironment env)
    {
        var game = GameArgument();
        var quick = new Option<bool>("--quick") { Description = "One short launch until the game has loaded, instead of the full watch and a second launch." };
        var files = new Option<string[]>("--files") { Description = "Lay only these rebuilt files over the game (paths as in the job's build/ folder); repeatable.", AllowMultipleArgumentsPerToken = true };
        var seconds = new Option<int?>("--seconds") { Description = "Keep the translated game running at least this long (at most 180)." };
        var command = new Command("test", "Run the test run on the game's last build (the job's build/ folder) without rebuilding it.") { game, quick, files, seconds };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context || LastJob(context, error) is not { } entry)
            {
                return 1;
            }

            if (result.GetValue(seconds) is < 1 or > 180)
            {
                error.WriteLine("--seconds must be between 1 and 180.");
                return 1;
            }

            if (Built(entry, error) is not { } built)
            {
                return 1;
            }

            var laid = built;
            if (result.GetValue(files) is { Length: > 0 } wanted)
            {
                var asked = wanted.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToList();
                if (asked.Where(f => !built.Contains(f, StringComparer.Ordinal)).ToList() is { Count: > 0 } unknown)
                {
                    error.WriteLine($"Not in the build: {string.Join(", ", unknown)}. Its files: {string.Join(", ", built.Take(40))}{(built.Count > 40 ? ", …" : "")}");
                    return 1;
                }

                laid = asked;
            }

            using var held = GenerationJobLock.TryAcquire(entry.Folder);
            if (held is null)
            {
                error.WriteLine("The job is busy: a generation run or a fix works on it.");
                return 1;
            }

            var job = new GenerationFiles(entry.Folder);
            output.WriteLine($"Test run of {context.Game.Name} with {laid.Count} of {built.Count} rebuilt files{(result.GetValue(quick) ? " (quick)" : "")}: the game opens on the screen, don't close it.");
            var run = await Runner(context).RunAsync(
                new TestRunRequest(context.Game, context.Game.InstallPath, job.BuildFolder, laid, entry.Folder, line => output.WriteLine(line))
                {
                    Quick = result.GetValue(quick),
                    AtLeast = result.GetValue(seconds) is { } s ? TimeSpan.FromSeconds(s) : null,
                },
                cancellationToken);
            output.WriteLine($"{run.Outcome.ToString().ToLowerInvariant()}{(run.Skip is { } skip ? $" ({skip})" : "")}: {run.Detail}");
            if (run.ExitCode is { } code && run.Outcome == TestRunOutcome.Crashed)
            {
                output.WriteLine($"exit code: {CrashReports.ExitCodeText(code)}");
            }

            foreach (var shot in run.Shots ?? [])
            {
                output.WriteLine($"{shot.Kind.ToString().ToLowerInvariant()} screenshot: {shot.Path}");
            }

            output.WriteLine($"output: {Path.Combine(entry.Folder, "logs", "test-run.log")}");
            return run.Outcome == TestRunOutcome.Crashed ? 2 : 0;
        });
        return command;
    }

    private static Command BuildBisect(OsEnvironment env)
    {
        var game = GameArgument();
        var max = new Option<int?>("--max-runs") { Description = $"The most quick runs (default {TestRunBisect.DefaultMaxRuns}, at most 20)." };
        var command = new Command("bisect", "Find the rebuilt file that crashes the game: quick runs with half of the files laid over it, then half of that half.") { game, max };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context || LastJob(context, error) is not { } entry || Built(entry, error) is not { } built)
            {
                return 1;
            }

            if (result.GetValue(max) is < 1 or > 20)
            {
                error.WriteLine("--max-runs must be between 1 and 20.");
                return 1;
            }

            using var held = GenerationJobLock.TryAcquire(entry.Folder);
            if (held is null)
            {
                error.WriteLine("The job is busy: a generation run or a fix works on it.");
                return 1;
            }

            output.WriteLine($"Narrowing down which of {built.Count} rebuilt files crashes {context.Game.Name}: the game opens on the screen for each quick run, don't close it.");
            var found = await new TestRunBisect(Runner(context), result.GetValue(max) ?? TestRunBisect.DefaultMaxRuns).RunAsync(
                context.Game, context.Game.InstallPath, new GenerationFiles(entry.Folder).BuildFolder, built, entry.Folder, line => output.WriteLine(line), null, cancellationToken);
            output.WriteLine(found.Summary);
            return found.Crashing.Count == 1 ? 0 : 2;
        });
        return command;
    }

    private static Command BuildLimits(OsEnvironment env)
    {
        var game = GameArgument();
        var set = new Option<string?>("--set") { Description = "Set a limit for these keys (a key, or a pattern with * for any text, e.g. 'lang/en.txt:*')." };
        var bytes = new Option<int?>("--bytes") { Description = "With --set: the most UTF-8 bytes a translation may take." };
        var reason = new Option<string?>("--reason") { Description = "With --set: why (what crashes the game)." };
        var remove = new Option<string?>("--remove") { Description = "Remove the limit with these keys." };
        var command = new Command("limits", "Show the limits learned about the game (bytes a translation may take, by key pattern) and the stored translations over them; set or remove one.") { game, set, bytes, reason, remove };
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context || LastJob(context, error) is not { } entry)
            {
                return 1;
            }

            var setting = result.GetValue(set);
            var removing = result.GetValue(remove);
            if (setting is not null && removing is not null)
            {
                error.WriteLine("Give --set or --remove, not both.");
                return 1;
            }

            try
            {
                if (setting is not null)
                {
                    if (result.GetValue(bytes) is not (> 0 and var max) || result.GetValue(reason) is not { Length: > 0 } why || setting.Trim('*').Length == 0)
                    {
                        error.WriteLine("--set needs a key or pattern (more than *), --bytes above 0 and --reason.");
                        return 1;
                    }

                    StringLimitsFile.Put(entry.Folder, new StringLimit(setting.Trim(), max, why.Trim()));
                }
                else if (removing is not null)
                {
                    StringLimitsFile.Put(entry.Folder, new StringLimit(removing.Trim(), 0, string.Empty));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.WriteLine($"The limits can't be saved: {e.Message}");
                return 1;
            }

            var limits = StringLimitsFile.Read(entry.Folder);
            if (limits.Count == 0)
            {
                output.WriteLine("No limits.");
                return 0;
            }

            var job = new GenerationFiles(entry.Folder);
            IReadOnlyList<string> keys;
            IReadOnlyDictionary<string, string> stored;
            try
            {
                keys = SourceStringsFile.Read(job)?.Strings.Select(s => s.Key).ToList() ?? [];
                stored = job.ReadStrings()?.Strings ?? new Dictionary<string, string>();
            }
            catch (GenerationFileException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }

            foreach (var limit in limits)
            {
                var over = stored.Where(p => limit.Matches(p.Key) && StringLimit.For(limits, p.Key) == limit && Encoding.UTF8.GetByteCount(p.Value) > limit.MaxBytes)
                    .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{limit.Keys}: at most {limit.MaxBytes} bytes, {keys.Count(limit.Matches):N0} strings, {over.Count} stored translations over it ({limit.Reason})"));
                foreach (var (key, text) in over.Take(20))
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {key}: {Encoding.UTF8.GetByteCount(text)} bytes"));
                }

                if (over.Count > 20)
                {
                    output.WriteLine($"  … and {over.Count - 20} more");
                }
            }

            return 0;
        });
        return command;
    }

    /// <summary>The game's latest job that isn't cancelled (failed or completed ones too), or <c>null</c> after telling why.</summary>
    private static GenerationJobEntry? LastJob(JobContext context, TextWriter error)
    {
        if (context.Jobs.Store.List().LastOrDefault(e => e.Job.Game == context.Game.Id && e.Job.Status != GenerationJobStatus.Cancelled) is { } entry)
        {
            return entry;
        }

        error.WriteLine($"{context.Game.Name} has no generation job: start one with 'gen run'.");
        return null;
    }

    /// <summary>The files of the job's <c>build/</c> folder (relative, '/'-separated), or <c>null</c> after telling why.</summary>
    private static IReadOnlyList<string>? Built(GenerationJobEntry entry, TextWriter error)
    {
        var folder = new GenerationFiles(entry.Folder).BuildFolder;
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
            : [];
        if (files.Count == 0)
        {
            error.WriteLine("The job has no build yet: the Build stage writes it (gen resume).");
            return null;
        }

        return files;
    }

    private static GameTestRunner Runner(JobContext context) => GameTestRunner.ForMachine(new ProcessRunner(), context.Env, context.Paths);
}
