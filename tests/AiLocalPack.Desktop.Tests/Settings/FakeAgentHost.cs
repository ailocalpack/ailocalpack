using AiLocalPack.Agents;
using AiLocalPack.Agents.Install;
using AiLocalPack.Desktop.Agents;

namespace AiLocalPack.Desktop.Tests.Settings;

/// <summary>
/// Agents as the test sets them, so no test ever starts a real agent or downloads one. By default no agent is installed
/// and no release is found. <see cref="Install"/> / <see cref="SignIn"/> / <see cref="SignOut"/> decide what an action
/// does (a pending task holds it, so the test sees the in-between state); every call is recorded.
/// </summary>
internal sealed class FakeAgentHost : IAgentHost
{
    public IReadOnlyList<string> AgentIds { get; set; } = ["claude", "codex"];

    /// <summary>What a check finds per agent; a missing entry = not installed.</summary>
    public Dictionary<string, AgentProbe> Probes { get; } = [];

    /// <summary>The current release per agent; a missing entry = none found (offline).</summary>
    public Dictionary<string, AgentRelease> Releases { get; } = [];

    /// <summary>The install: agent, progress, cancellation → result. Default: fails offline.</summary>
    public Func<string, IProgress<AgentInstallProgress>?, CancellationToken, Task<AgentInstallResult>> Install { get; set; } =
        (_, _, _) => Task.FromResult(new AgentInstallResult(null, AgentInstallProblem.Offline, "offline"));

    /// <summary>The sign-in. Default: the login exits without signing in.</summary>
    public Func<AgentInstallation, CancellationToken, Task<AgentSignInResult>> SignIn { get; set; } =
        (_, _) => Task.FromResult(new AgentSignInResult(AgentSignInOutcome.Exited, new AgentAuthStatus(AgentAuthState.SignedOut), "exited"));

    /// <summary>The sign-out. Default: signed out.</summary>
    public Func<AgentInstallation, Task<AgentAuthStatus>> SignOut { get; set; } =
        _ => Task.FromResult(new AgentAuthStatus(AgentAuthState.SignedOut));

    public List<string> Calls { get; } = [];

    public static AgentInstallation Program(string id, AgentSource source = AgentSource.Path, string version = "2.1.319") =>
        new(id, $"/usr/local/bin/{id}", version, source);

    public static AgentProbe SignedIn(string id, AgentSource source = AgentSource.Path, string version = "2.1.319", string? account = "player@example.com") =>
        new(Program(id, source, version), true, new AgentAuthStatus(AgentAuthState.SignedIn, account));

    public static AgentProbe SignedOut(string id, AgentSource source = AgentSource.Path, string version = "2.1.319") =>
        new(Program(id, source, version), true, new AgentAuthStatus(AgentAuthState.SignedOut));

    public static AgentRelease Release(string id, string version, long size = 58 * 1024 * 1024) =>
        new(id, version, new Uri($"https://example.com/{id}"), size, new string('a', 64), AgentPackageKind.Binary, id, "signature checked");

    public Task<AgentProbe> ProbeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"probe {agentId}");
        return Task.FromResult(Probes.GetValueOrDefault(agentId) ?? AgentProbe.Missing);
    }

    public Task<AgentReleaseLookup> FindReleaseAsync(string agentId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"release {agentId}");
        return Task.FromResult(Releases.TryGetValue(agentId, out var release)
            ? new AgentReleaseLookup(release)
            : AgentReleaseLookup.Fail(AgentInstallProblem.Offline, "offline"));
    }

    public Task<AgentInstallResult> InstallAsync(string agentId, IProgress<AgentInstallProgress>? progress, CancellationToken cancellationToken = default)
    {
        Calls.Add($"install {agentId}");
        return Install(agentId, progress, cancellationToken);
    }

    public Task<AgentSignInResult> SignInAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        Calls.Add($"sign-in {installation.AgentId}");
        return SignIn(installation, cancellationToken);
    }

    public Task<AgentAuthStatus> SignOutAsync(AgentInstallation installation, CancellationToken cancellationToken = default)
    {
        Calls.Add($"sign-out {installation.AgentId}");
        return SignOut(installation);
    }
}
