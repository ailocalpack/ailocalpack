using System.CommandLine;
using System.Globalization;
using AiLocalPack.Agents;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>agent run --fake &lt;recording.jsonl&gt; [prompt] [--cwd &lt;dir&gt;] [--json]</c> (task 40): runs an agent headless and prints
/// its normalized events, one line each (<c>--json</c>: <see cref="AgentEventJson"/> lines); exit 0 when the run succeeded,
/// else 1; Ctrl+C kills the agent's process tree. <c>--fake</c> replays a recording through the CLI itself (hidden
/// <c>agent replay &lt;recording&gt;</c>, the <see cref="FakeAgentReplay"/> program); real agents join with tasks 41 / 42.
/// </summary>
internal static class AgentCommand
{
    public static Command Build()
    {
        return new Command("agent", "Run AI agents headless (Claude Code, Codex) and see their normalized events.")
        {
            BuildRun(),
            BuildReplay(),
        };
    }

    private static Command BuildRun()
    {
        var words = new Argument<string[]>("agent-and-prompt")
        {
            Description = "The agent and the prompt; with --fake only the prompt.",
            Arity = new ArgumentArity(0, 2),
        };
        var fake = new Option<string?>("--fake") { Description = "Run the fake agent, replaying this JSONL recording (see FakeAgentReplay)." };
        var cwd = new Option<string?>("--cwd") { Description = "Folder the agent works in (default: the current one)." };
        var json = new Option<bool>("--json") { Description = "Print each event as a JSON line." };
        var run = new Command("run", "Run an agent on a prompt and print its events.") { words, fake, cwd, json };
        run.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var given = result.GetValue(words) ?? [];
            if (result.GetValue(fake) is not { } recording)
            {
                error.WriteLine(given.Length == 0
                    ? "Name an agent, or replay a recording with --fake <recording.jsonl>."
                    : $"Unknown agent \"{given[0]}\": only the fake agent (--fake <recording.jsonl>) runs for now.");
                return 1;
            }

            if (given.Length > 1)
            {
                error.WriteLine("With --fake give only the prompt.");
                return 1;
            }

            recording = Path.GetFullPath(recording);
            if (!File.Exists(recording))
            {
                error.WriteLine($"{recording}: no such recording.");
                return 1;
            }

            var folder = Path.GetFullPath(result.GetValue(cwd) ?? Directory.GetCurrentDirectory());
            if (!Directory.Exists(folder))
            {
                error.WriteLine($"{folder}: no such folder.");
                return 1;
            }

            var adapter = new FakeAgentAdapter(new ProcessRunner(), FakeAgentReplayer.ForCurrentProcess("agent", "replay"), recording);
            var installation = await adapter.DetectAsync(cancellationToken);
            var request = new AgentRunRequest(given.Length == 1 ? given[0] : string.Empty, folder);
            var outcome = AgentOutcome.Failed;
            await foreach (var e in adapter.RunAsync(installation!, request, cancellationToken))
            {
                output.WriteLine(result.GetValue(json) ? AgentEventJson.Write(e) : Describe(e));
                if (e is AgentDone done)
                {
                    outcome = done.Outcome;
                }
            }

            return outcome == AgentOutcome.Succeeded ? 0 : 1;
        });
        return run;
    }

    private static Command BuildReplay()
    {
        var recording = new Argument<string>("recording") { Description = "The JSONL recording to replay." };
        var replay = new Command("replay", "The fake agent's program: replays a recording as an agent's output.") { recording };
        replay.Hidden = true;
        replay.SetAction((result, cancellationToken) =>
            FakeAgentReplay.RunOnConsoleAsync(result.GetValue(recording)!, FakeAgentReplayer.ForCurrentProcess("agent", "replay"), cancellationToken));
        return replay;
    }

    /// <summary>One line per event, a fixed-width label first.</summary>
    internal static string Describe(AgentEvent e) => e switch
    {
        AgentSessionStarted s => $"session  {s.SessionId ?? "-"}" + (s.Model is null ? string.Empty : $" (model {s.Model})"),
        AgentMessage m => $"message  {OneLine(m.Text)}",
        AgentToolCall c => $"tool     {c.Id} {c.Name} {OneLine(c.Input)}",
        AgentToolResult r => $"result   {r.CallId} {(r.IsError ? "error" : "ok")}: {OneLine(r.Output)}",
        AgentUsage u => string.Create(
            CultureInfo.InvariantCulture,
            $"usage    in {u.InputTokens} · out {u.OutputTokens} · cache read {u.CacheReadTokens} · cache write {u.CacheWriteTokens}")
            + (u.CostUsd is { } cost ? string.Create(CultureInfo.InvariantCulture, $" · ${cost}") : string.Empty),
        AgentLimitReached l => $"limit    {(l.ResetsAt is { } at ? "resets " + at.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) : "reset time unknown")}: {OneLine(l.Message)}",
        AgentError x => $"error    {x.Kind}: {OneLine(x.Message)}",
        AgentDiagnostic d => $"stderr   {d.Text}",
        AgentDone d => $"done     {d.Outcome}"
            + (d.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $" (exit {code})") : string.Empty)
            + (d.Result is { } text ? $": {OneLine(text)}" : string.Empty)
            + (d.Message is { } message && d.Outcome != AgentOutcome.Succeeded ? $" | {OneLine(message)}" : string.Empty),
        _ => e.ToString(),
    };

    private static string OneLine(string text) => text.ReplaceLineEndings(" ⏎ ");
}
