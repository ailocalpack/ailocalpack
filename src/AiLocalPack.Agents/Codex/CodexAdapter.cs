using System.Text.RegularExpressions;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Agents.Codex;

/// <summary>
/// The OpenAI Codex CLI (task 42), driven headless through <see cref="CodexCommandLine"/> and read by
/// <see cref="CodexParser"/>, on the player's own sign-in (ChatGPT or API key).
/// <list type="bullet">
/// <item><b>Detect</b>: <see cref="Layout"/> through <see cref="AgentPrograms.Candidates"/> (<c>PATH</c>, <c>~/.local/bin</c>,
/// Homebrew, npm's global folder, the app-managed folder); the first answering <c>--version</c> (<c>codex-cli 0.167.0</c>).</item>
/// <item><b>Sign-in</b>: <c>codex login status</c> (15 s; local, no token read by the app), which writes to standard error:
/// exit 0 + <c>Logged in using ChatGPT</c> / <c>…an API key…</c> = signed in (account = ChatGPT / API key),
/// <c>Not logged in</c> = signed out, anything else = unknown.</item>
/// <item><b>Sign in / out</b> (task 43): <c>codex login</c> (ChatGPT; opens the browser, local callback) with the status
/// polled (<see cref="AgentSignIn"/>), <c>codex logout</c>.</item>
/// </list>
/// </summary>
/// <param name="runner">Starts Codex.</param>
/// <param name="environment">The OS, home and <c>PATH</c> to look in.</param>
/// <param name="managedDirectory">The folder the app installs Codex into (task 43), when there is one.</param>
/// <param name="systemFolders">Folders outside the home to look in on Unix (default <see cref="AgentPrograms.UnixSystemFolders"/>; tests pass none).</param>
/// <param name="clock">The clock and time zone limit reset times are read in.</param>
public sealed partial class CodexAdapter(
    IProcessRunner runner,
    OsEnvironment environment,
    string? managedDirectory = null,
    IReadOnlyList<string>? systemFolders = null,
    TimeProvider? clock = null)
    : ProcessAgentAdapter(runner), IAgentAccount
{
    public const string AgentId = "codex";

    /// <summary>The Codex version the command line was checked with; older ones may lack a flag (no minimum is enforced).</summary>
    public const string CheckedVersion = "0.167.0";

    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(15);

    private readonly OsEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    public static AgentProgramLayout Layout { get; } = new("codex", [".local/bin"], []);

    public override string Id => AgentId;

    public override string DisplayName => "Codex";

    public override Task<AgentInstallation?> DetectAsync(CancellationToken cancellationToken = default) =>
        AgentPrograms.DetectAsync(
            AgentId,
            AgentPrograms.Candidates(Layout, _environment, managedDirectory, systemFolders),
            Runner,
            _environment,
            cancellationToken);

    public override async Task<AgentAuthStatus> GetAuthStatusAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        try
        {
            var spec = AgentPrograms.Process(installation.Executable, [.. installation.Arguments, "login", "status"], _environment);
            return ReadAuth(await Runner.RunToEndAsync(spec, AuthTimeout, cancellationToken));
        }
        catch (ProcessStartException e)
        {
            return new AgentAuthStatus(AgentAuthState.Unknown, Detail: e.Message);
        }
    }

    /// <summary>Reads <c>codex login status</c> (either output; Codex 0.167 writes to standard error).</summary>
    public static AgentAuthStatus ReadAuth(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TimedOut)
        {
            return new AgentAuthStatus(AgentAuthState.Unknown, Detail: "Codex didn't answer the sign-in check in time.");
        }

        var text = (result.StandardOutput + "\n" + result.StandardError).Trim();
        var said = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (result.ExitCode == 0 && LoggedIn().Match(text) is { Success: true } match)
        {
            var how = match.Groups["how"].Value;
            var account = how.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "API key"
                : how.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "ChatGPT"
                : null;
            return new AgentAuthStatus(AgentAuthState.SignedIn, account);
        }

        if (NotLoggedIn().IsMatch(text))
        {
            return new AgentAuthStatus(AgentAuthState.SignedOut);
        }

        return new AgentAuthStatus(
            AgentAuthState.Unknown,
            Detail: $"The sign-in check exited with code {result.ExitCode}" + (said is null ? "." : $": {said}"));
    }

    public Task<AgentSignInResult> SignInAsync(AgentInstallation installation, Action<ProcessLine>? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var spec = AgentPrograms.Process(installation.Executable, [.. installation.Arguments, "login"], _environment);
        return AgentSignIn.RunAsync(Runner, spec, ct => GetAuthStatusAsync(installation, ct), output, SignInOptions, cancellationToken);
    }

    public async Task<AgentAuthStatus> SignOutAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        try
        {
            var spec = AgentPrograms.Process(installation.Executable, [.. installation.Arguments, "logout"], _environment);
            await Runner.RunToEndAsync(spec, AuthTimeout, cancellationToken);
        }
        catch (ProcessStartException e)
        {
            return new AgentAuthStatus(AgentAuthState.Unknown, Detail: e.Message);
        }

        return await GetAuthStatusAsync(installation, cancellationToken);
    }

    /// <summary>The sign-in loop's timing (tests shorten it).</summary>
    public AgentSignInOptions? SignInOptions { get; init; }

    protected override AgentRunProcess CreateRunProcess(AgentInstallation installation, AgentRunRequest request)
    {
        var template = new ProcessSpec(installation.Executable, [])
        {
            WorkingDirectory = request.WorkingDirectory,
            StandardInput = request.Prompt,
            Environment = AgentSandbox.Environment(request),
        };
        var arguments = CodexCommandLine.Arguments(request);
        return new AgentRunProcess(AgentPrograms.Process(installation.Executable, [.. installation.Arguments, .. arguments], _environment, template));
    }

    protected override IAgentOutputParser CreateParser(AgentRunRequest request) => new CodexParser(request.Model, clock);

    [GeneratedRegex(@"^Logged in(?<how>.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LoggedIn();

    [GeneratedRegex("Not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex NotLoggedIn();
}
