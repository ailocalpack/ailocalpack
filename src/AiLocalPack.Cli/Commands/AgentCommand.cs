using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Agents;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Fake;
using AiLocalPack.Agents.Install;
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
/// <see cref="FakeAgentReplay"/> program). Task 43: <c>agent install &lt;agent&gt; [--version &lt;v&gt;] [--force]</c> installs the
/// official build into the app's folder (or updates it; nothing to do when the player's own copy is found, unless
/// <c>--force</c>), <c>agent uninstall &lt;agent&gt;</c> removes the app's copy, <c>agent login &lt;agent&gt;</c> runs the
/// agent's own browser sign-in and waits for it, <c>agent logout &lt;agent&gt;</c> signs out. Every command finds the
/// agent on <c>PATH</c>, in the installers' folders, then in the app's own folder.
/// </summary>
internal static class AgentCommand
{
    public static Command Build(OsEnvironment env)
    {
        return new Command("agent", "Run AI agents headless (Claude Code, Codex) and see their normalized events.")
        {
            BuildProbe(env),
            BuildRun(env),
            BuildInstall(env),
            BuildUninstall(env),
            BuildLogin(env),
            BuildLogout(env),
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
            if (Adapter(env, id) is not { } adapter)
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
            output.WriteLine($"program:  not found (looked on PATH, in the installers' usual folders and the app's own; `ailocalpack agent install {adapter.Id}` installs it)");
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

                if (Adapter(env, given[0]) is not { } real)
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
                error.WriteLine($"{adapter.DisplayName} isn't installed (looked on PATH, in the installers' usual folders and the app's own): run `ailocalpack agent install {adapter.Id}`.");
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

    /// <summary>The adapter of <paramref name="id"/>, looking in the app's own folder last.</summary>
    internal static IAgentAdapter? Adapter(OsEnvironment env, string id)
    {
        if (!AgentAdapters.Ids.Contains(id.ToLowerInvariant()))
        {
            return null;
        }

        var managed = ManagedFolder(env, id);
        return AgentAdapters.Create(id, new ProcessRunner(), env, managed);
    }

    private static string? ManagedFolder(OsEnvironment env, string id)
    {
        using var http = new HttpClient();
        return AgentInstaller.Create(AppPaths.Resolve(env), env, http).ManagedDirectory(id);
    }

    private static Command BuildInstall(OsEnvironment env)
    {
        var agent = new Argument<string>("agent") { Description = $"The agent ({KnownAgents})." };
        var version = new Option<string?>("--version") { Description = "Install this version (default: the vendor's current release)." };
        var force = new Option<bool>("--force") { Description = "Install the app's own copy even when one you installed is found." };
        var install = new Command("install", "Install an agent's official build into the app's folder, or update it.") { agent, version, force };
        install.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            var id = result.GetValue(agent)!.ToLowerInvariant();
            if (Adapter(env, id) is not { } adapter)
            {
                error.WriteLine($"Unknown agent \"{id}\": known agents are {KnownAgents}.");
                return 1;
            }

            if (!result.GetValue(force) && await adapter.DetectAsync(cancellationToken) is { Source: AgentSource.Path } own)
            {
                output.WriteLine($"{adapter.DisplayName} {own.Version ?? "(version unknown)"} is already installed by you at {own.Executable}; the app uses that one. Pass --force to install the app's own copy anyway.");
                return 0;
            }

            using var http = AgentInstaller.CreateHttpClient();
            var installer = AgentInstaller.Create(AppPaths.Resolve(env), env, http);
            var reported = -1L;
            var progress = new SyncProgress<AgentInstallProgress>(p =>
            {
                if (p.Stage == AgentInstallStage.Downloading && p.Total > 0)
                {
                    var tenth = p.Done * 10 / p.Total;
                    if (tenth != reported)
                    {
                        reported = tenth;
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"downloading {p.Done / 1048576.0:0.0} of {p.Total / 1048576.0:0.0} MB"));
                    }
                }
                else if (p.Stage == AgentInstallStage.Unpacking)
                {
                    output.WriteLine("unpacking");
                }
            });
            output.WriteLine($"{adapter.DisplayName}: looking for the release for {installer.Platform?.ToString() ?? "this CPU"}…");
            var outcome = await installer.InstallAsync(id, result.GetValue(version), progress, cancellationToken);
            if (!outcome.Succeeded)
            {
                error.WriteLine($"{adapter.DisplayName} wasn't installed: {outcome.Detail}" + (outcome.MayPass ? " Try again later; a partial download resumes." : string.Empty));
                return 1;
            }

            var installed = outcome.Installed!;
            if (outcome.AlreadyInstalled)
            {
                output.WriteLine($"{adapter.DisplayName} {installed.Version} is already installed by the app at {installed.Executable}.");
            }
            else
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Installed {adapter.DisplayName} {installed.Version} into {installed.Folder}: downloaded {outcome.Downloaded} bytes, {outcome.Release!.Verification}, SHA-256 checked."));
            }

            output.WriteLine($"program: {installed.Executable}");
            return 0;
        });
        return install;
    }

    private static Command BuildUninstall(OsEnvironment env)
    {
        var agent = new Argument<string>("agent") { Description = $"The agent ({KnownAgents})." };
        var uninstall = new Command("uninstall", "Remove the app's own copy of an agent (one you installed is left alone).") { agent };
        uninstall.SetAction(result =>
        {
            var id = result.GetValue(agent)!.ToLowerInvariant();
            if (!AgentAdapters.Ids.Contains(id))
            {
                result.InvocationConfiguration.Error.WriteLine($"Unknown agent \"{id}\": known agents are {KnownAgents}.");
                return 1;
            }

            using var http = new HttpClient();
            var installer = AgentInstaller.Create(AppPaths.Resolve(env), env, http);
            var found = installer.Find(id);
            if (installer.Uninstall(id) is { } problem)
            {
                result.InvocationConfiguration.Error.WriteLine($"The app's copy of {id} wasn't fully removed (is it running?): {problem}");
                return 1;
            }

            result.InvocationConfiguration.Output.WriteLine(found is null
                ? $"The app has no copy of {id} installed."
                : $"Removed the app's copy of {id} {found.Version}.");
            return 0;
        });
        return uninstall;
    }

    private static Command BuildLogin(OsEnvironment env)
    {
        var agent = new Argument<string>("agent") { Description = $"The agent ({KnownAgents})." };
        var login = new Command("login", "Sign in with the agent's own login (opens the browser) and wait until it is done.") { agent };
        login.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            if (await FindAccountAsync(env, result.GetValue(agent)!, error, cancellationToken) is not var (adapter, account, installation))
            {
                return 1;
            }

            output.WriteLine($"{adapter.DisplayName}: starting its sign-in; finish it in the browser (waiting up to {AgentSignInOptions.Default.Timeout.TotalMinutes:0} min, Ctrl+C stops).");
            var signIn = await account.SignInAsync(installation, line => output.WriteLine($"  {line.Text}"), cancellationToken);
            switch (signIn.Outcome)
            {
                case AgentSignInOutcome.AlreadySignedIn:
                    output.WriteLine($"Already signed in{Account(signIn.Status)}; `ailocalpack agent logout {adapter.Id}` signs out.");
                    return 0;
                case AgentSignInOutcome.SignedIn:
                    output.WriteLine($"Signed in{Account(signIn.Status)}.");
                    return 0;
                case AgentSignInOutcome.Cancelled:
                    error.WriteLine("Sign-in stopped.");
                    return 1;
                default:
                    error.WriteLine($"Not signed in: {signIn.Detail}");
                    return 1;
            }
        });
        return login;
    }

    private static Command BuildLogout(OsEnvironment env)
    {
        var agent = new Argument<string>("agent") { Description = $"The agent ({KnownAgents})." };
        var logout = new Command("logout", "Sign out with the agent's own logout.") { agent };
        logout.SetAction(async (result, cancellationToken) =>
        {
            var error = result.InvocationConfiguration.Error;
            if (await FindAccountAsync(env, result.GetValue(agent)!, error, cancellationToken) is not var (adapter, account, installation))
            {
                return 1;
            }

            var status = await account.SignOutAsync(installation, cancellationToken);
            if (status.State == AgentAuthState.SignedIn)
            {
                error.WriteLine($"{adapter.DisplayName} still reports being signed in{Account(status)}.");
                return 1;
            }

            result.InvocationConfiguration.Output.WriteLine($"{adapter.DisplayName}: signed out.");
            return 0;
        });
        return logout;
    }

    private static async Task<(IAgentAdapter Adapter, IAgentAccount Account, AgentInstallation Installation)?> FindAccountAsync(
        OsEnvironment env, string id, TextWriter error, CancellationToken cancellationToken)
    {
        if (Adapter(env, id) is not { } adapter || adapter is not IAgentAccount account)
        {
            error.WriteLine($"Unknown agent \"{id}\": known agents are {KnownAgents}.");
            return null;
        }

        if (await adapter.DetectAsync(cancellationToken) is not { } installation)
        {
            error.WriteLine($"{adapter.DisplayName} isn't installed: run `ailocalpack agent install {adapter.Id}`.");
            return null;
        }

        return (adapter, account, installation);
    }

    private static string Account(AgentAuthStatus? status) => status?.Account is { } account ? $" ({account})" : string.Empty;

    /// <summary>Reports on the reporting thread (console order kept).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
