using System.CommandLine;
using System.Globalization;
using AiLocalPack.Agents;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Pipeline;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// Fixes with AI (task 75): <c>gen fix &lt;game&gt; "&lt;request&gt;" [--attach &lt;file&gt;]… [--fake-agent]</c> sends a
/// request about the game's last translation to its job's agent and prints the answer (steps, reply, choices, the new
/// draft or the plan); <c>gen fix &lt;game&gt; --approve | --drop</c> applies or drops the plan waiting for the player;
/// <c>gen drafts &lt;game&gt; [--back &lt;n&gt;]</c> lists the drafts and the conversation, or makes draft n current again.
/// </summary>
internal static partial class GenJobCommands
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    private static Command BuildFix(OsEnvironment env)
    {
        var game = GameArgument();
        var request = new Argument<string?>("request") { Description = "What is wrong or what to change, in your own words.", Arity = ArgumentArity.ZeroOrOne };
        var attach = new Option<string[]>("--attach") { Description = "A screenshot (png, jpg, webp…) or a log file to send with the request; repeatable.", AllowMultipleArgumentsPerToken = false };
        var approve = new Option<bool>("--approve") { Description = "Apply the plan the agent left for a large change (builds the next draft)." };
        var drop = new Option<bool>("--drop") { Description = "Drop the plan the agent left (change nothing)." };
        var fake = FakeAgentOption();
        var command = new Command("fix", "Ask the agent to fix the game's last translation; each change builds the next draft.") { game, request, attach, approve, drop, fake };
        command.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context || Completed(context, error) is not { } entry)
            {
                return 1;
            }

            var text = result.GetValue(request);
            var approving = result.GetValue(approve);
            var dropping = result.GetValue(drop);
            if ((approving ? 1 : 0) + (dropping ? 1 : 0) + (text is null ? 0 : 1) != 1)
            {
                error.WriteLine("Give a request, or --approve, or --drop.");
                return 1;
            }

            var recording = result.GetValue(fake) is { } path ? Path.GetFullPath(path) : null;
            if (recording is null && entry.Job.Agent == FakeAgentAdapter.AgentId && !dropping)
            {
                error.WriteLine("The job ran with the fake agent: give its recording with --fake-agent.");
                return 1;
            }

            if (recording is null && !dropping && !AgentAdapters.Ids.Contains(entry.Job.Agent))
            {
                error.WriteLine($"The job's agent '{entry.Job.Agent}' is unknown to this version.");
                return 1;
            }

            var plan = new StagePlan(recording is null ? entry.Job.Agent : FakeAgentAdapter.AgentId, recording, null, null);
            var fixer = context.Fixer(plan);
            var fixFiles = new FixFiles(entry.Folder);
            var progress = new Progress<FixProgress>(_ => { });
            try
            {
                FixRunner.Recover(entry, TimeProvider.System);
                if (dropping)
                {
                    var waiting = fixFiles.Read().WaitingPlan ?? throw new FixException(FixProblem.NotFound, "No plan waits for you.");
                    fixer.DropPlan(entry, waiting.Id);
                    output.WriteLine("Plan dropped: nothing changed.");
                    return 0;
                }

                FixTurn turn;
                if (approving)
                {
                    var waiting = fixFiles.Read().WaitingPlan ?? throw new FixException(FixProblem.NotFound, "No plan waits for you.");
                    turn = await fixer.ApplyPlanAsync(entry, waiting.Id, progress, cancellationToken);
                }
                else
                {
                    var attachments = new List<FixAttachment>();
                    foreach (var file in result.GetValue(attach) ?? [])
                    {
                        var full = Path.GetFullPath(file);
                        if (!File.Exists(full))
                        {
                            error.WriteLine($"{full} doesn't exist.");
                            return 1;
                        }

                        attachments.Add(ImageExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)
                            ? fixFiles.AddScreenshot(full)
                            : Toolkit.Generation.GameLogs.Tail(full) is { } tail
                                ? fixFiles.AddLog(Path.GetFileName(full), tail.Lines)
                                : throw new IOException($"{full} can't be read."));
                    }

                    attachments.Add(FixAttachment.Changes());
                    turn = await fixer.SendAsync(entry, new FixRequest(text!, attachments), progress, cancellationToken);
                }

                Print(output, turn);
                return turn.Outcome == FixTurnOutcome.Failed ? 1 : 0;
            }
            catch (FixException e)
            {
                error.WriteLine(e.Message);
                return 1;
            }
            catch (OperationCanceledException)
            {
                error.WriteLine("Stopped: nothing changed.");
                return 1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine(e.Message);
                return 1;
            }
        });
        return command;
    }

    private static Command BuildDrafts(OsEnvironment env)
    {
        var game = GameArgument();
        var back = new Option<int?>("--back") { Description = "Make this draft current again (the current one is kept)." };
        var command = new Command("drafts", "List the drafts of the game's last translation and the fix conversation.") { game, back };
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (Open(env, result.GetValue(game)!, error) is not { } context || Completed(context, error) is not { } entry)
            {
                return 1;
            }

            FixRunner.Recover(entry, TimeProvider.System);
            if (result.GetValue(back) is { } number)
            {
                try
                {
                    var pack = FixRunner.GoBack(entry, number, context.LocalPacks, TimeProvider.System);
                    output.WriteLine($"Draft {pack.Report.DraftNumber} is current again: {pack.Folder}");
                    return 0;
                }
                catch (FixException e)
                {
                    error.WriteLine(e.Message);
                    return 1;
                }
            }

            var state = new FixFiles(entry.Folder).Read();
            var current = context.LocalPacks.FindByJob(context.Game.Id, entry.Job.Id);
            var numbers = state.Drafts.Select(d => d.Number).Append(current?.Report.DraftNumber ?? 1).Distinct().Order();
            foreach (var n in numbers)
            {
                var report = n == current?.Report.DraftNumber ? current.Report : LocalPackStore.SnapshotReportOf(new FixFiles(entry.Folder).DraftPackFolder(n));
                var mark = n == current?.Report.DraftNumber ? " (current)" : "";
                var played = report?.Crash is { } crash ? string.Create(CultureInfo.InvariantCulture, $", the game closed after {crash.Lasted.TotalSeconds:0.#} s")
                    : report?.Tried is not null ? ", tried in game" : ", not tried yet";
                output.WriteLine($"Draft {n}{mark}: test run {report?.TestRun.Outcome.ToString().ToLowerInvariant() ?? "unknown"}{played}");
            }

            foreach (var turn in state.Turns)
            {
                if (turn.Kind == FixTurnKind.Player)
                {
                    output.WriteLine($"> {turn.Text.ReplaceLineEndings(" ")}");
                }
                else
                {
                    Print(output, turn);
                }
            }

            return 0;
        });
        return command;
    }

    /// <summary>The game's latest completed job whose translation is still among the local packs.</summary>
    private static GenerationJobEntry? Completed(JobContext context, TextWriter error)
    {
        var entry = context.Jobs.Store.List()
            .Where(e => e.Job.Game == context.Game.Id && e.Job.Status == GenerationJobStatus.Completed && context.LocalPacks.FindByJob(context.Game.Id, e.Job.Id) is not null)
            .OrderByDescending(e => e.Job.Updated)
            .FirstOrDefault();
        if (entry is null)
        {
            error.WriteLine($"{context.Game.Name} has no finished translation to fix (its job folder or local pack is gone): make one with 'gen run'.");
        }

        return entry;
    }

    private static void Print(TextWriter output, FixTurn turn)
    {
        if (turn.Steps.Count > 0)
        {
            output.WriteLine($"  steps: {string.Join(", ", turn.Steps)}");
        }

        if (turn.Text.Length > 0)
        {
            output.WriteLine(turn.Text);
        }

        if (turn.Choices.Count > 0)
        {
            output.WriteLine($"  choices: {string.Join(" | ", turn.Choices)}");
        }

        if (turn.Change is { } change)
        {
            output.WriteLine($"Draft {change.Draft}: {change.Strings} strings changed, {change.Terms} glossary entries" + (change.Scripts ? ", scripts changed" : "")
                + $", test run {change.TestRun.ToString().ToLowerInvariant()}.");
            PrintSamples(output, change.Samples);
        }
        else if (turn.Plan is { } plan)
        {
            output.WriteLine($"Plan ({plan.State.ToString().ToLowerInvariant()}): {plan.Strings} strings, {plan.Terms} glossary entries" + (plan.Scripts ? ", scripts" : "")
                + (plan.State == FixPlanState.Waiting ? ". Apply it with 'gen fix <game> --approve', or drop it with --drop." : "."));
            PrintSamples(output, plan.Samples);
        }

        if (turn.Outcome == FixTurnOutcome.Failed)
        {
            output.WriteLine($"Failed: {turn.Problem}");
        }
        else if (turn.Outcome == FixTurnOutcome.Stopped)
        {
            output.WriteLine("Stopped: nothing changed.");
        }
    }

    private static void PrintSamples(TextWriter output, IReadOnlyList<FixStringChange> samples)
    {
        foreach (var s in samples)
        {
            output.WriteLine($"  {s.Key}: {(s.Before.Length == 0 ? "(none)" : s.Before)} → {(s.After.Length == 0 ? "(none)" : s.After)}");
        }
    }
}
