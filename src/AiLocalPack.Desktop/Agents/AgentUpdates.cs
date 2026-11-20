using AiLocalPack.Agents;
using AiLocalPack.Agents.Install;
using AiLocalPack.Core.Activity;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Agents;

/// <summary>A newer release of an agent the app installed itself.</summary>
/// <param name="AgentId">The adapter's ID.</param>
/// <param name="Installed">The version of the app's copy.</param>
/// <param name="Available">The vendor's current release.</param>
public sealed record AgentUpdateOffer(string AgentId, string Installed, string Available);

/// <summary>
/// Updates of the agents the app installed itself (<see cref="AgentSource.Managed"/>; the player updates their own copy
/// the way they installed it). Finds them at start (Settings → AI agents checks its rows then and on every opening, and
/// each row reports what it found through <see cref="Report"/>) and every <see cref="CheckInterval"/> while the app runs;
/// each new version is one Activity event <see cref="ActivityEventKind.AgentUpdateAvailable"/> with <c>Update</c>.
/// The update itself is the row's <c>Update to X</c> (the installer puts the new build in, then removes the old one), held
/// back while a generation or a Fix with AI run uses the agents (<see cref="IsBusy"/>): a run started from the old
/// folder would lose it. Thread-safe; <see cref="Changed"/> may come from any thread. Never throws.
/// </summary>
public sealed class AgentUpdates : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly IAgentHost _host;
    private readonly ActivityStore _activity;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentUpdates> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, AgentUpdateOffer> _offers = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;
    private Task? _check;

    public AgentUpdates(IAgentHost host, ActivityStore activity, TimeProvider time, ILogger<AgentUpdates> logger)
    {
        _host = host;
        _activity = activity;
        _time = time;
        _logger = logger;
    }

    /// <summary>An offer came or went (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>The updates waiting, by agent.</summary>
    public IReadOnlyList<AgentUpdateOffer> Offers
    {
        get
        {
            lock (_lock)
            {
                return [.. _offers.Values.OrderBy(o => o.AgentId, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>The update waiting for the agent, or <c>null</c>.</summary>
    public AgentUpdateOffer? OfferFor(string agentId)
    {
        lock (_lock)
        {
            return _offers.GetValueOrDefault(agentId);
        }
    }

    /// <summary>
    /// A generation or a Fix with AI run is going on: the agents' folders aren't swapped under it (updating waits until
    /// it stops or is paused).
    /// </summary>
    public bool IsBusy => _activity.Tasks.Any(t => t.Kind == ActivityTaskKind.Generation);

    /// <summary>
    /// What a check found for one agent: the program in use and the vendor's current release (<c>null</c>: unknown, the
    /// offer stays as it was). Only the app's own copy with a known, older version gets an offer; anything else drops it.
    /// </summary>
    public void Report(string agentId, AgentInstallation? installation, string? releaseVersion)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        if (installation is { Source: AgentSource.Managed, Version: { } installed })
        {
            if (releaseVersion is null)
            {
                return;
            }

            Set(agentId, AgentRelease.IsNewer(releaseVersion, installed) ? new AgentUpdateOffer(agentId, installed, releaseVersion) : null);
            return;
        }

        Set(agentId, null);
    }

    /// <summary>Looks for updates of every agent the app installed (programs, then releases); a check already running is shared.</summary>
    public Task CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_check is { IsCompleted: false } running)
            {
                return running;
            }

            _check = RunCheckAsync(cancellationToken);
            return _check;
        }
    }

    /// <summary>Checks again every <see cref="CheckInterval"/> from now on (once; later calls do nothing).</summary>
    public void StartSchedule()
    {
        lock (_lock)
        {
            _timer ??= _time.CreateTimer(_ => _ = CheckAsync(), null, CheckInterval, CheckInterval);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task RunCheckAsync(CancellationToken cancellationToken)
    {
        // Off the caller's path: the timer's thread or the UI thread returns at once.
        await Task.Yield();
        foreach (var agentId in _host.AgentIds)
        {
            try
            {
                var probe = await _host.ProbeAsync(agentId, cancellationToken).ConfigureAwait(false);
                if (probe.Installation is not { Source: AgentSource.Managed })
                {
                    Report(agentId, probe.Installation, null);
                    continue;
                }

                var lookup = await _host.FindReleaseAsync(agentId, cancellationToken).ConfigureAwait(false);
                if (lookup.Release is null)
                {
                    _logger.LogInformation("Agent {Agent}: no release found to compare ({Problem}: {Detail})", agentId, lookup.Problem, lookup.Detail);
                }

                Report(agentId, probe.Installation, lookup.Release?.Version);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Agent {Agent}: checking for an update failed", agentId);
            }
        }
    }

    private void Set(string agentId, AgentUpdateOffer? offer)
    {
        lock (_lock)
        {
            var current = _offers.GetValueOrDefault(agentId);
            if (current == offer)
            {
                return;
            }

            if (offer is null)
            {
                _offers.Remove(agentId);
            }
            else
            {
                _offers[agentId] = offer;
            }
        }

        if (offer is not null)
        {
            _logger.LogInformation("Agent {Agent}: {Available} is out (the app's copy is {Installed})", agentId, offer.Available, offer.Installed);
            _activity.Add(ActivityEvent.Create(ActivityEventKind.AgentUpdateAvailable, _time.GetUtcNow()) with
            {
                Agent = offer.AgentId.ToLowerInvariant(),
                Version = offer.Available,
                Key = "agent-update:" + offer.AgentId.ToLowerInvariant() + ":" + offer.Available,
            });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
