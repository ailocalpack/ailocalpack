using System.Globalization;
using AiLocalPack.Agents;
using AiLocalPack.Core.Generation;

namespace AiLocalPack.Pipeline;

/// <summary>How a stage's agent run ended.</summary>
/// <param name="Stop">The step's result when the run didn't succeed (missing, signed out, limit, failed); <c>null</c> = succeeded.</param>
/// <param name="Reply">The agent's final answer, when it gave one.</param>
public sealed record PipelineAgentResult(GenerationStepResult? Stop, string? Reply)
{
    public bool Succeeded => Stop is null;
}

/// <summary>A system prompt in place of the run's usual one, with its values.</summary>
public sealed record AgentSystemPrompt(PromptTemplate Template, IReadOnlyDictionary<string, string> Values);

/// <summary>What a run may do besides the usual (task 75's fix runs): its own system prompt, every event seen as it comes, its transcript's name.</summary>
/// <param name="Transcript">The transcript is <c>logs/agent-&lt;name&gt;.jsonl</c> instead of the stage's.</param>
/// <param name="TestRuns">Offer the toolkit server's <c>test_run</c> with this many game runs (a fix with AI), or <c>null</c>.</param>
public sealed record AgentRunExtras(AgentSystemPrompt? System = null, Action<AgentEvent>? OnEvent = null, string? Transcript = null, int? TestRuns = null);

/// <summary>
/// A run in the agent's own workspace (task 57, a game the toolkit can't read): the job's <c>agent/</c> folder as the
/// working folder, reading files, editing them and running commands there (each adapter's sandbox keeps writes inside
/// it and has no network), the <see cref="PromptTemplate.SystemScripts"/> system prompt; the toolkit server still reads
/// the game folder (read only). With a translation installed in the game folder (<paramref name="Installed"/>, a draft
/// tried before a fix) the workspace gets <c>installed.json</c> (<see cref="InstalledFile"/>: where the game's own
/// versions of the files it replaced are, which files it added) and the system prompt <see cref="PromptTemplate.SystemInstalled"/>,
/// so the agent's scripts read the game's text, not the translation; without one a stale <c>installed.json</c> is deleted.
/// </summary>
public sealed record AgentWorkspace(string Folder, InstalledOriginals? Installed = null)
{
    /// <summary>The file in the workspace naming an installed translation's files.</summary>
    public const string InstalledFile = "installed.json";

    private static readonly System.Text.Json.JsonSerializerOptions InstalledJson = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes (or deletes) <see cref="InstalledFile"/> for <see cref="Installed"/>.</summary>
    /// <exception cref="IOException">The file can't be written or deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be written or deleted.</exception>
    internal void WriteInstalled(string gameFolder)
    {
        var path = Path.Combine(Folder, InstalledFile);
        if (Installed is not { IsEmpty: false } installed)
        {
            File.Delete(path);
            return;
        }

        var originals = new SortedDictionary<string, string>(installed.Restored.ToDictionary(), StringComparer.Ordinal);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { gameFolder, originals, added = installed.Added.Order(StringComparer.Ordinal).ToList() },
            InstalledJson);
        Core.Storage.AtomicFile.WriteAllBytes(path, bytes);
    }

    public static IReadOnlySet<AgentBuiltinTool> Tools { get; } =
        new HashSet<AgentBuiltinTool> { AgentBuiltinTool.ReadFiles, AgentBuiltinTool.EditFiles, AgentBuiltinTool.RunCommands };
}

/// <summary>
/// Runs the job's agent for a stage: finds the program and checks its sign-in first (a signed-out Codex retries for half
/// a minute before saying so), then runs it headless in the job's agent folder with the toolkit server as its only tools
/// (no built-in file, edit or shell tools), the <see cref="PromptTemplate.System"/> prompt appended and the job's model.
/// Every event goes to <c>logs/agent-&lt;stage&gt;.jsonl</c> (<see cref="AgentEventJson"/>) and a readable line of each
/// message, tool call, tool error and the usage to the job log; the usage is also summed into <c>progress.json</c>
/// (<see cref="PipelineProgressFile"/>). A cancelled run throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class PipelineAgent(IAgentAdapter adapter, ToolkitServerProgram server)
{
    private const int LogTextLength = 400;

    // Translate chunks running side by side append to the same transcript.
    private static readonly Lock TranscriptGate = new();

    public string Id => adapter.Id;

    public string DisplayName => adapter.DisplayName;

    public async Task<PipelineAgentResult> RunAsync(
        GenerationStepContext context,
        PromptTemplate prompt,
        IReadOnlyDictionary<string, string> values,
        string gameName,
        CancellationToken cancellationToken,
        AgentWorkspace? workspace = null,
        string? logTag = null,
        IReadOnlySet<AgentBuiltinTool>? extraTools = null,
        AgentRunExtras? extras = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(values);
        var task = prompt.Render(values);
        string gameFolder;
        try
        {
            gameFolder = context.GameFolder;
        }
        catch (GenerationFileException e)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(GenerationFailureKind.Disk, e.Message));
        }

        var systemTemplate = extras?.System?.Template ?? (workspace is null ? PromptTemplate.System : PromptTemplate.SystemScripts);
        var system = extras?.System is { } own ? own.Template.Render(own.Values)
            : workspace is null
            ? PromptTemplate.System.Render(PromptValues.System(gameName, context.Job.Language))
            : PromptTemplate.SystemScripts.Render(PromptValues.SystemScripts(gameName, context.Job.Language, gameFolder));
        string? brief;
        try
        {
            brief = BriefFor(context);
        }
        catch (GenerationFileException e)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"{e.Message}; run the Study stage again."));
        }

        if (brief is not null)
        {
            system += "\n\n" + PromptTemplate.SystemBrief.Render(PromptValues.SystemBrief(brief));
        }

        var references = ReferencesFor(context, workspace, extras);
        if (references is not null)
        {
            var files = context.Job.Stage == GenerationStage.Extract ? references.TouchedFiles : null;
            system += "\n\n" + PromptTemplate.SystemReferences.Render(PromptValues.SystemReferences(gameName, context.Job.Language, references, files));
        }

        var choice = ModelFor(context.Job, Id, workspace is not null);
        if (await adapter.DetectAsync(cancellationToken).ConfigureAwait(false) is not { } installation)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(
                GenerationFailureKind.AgentMissing, $"{adapter.DisplayName} isn't installed: install it in Settings → AI agents."));
        }

        var auth = await adapter.GetAuthStatusAsync(installation, cancellationToken).ConfigureAwait(false);
        if (auth.State == AgentAuthState.SignedOut)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(
                GenerationFailureKind.SignedOut, $"{adapter.DisplayName} isn't signed in: sign in in Settings → AI agents."));
        }

        // Never the game folder: the agent works in the job's agent/ folder (only its own commands write there, for an
        // unknown engine) and reads the game through the toolkit server (_Live game folder_).
        var workingFolder = workspace?.Folder ?? context.Files.AgentFolder;
        try
        {
            Directory.CreateDirectory(workingFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Stop(context, logTag, new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"The agent's working folder {workingFolder} can't be made: {e.Message}"));
        }

        if (workspace is not null)
        {
            try
            {
                workspace.WriteInstalled(gameFolder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Stop(context, logTag, new GenerationStepResult.Failed(GenerationFailureKind.Disk, $"The agent's workspace can't be prepared: {e.Message}"));
            }

            if (workspace.Installed is { IsEmpty: false } installed)
            {
                system += "\n\n" + PromptTemplate.SystemInstalled.Render(PromptValues.SystemInstalled(installed.Restored.Count, installed.Added.Count));
            }
        }

        var request = new AgentRunRequest(task, workingFolder)
        {
            SystemPrompt = system,
            BuiltinTools = Tools(workspace, extraTools),
            McpServers = [server.For(gameFolder, context.Folder, context.Job.Language, context.Job.Options.Reader, extras?.TestRuns)],
            Model = choice.Model,
            Effort = choice.Effort,
            AllowUnsandboxed = workspace is not null && context.Job.Options.Unsandboxed,
        };
        Log(context, logTag, $"Agent {adapter.Id} {installation.Version ?? "(version unknown)"} ({installation.Source}), prompt {prompt.Label} + {systemTemplate.Label}"
            + (brief is null ? "" : $" + {PromptTemplate.SystemBrief.Label}")
            + (references is null ? "" : $" + {PromptTemplate.SystemReferences.Label} ({references.Packs.Count} packs)")
            + (workspace?.Installed is { IsEmpty: false } shown ? $" + {PromptTemplate.SystemInstalled.Label} ({shown.Restored.Count} files replaced, {shown.Added.Count} added by the installed translation)" : "")
            + (workspace is null ? "" : $", in its workspace {workspace.Folder} with file and command tools")
            + (request.AllowUnsandboxed ? " (without a sandbox where it can't start, as the player allowed)" : "")
            + (extraTools is { Count: > 0 } ? $", with {string.Join(", ", extraTools.Order())}" : "")
            + $", quality {context.Job.Options.Quality.ToString().ToLowerInvariant()}"
            + (request.Model is { } model ? $", model {model}" : "")
            + (request.Effort is { } effort ? $", effort {effort.ToString().ToLowerInvariant()}." : "."));
        var transcript = Transcript(context, extras?.Transcript);
        var usage = new List<AgentUsage>();
        var seenModel = request.Model;
        try
        {
            var outcome = await AgentStepRun.RunAsync(
                adapter,
                installation,
                request,
                e =>
                {
                    if (e is AgentUsage u)
                    {
                        usage.Add(u);
                    }
                    else if (e is AgentSessionStarted { Model: { } started })
                    {
                        seenModel = started;
                    }
                    else if (e is AgentLimitStatus status)
                    {
                        // Shown while the run goes on (the progress card's limit box), so written at once.
                        PipelineProgressFile.Update(context.Files, p => p with { Limit = new AgentLimitInfo(status.Used, status.Window, status.ResetsAt) });
                    }

                    Record(context, logTag, transcript, e);
                    extras?.OnEvent?.Invoke(e);
                },
                cancellationToken).ConfigureAwait(false);
            return new PipelineAgentResult(outcome.Stop, outcome.Done.Result);
        }
        finally
        {
            PipelineProgressFile.Update(context.Files, p => p with
            {
                Usage = p.Usage.Add(usage),
                Agent = new AgentRunInfo(installation.Version ?? p.Agent?.Version, seenModel ?? p.Agent?.Model),
            });
        }
    }

    /// <summary>
    /// The model of a run: the job's <see cref="GenerationOptions.Model"/> when the player named one (CLI <c>--model</c>),
    /// else <see cref="AgentModels"/> for the job's quality; the agent's own scripts (unknown engines) always run with the
    /// Balanced choice.
    /// </summary>
    public static AgentModelChoice ModelFor(GenerationJob job, string agentId, bool scripts)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Options.Model is { } model)
        {
            return new AgentModelChoice(model, null);
        }

        return scripts ? AgentModels.ForExtraction(agentId) : AgentModels.For(agentId, job.Options.Quality);
    }

    /// <summary>The game brief a run gets: a Thorough job's Glossary and Translate runs, once Study wrote it.</summary>
    private static string? BriefFor(GenerationStepContext context) =>
        context.Job.Options.Studies && context.Job.Stage is GenerationStage.Glossary or GenerationStage.Translate
            ? context.Files.ReadBrief()
            : null;

    /// <summary>
    /// The reference packs a run hears about: a job with references, in its Glossary and Translate runs, an Extract run
    /// with the agent's own code, and fix runs. <c>null</c> otherwise.
    /// </summary>
    private static ReferenceSet? ReferencesFor(GenerationStepContext context, AgentWorkspace? workspace, AgentRunExtras? extras)
    {
        if (context.Job.Options.References is not { Count: > 0 })
        {
            return null;
        }

        var wanted = extras?.System?.Template == PromptTemplate.SystemFix
            || (extras?.System is null && context.Job.Stage is GenerationStage.Glossary or GenerationStage.Translate)
            || (workspace is not null && context.Job.Stage == GenerationStage.Extract);
        return wanted && context.Files.ReadReferences() is { IsEmpty: false } set ? set : null;
    }

    private static HashSet<AgentBuiltinTool> Tools(AgentWorkspace? workspace, IReadOnlySet<AgentBuiltinTool>? extra)
    {
        var tools = workspace is null ? new HashSet<AgentBuiltinTool>() : new HashSet<AgentBuiltinTool>(AgentWorkspace.Tools);
        if (extra is not null)
        {
            tools.UnionWith(extra);
        }

        return tools;
    }

    private static PipelineAgentResult Stop(GenerationStepContext context, string? logTag, GenerationStepResult.Failed failed)
    {
        Log(context, logTag, failed.Message);
        return new PipelineAgentResult(failed, null);
    }

    private static string Transcript(GenerationStepContext context, string? name)
    {
        var logs = Path.Combine(context.Folder, "logs");
        Directory.CreateDirectory(logs);
        return Path.Combine(logs, $"agent-{name ?? context.Job.Stage.ToString().ToLowerInvariant()}.jsonl");
    }

    /// <summary>A job log line, after <c>logTag: </c> when runs go side by side (Translate chunks).</summary>
    private static void Log(GenerationStepContext context, string? logTag, string line) =>
        context.Log(logTag is null ? line : $"{logTag}: {line}");

    private static void Record(GenerationStepContext context, string? logTag, string transcript, AgentEvent e)
    {
        lock (TranscriptGate)
        {
            try
            {
                File.AppendAllText(transcript, AgentEventJson.Write(e) + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort, like the job log.
            }
        }

        var line = e switch
        {
            AgentSessionStarted s => $"Agent session {s.SessionId}" + (s.Model is { } m ? $" ({m})." : "."),
            AgentMessage m => $"Agent: {Short(m.Text)}",
            AgentToolCall c => $"Tool call: {c.Name}",
            AgentToolResult { IsError: true } r => $"Tool error: {Short(r.Output)}",
            AgentUsage u => string.Create(CultureInfo.InvariantCulture, $"Usage: {u.InputTokens} input, {u.OutputTokens} output, {u.CacheReadTokens} cache read, {u.CacheWriteTokens} cache write tokens")
                + (u.CostUsd is { } cost ? string.Create(CultureInfo.InvariantCulture, $", ${cost:0.####}.") : "."),
            AgentLimitReached l => $"Agent limit reached" + (l.ResetsAt is { } at ? $", resets at {at:u}." : "."),
            AgentLimitStatus st => string.Create(CultureInfo.InvariantCulture, $"Agent usage: {st.Used:P0} of the {st.Window ?? "current"} window used."),
            AgentError error => $"Agent error ({error.Kind}): {Short(error.Message)}",
            AgentDone d => $"Agent run ended: {d.Outcome}" + (d.Message is { } message ? $" ({Short(message)})." : "."),
            _ => null,
        };
        if (line is not null)
        {
            Log(context, logTag, line);
        }
    }

    private static string Short(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= LogTextLength ? flat : flat[..LogTextLength] + "…";
    }
}
