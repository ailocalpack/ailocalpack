using AiLocalPack.Agents;
using AiLocalPack.Agents.Claude;
using AiLocalPack.Agents.Install;
using AiLocalPack.Agents.Processes;
using AiLocalPack.Core.Storage;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Agents;

/// <summary>What the app knows about an agent: its program (or none), whether that version is recent enough, its sign-in.</summary>
/// <param name="Installation">The program found (the player's own copy first, then the app's), or <c>null</c>.</param>
/// <param name="IsSupported">Its version is one the app can drive (Claude Code ≥ <see cref="ClaudeCodeCommandLine.MinimumVersion"/>).</param>
/// <param name="Auth">Its sign-in; <c>null</c> when there is no program.</param>
public sealed record AgentProbe(AgentInstallation? Installation, bool IsSupported, AgentAuthStatus? Auth)
{
    public static AgentProbe Missing { get; } = new(null, false, null);
}

/// <summary>
/// The agents' operations as Settings → AI agents uses them (task 44): find, check sign-in, install / update the official
/// build into the app's folder, run the agent's own sign-in and sign-out. Behind an interface so view models are tested
/// with a fake; the real one is <see cref="AgentHost"/>. Every method may be called off the UI thread and never throws
/// but for cancellation.
/// </summary>
public interface IAgentHost
{
    /// <summary>The agents the app offers, in order (<see cref="AgentAdapters.Ids"/>).</summary>
    IReadOnlyList<string> AgentIds { get; }

    Task<AgentProbe> ProbeAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>The release an install would get now (its version and size), or why there is none.</summary>
    Task<AgentReleaseLookup> FindReleaseAsync(string agentId, CancellationToken cancellationToken = default);

    Task<AgentInstallResult> InstallAsync(string agentId, IProgress<AgentInstallProgress>? progress, CancellationToken cancellationToken = default);

    Task<AgentSignInResult> SignInAsync(AgentInstallation installation, CancellationToken cancellationToken = default);

    Task<AgentAuthStatus> SignOutAsync(AgentInstallation installation, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real <see cref="IAgentHost"/>: <see cref="AgentAdapters"/> made afresh for each call (so an agent installed a moment
/// ago is found in the app's folder), <see cref="AgentInstaller"/> for installs and updates.
/// </summary>
public sealed class AgentHost(IProcessRunner runner, OsEnvironment environment, AgentInstaller installer, ILogger<AgentHost> logger) : IAgentHost
{
    public IReadOnlyList<string> AgentIds => AgentAdapters.Ids;

    public async Task<AgentProbe> ProbeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var adapter = Adapter(agentId);
        var installation = await adapter.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (installation is null)
        {
            logger.LogInformation("Agent {Agent}: not found", agentId);
            return AgentProbe.Missing;
        }

        var auth = await adapter.GetAuthStatusAsync(installation, cancellationToken).ConfigureAwait(false);
        var supported = AgentVersions.IsSupported(agentId, installation.Version);
        logger.LogInformation(
            "Agent {Agent}: {Executable} ({Source}) version {Version}{Old}, sign-in {Auth}",
            agentId, installation.Executable, installation.Source, installation.Version ?? "unknown", supported ? "" : " (too old)", auth.State);
        return new AgentProbe(installation, supported, auth);
    }

    public Task<AgentReleaseLookup> FindReleaseAsync(string agentId, CancellationToken cancellationToken = default) =>
        installer.FindReleaseAsync(agentId, cancellationToken: cancellationToken);

    public Task<AgentInstallResult> InstallAsync(string agentId, IProgress<AgentInstallProgress>? progress, CancellationToken cancellationToken = default) =>
        installer.InstallAsync(agentId, progress: progress, cancellationToken: cancellationToken);

    public Task<AgentSignInResult> SignInAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return Account(installation.AgentId).SignInAsync(installation, cancellationToken: cancellationToken);
    }

    public Task<AgentAuthStatus> SignOutAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return Account(installation.AgentId).SignOutAsync(installation, cancellationToken);
    }

    private IAgentAdapter Adapter(string agentId) =>
        AgentAdapters.Create(agentId, runner, environment, installer.ManagedDirectory(agentId))
        ?? throw new ArgumentException($"Unknown agent \"{agentId}\".", nameof(agentId));

    private IAgentAccount Account(string agentId) =>
        Adapter(agentId) as IAgentAccount ?? throw new InvalidOperationException($"Agent \"{agentId}\" has no sign-in.");
}

/// <summary>Agent version rules shared by the app.</summary>
public static class AgentVersions
{
    /// <summary>Whether the app can drive <paramref name="version"/> of the agent (an unknown version is given the benefit of the doubt).</summary>
    public static bool IsSupported(string agentId, string? version) =>
        agentId != ClaudeCodeAdapter.AgentId || version is null || ClaudeCodeCommandLine.IsSupported(version);

    /// <summary>Whether <paramref name="candidate"/> is a later version than <paramref name="installed"/> (pre-release parts ignored).</summary>
    public static bool IsNewer(string? candidate, string? installed) =>
        Parse(candidate) is { } c && (Parse(installed) is not { } i || c > i);

    private static Version? Parse(string? version) =>
        version is not null && Version.TryParse(version.Split('-', '+')[0], out var parsed) ? parsed : null;
}
