using AiLocalPack.Agents;
using AiLocalPack.Desktop.Agents;

namespace AiLocalPack.Desktop.ViewModels.Settings.Agents;

/// <summary>Where an agent's row is (design §8, Settings → AI agents).</summary>
public enum AgentRowState
{
    /// <summary>Looking for the program and asking its sign-in.</summary>
    Checking,

    /// <summary>No program: <c>Install</c>. Never a warning (agents are asked for only when generating).</summary>
    NotInstalled,

    /// <summary>Downloading and checking the official build: percent, <c>Cancel</c>.</summary>
    Installing,

    /// <summary>A version the app can't drive: <c>Update</c> for the app's copy, a hint for the player's own.</summary>
    TooOld,

    /// <summary>Installed, never signed in (or signed out here): <c>Sign in</c>.</summary>
    NotSignedIn,

    /// <summary>Was signed in, now isn't: a warning with <c>Sign in</c>.</summary>
    SessionExpired,

    /// <summary>The sign-in couldn't be checked: <c>Sign in</c>, <c>Check again</c>.</summary>
    SignInUnknown,

    /// <summary>The agent's own login runs in the browser: <c>Cancel</c>.</summary>
    SigningIn,

    /// <summary>Signed in: <c>Update to X</c> when the app's copy has one, <c>Sign out</c>.</summary>
    Ready,

    /// <summary>Installing a newer build of the app's copy.</summary>
    Updating,
}

/// <summary>The rules from what the app found to a row state, and which states count as problems.</summary>
public static class AgentRowStates
{
    /// <summary>The state for <paramref name="probe"/>.</summary>
    /// <param name="wasSignedIn">The app saw this agent signed in before and the player didn't sign out here.</param>
    public static AgentRowState From(AgentProbe probe, bool wasSignedIn)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.Installation is null)
        {
            return AgentRowState.NotInstalled;
        }

        if (!probe.IsSupported)
        {
            return AgentRowState.TooOld;
        }

        return probe.Auth?.State switch
        {
            AgentAuthState.SignedIn => AgentRowState.Ready,
            AgentAuthState.SignedOut => wasSignedIn ? AgentRowState.SessionExpired : AgentRowState.NotSignedIn,
            _ => AgentRowState.SignInUnknown,
        };
    }

    /// <summary>
    /// Whether the state is a warning on top of the section and in its badge. Only an expired session is: a missing,
    /// old or never signed-in agent is asked for when generating, not flagged.
    /// </summary>
    public static bool IsWarning(AgentRowState state) => state == AgentRowState.SessionExpired;

    /// <summary>An operation runs: no new check or action starts.</summary>
    public static bool IsBusy(AgentRowState state) =>
        state is AgentRowState.Checking or AgentRowState.Installing or AgentRowState.SigningIn or AgentRowState.Updating;
}
