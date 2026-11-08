using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Agents;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>agent</c> (tasks 40, 41). <c>agent probe &lt;agent&gt; [--json]</c>: finds the agent, its version and whether it is
/// signed in (exit 0 = found, recent enough and signed in, else 1). <c>agent run &lt;agent&gt; &lt;prompt&gt; [--cwd &lt;dir&gt;]
/// [--allow read|edit|run]… [--model &lt;m&gt;] [--resume &lt;session&gt;] [--system-prompt &lt;text&gt;] [--json]</c> or
/// <c>agent run --fake &lt;recording.jsonl&gt; [prompt]</c>: runs it headless and prints its normalized events, one line each
/// (<c>--json</c>: <see cref="AgentEventJson"/> lines); exit 0 when the run succeeded, else 1; Ctrl+C kills the agent's
/// process tree. <c>--allow</c> = the built-in tools the agent may use inside the folder (default <c>read</c>).
/// <c>--fake</c> replays a recording through the CLI itself (hidden <c>agent replay &lt;recording&gt;</c>, the
/// <see cref="FakeAgentReplay"/> program).
/// </summary>
internal static class AgentCommand
{
    public static Command Build(OsEnvironment env)
    {
        return new Command("agent", "Run AI agents headless (Claude Code, Codex) and see their normalized events.")
        {
            BuildProbe(env),
            BuildRun(env),
            BuildReplay(),
        };
    }

    private static string KnownAgents => string.Join(", ", AgentAdapters.Ids);

    private static Command BuildProbe(OsEnvironment env)
    {
        var agent = new Argument<string>("agent") { Description = $"The agent ({KnownAgents})." };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var probe = new Command("probe", "Find an agent, its version and whether it is signed in.") { agent, json };
        probe.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var id = result.GetValue(agent)!;
            if (AgentAdapters.Create(id, new ProcessRunner(), env) is not { } adapter)
            {
                result.InvocationConfiguration.Error.WriteLine($"Unknown agent \"{id}\": known agents are {KnownAgents}.");
                return 1;
            }

            var installation = await adapter.DetectAsync(cancellationToken);
            var auth = installation is null ? null : await adapter.GetAuthStatusAsync(installation, cancellationToken);
            var supported = installation is not null && IsSupported(adapter, installation.Version);
            if (result.GetValue(json))
            {
                output.WriteLine(ProbeJson(adapter, installation, supported, auth));
            }
            else
            {
                WriteProbe(output, adapter, installation, supported, auth);
            }

            return supported && auth?.State == AgentAuthState.SignedIn ? 0 : 1;
        });
        return probe;
    }

    private static bool IsSupported(IAgentAdapter adapter, string? version) =>
        adapter.Id != ClaudeCodeAdapter.AgentId || ClaudeCodeCommandLine.IsSupported(version);

    private static void WriteProbe(TextWriter output, IAgentAdapter adapter, AgentInstallation? installation, bool supported, AgentAuthStatus? auth)
    {
        output.WriteLine($"{adapter.DisplayName} ({adapter.Id})");
        if (installation is null)
        {
            output.WriteLine("program:  not found (looked on PATH and in the installers' usual folders)");
            return;
        }

        var source = installation.Source switch
        {
            AgentSource.Path => "installed by you",
            AgentSource.Managed => "installed by the app",
            _ => "configured",
        };
        output.WriteLine($"program:  {installation.Executable} ({source})");
        var version = installation.Version ?? "unknown (it didn't answer --version)";
        output.WriteLine(supported || installation.Version is null
            ? $"version:  {version}"
            : $"version:  {version} (older than {ClaudeCodeCommandLine.MinimumVersion}, which the app needs: update it)");
        output.WriteLine(auth!.State switch
        {
            AgentAuthState.SignedIn => "sign-in:  signed in" + (auth.Account is { } account ? $" ({account})" : string.Empty),
            AgentAuthState.SignedOut => "sign-in:  signed out",
            _ => "sign-in:  unknown" + (auth.Detail is { } detail ? $" ({detail})" : string.Empty),
        });
    }

    private static string ProbeJson(IAgentAdapter adapter, AgentInstallation? installation, bool supported, AgentAuthStatus? auth)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("agent", adapter.Id);
            json.WriteString("name", adapter.DisplayName);
            json.WriteBoolean("found", installation is not null);
            json.WriteString("executable", installation?.Executable);
            json.WriteString("source", installation is null ? null : JsonNamingPolicy.CamelCase.ConvertName(installation.Source.ToString()));
            json.WriteString("version", installation?.Version);
            json.WriteBoolean("supported", supported);
            json.WriteString("auth", auth is null ? null : JsonNamingPolicy.CamelCase.ConvertName(auth.State.ToString()));
            json.WriteString("account", auth?.Account);
            json.WriteString("detail", auth?.Detail);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Command BuildRun(OsEnvironment env)
    {
        var words = new Argument<string[]>("agent-and-prompt")
        {
            Description = "The agent and the prompt; with --fake only the prompt.",
            Arity = new ArgumentArity(0, 2),
        };
        var fake = new Option<string?>("--fake") { Description = "Run the fake agent, replaying this JSONL recording (see FakeAgentReplay)." };
        var cwd = new Option<string?>("--cwd") { Description = "Folder the agent works in (default: the current one)." };
        var allow = new Option<string[]>("--allow")
        {
            Description = "Built-in tools the agent may use inside the folder: read, edit, run (default: read).",
            AllowMultipleArgumentsPerToken = true,
        };
        var model = new Option<string?>("--model") { Description = "The model (default: the agent's own)." };
        var resume = new Option<string?>("--resume") { Description = "Continue this session instead of starting one." };
        var systemPrompt = new Option<string?>("--system-prompt") { Description = "Instructions appended to the agent's system prompt." };
        var json = new Option<bool>("--json") { Description = "Print each event as a JSON line." };
        var run = new Command("run", "Run an agent on a prompt and print its events.") { words, fake, cwd, allow, model, resume, systemPrompt, json };
        run.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var given = result.GetValue(words) ?? [];
            var folder = Path.GetFullPath(result.GetValue(cwd) ?? Directory.GetCurrentDirectory());
            IAgentAdapter adapter;
            string prompt;
            if (result.GetValue(fake) is { } recording)
            {
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

                adapter = new FakeAgentAdapter(new ProcessRunner(), FakeAgentReplayer.ForCurrentProcess("agent", "replay"), recording);
                prompt = given.Length == 1 ? given[0] : string.Empty;
            }
            else
            {
                if (given.Length == 0)
                {
                    error.WriteLine($"Name an agent ({KnownAgents}), or replay a recording with --fake <recording.jsonl>.");
                    return 1;
                }

                if (AgentAdapters.Create(given[0], new ProcessRunner(), env) is not { } real)
                {
                    error.WriteLine($"Unknown agent \"{given[0]}\": known agents are {KnownAgents}, or replay a recording with --fake.");
                    return 1;
                }

                if (given.Length < 2 || string.IsNullOrWhiteSpace(given[1]))
                {
                    error.WriteLine("Give the prompt after the agent.");
                    return 1;
                }

                adapter = real;
                prompt = given[1];
            }

            if (!Directory.Exists(folder))
            {
                error.WriteLine($"{folder}: no such folder.");
                return 1;
            }

            if (!TryTools(result.GetValue(allow), out var tools, out var badTool))
            {
                error.WriteLine($"--allow {badTool}: expected read, edit or run.");
                return 1;
            }

            var installation = await adapter.DetectAsync(cancellationToken);
            if (installation is null)
            {
                error.WriteLine($"{adapter.DisplayName} isn't installed (looked on PATH and in the installers' usual folders).");
                return 1;
            }

            var request = new AgentRunRequest(prompt, folder)
            {
                BuiltinTools = tools,
                Model = result.GetValue(model),
                ResumeSessionId = result.GetValue(resume),
                SystemPrompt = result.GetValue(systemPrompt),
            };
            var outcome = AgentOutcome.Failed;
            await foreach (var e in adapter.RunAsync(installation, request, cancellationToken))
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

    private static bool TryTools(string[]? given, out HashSet<AgentBuiltinTool> tools, out string? bad)
    {
        tools = [];
        bad = null;
        foreach (var word in given is { Length: > 0 } ? given.SelectMany(g => g.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : ["read"])
        {
            switch (word.ToLowerInvariant())
            {
                case "read":
                    tools.Add(AgentBuiltinTool.ReadFiles);
                    break;
                case "edit":
                    tools.Add(AgentBuiltinTool.EditFiles);
                    break;
                case "run":
                    tools.Add(AgentBuiltinTool.RunCommands);
                    break;
                default:
                    bad = word;
                    return false;
            }
        }

        return true;
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
