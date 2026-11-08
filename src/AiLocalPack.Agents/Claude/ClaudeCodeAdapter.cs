using System.Text.Json;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Claude;

/// <summary>
/// Claude Code (task 41), driven headless through <see cref="ClaudeCodeCommandLine"/> and read by
/// <see cref="ClaudeCodeParser"/>, on the player's own sign-in.
/// <list type="bullet">
/// <item><b>Detect</b>: <see cref="ClaudeCodeLocations.Candidates"/> in order (<c>PATH</c>, the installers' usual folders,
/// which an app started from the desktop doesn't have on its <c>PATH</c>, then the app-managed folder of task 43); the
/// first that answers <c>--version</c> (10 s) wins; when none does, the first found, without a version.</item>
/// <item><b>Sign-in</b>: <c>claude auth status --json</c> (15 s): local, no API call, no token read by the app; exit 0 +
/// <c>loggedIn</c> = signed in (account = e-mail, else the auth method), <c>loggedIn: false</c> = signed out, anything else
/// (an older Claude Code without the command, a timeout) = unknown.</item>
/// <item><b>Sign in / out</b> (task 43): <c>claude auth login</c> (Claude subscription by default; opens the browser) with
/// the status polled (<see cref="AgentSignIn"/>), <c>claude auth logout</c>.</item>
/// <item>The app's own copy (<see cref="AgentSource.Managed"/>) runs with <c>DISABLE_AUTOUPDATER=1</c>: the app updates
/// it (task 43), and Claude Code's own updater would install itself elsewhere.</item>
/// </list>
/// </summary>
/// <param name="runner">Starts Claude Code.</param>
/// <param name="environment">The OS, home and <c>PATH</c> to look in.</param>
/// <param name="managedDirectory">The folder the app installs Claude Code into (task 43), when there is one.</param>
/// <param name="systemFolders">Folders outside the home to look in on Unix (default <see cref="ClaudeCodeLocations.UnixSystemFolders"/>; tests pass none).</param>
public sealed class ClaudeCodeAdapter(
    IProcessRunner runner,
    OsEnvironment environment,
    string? managedDirectory = null,
    IReadOnlyList<string>? systemFolders = null)
    : ProcessAgentAdapter(runner), IAgentAccount
{
    public const string AgentId = "claude";

    /// <summary>Set for the app's own copy, so Claude Code doesn't update itself behind the app's back.</summary>
    public const string DisableAutoUpdaterVariable = "DISABLE_AUTOUPDATER";

    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(15);

    private readonly OsEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public override string Id => AgentId;

    public override string DisplayName => "Claude Code";

    public override Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default) =>
        AgentPrograms.DetectAsync(
            AgentId,
            ClaudeCodeLocations.Candidates(_environment, managedDirectory, systemFolders),
            Runner,
            _environment,
            cancellationToken);

    public override async Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        try
        {
            var spec = Spec(installation, ["auth", "status", "--json"]);
            return ClaudeCodeAuth.Read(await Runner.RunToEndAsync(spec, AuthTimeout, cancellationToken));
        }
        catch (ProcessStartException e)
        {
            return new AgentAuthStatus(AgentAuthState.Unknown, Detail: e.Message);
        }
    }

    protected override AgentRunProcess CreateRunProcess(AgentInstallation installation, AgentRunRequest request)
    {
        string? scratch = null;
        string? mcpConfig = null;
        string? systemPrompt = null;
        if (request.McpServers.Count > 0 || !string.IsNullOrEmpty(request.SystemPrompt))
        {
            var config = request.McpServers.Count > 0 ? ClaudeCodeCommandLine.McpConfig(request.McpServers) : null;
            scratch = Directory.CreateTempSubdirectory("ailocalpack-claude-").FullName;
            if (config is not null)
            {
                mcpConfig = Path.Combine(scratch, ClaudeCodeCommandLine.McpConfigFileName);
                File.WriteAllText(mcpConfig, config);
            }

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                systemPrompt = Path.Combine(scratch, ClaudeCodeCommandLine.SystemPromptFileName);
                File.WriteAllText(systemPrompt, request.SystemPrompt);
            }
        }

        var template = new ProcessSpec(installation.Executable, [])
        {
            WorkingDirectory = request.WorkingDirectory,
            StandardInput = request.Prompt,
        };
        var arguments = ClaudeCodeCommandLine.Arguments(request, mcpConfig, systemPrompt);
        var spec = Spec(installation, arguments, template);
        return new AgentRunProcess(spec) { ScratchDirectory = scratch };
    }

    public Task<AgentSignInResult> SignInAsync(AgentInstallation installation, Action<ProcessLine>? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return AgentSignIn.RunAsync(Runner, Spec(installation, ["auth", "login"]), ct => GetAuthStatusAsync(installation, ct), output, SignInOptions, cancellationToken);
    }

    public async Task<AgentAuthStatus> SignOutAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        try
        {
            await Runner.RunToEndAsync(Spec(installation, ["auth", "logout"]), AuthTimeout, cancellationToken);
        }
        catch (ProcessStartException e)
        {
            return new AgentAuthStatus(AgentAuthState.Unknown, Detail: e.Message);
        }

        return await GetAuthStatusAsync(installation, cancellationToken);
    }

    /// <summary>The sign-in loop's timing (tests shorten it).</summary>
    public AgentSignInOptions? SignInOptions { get; init; }

    /// <summary>The process running Claude Code with <paramref name="arguments"/>; the app's own copy never updates itself.</summary>
    private ProcessSpec Spec(AgentInstallation installation, IReadOnlyList<string> arguments, ProcessSpec? template = null)
    {
        var spec = AgentPrograms.Process(installation.Executable, [.. installation.Arguments, .. arguments], _environment, template);
        return installation.Source == AgentSource.Managed
            ? spec with { Environment = new Dictionary<string, string?>(spec.Environment) { [DisableAutoUpdaterVariable] = "1" } }
            : spec;
    }

    protected override IAgentOutputParser CreateParser(AgentRunRequest request) => new ClaudeCodeParser();
}

/// <summary>Reads <c>claude auth status --json</c> (documented: exit 0 when signed in, 1 when not).</summary>
public static class ClaudeCodeAuth
{
    public static AgentAuthStatus Read(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TimedOut)
        {
            return new AgentAuthStatus(AgentAuthState.Unknown, Detail: "Claude Code didn't answer the sign-in check in time.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("loggedIn", out var loggedIn))
            {
                if (loggedIn.ValueKind == JsonValueKind.True && result.ExitCode == 0)
                {
                    return new AgentAuthStatus(AgentAuthState.SignedIn, Text(root, "email") ?? Method(Text(root, "authMethod")));
                }

                if (loggedIn.ValueKind == JsonValueKind.False)
                {
                    return new AgentAuthStatus(AgentAuthState.SignedOut);
                }
            }
        }
        catch (JsonException)
        {
            // Not the JSON of a Claude Code that knows the command: unknown below.
        }

        var said = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput);
        return new AgentAuthStatus(
            AgentAuthState.Unknown,
            Detail: $"The sign-in check exited with code {result.ExitCode}" + (said is null ? "." : $": {said}"));
    }

    private static string? Method(string? method) => method switch
    {
        null or "none" => null,
        "api_key" or "api_key_helper" => "API key",
        "third_party" => "Cloud provider",
        _ => method,
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
