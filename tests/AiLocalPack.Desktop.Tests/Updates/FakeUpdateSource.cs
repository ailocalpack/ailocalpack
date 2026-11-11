using AiLocalPack.Desktop.Updates;

namespace AiLocalPack.Desktop.Tests.Updates;

/// <summary>
/// An app update feed for tests: installed by default with nothing newer; <see cref="Offer"/> sets the release found,
/// <see cref="CheckGate"/> / <see cref="DownloadGate"/> hold a step until the test lets it go, the failure fields make it throw.
/// </summary>
public sealed class FakeUpdateSource : IAppUpdateSource
{
    public AppInstallKind Install { get; set; } = AppInstallKind.Installed;

    public string? CurrentVersion { get; set; } = "0.4.0";

    public string? Channel { get; set; }

    public AppUpdateOffer? PendingRestart { get; set; }

    /// <summary>The release the next check finds; <c>null</c> = this is the latest.</summary>
    public string? Offer { get; set; }

    public Exception? CheckFailure { get; set; }

    public Exception? DownloadFailure { get; set; }

    public Exception? ApplyFailure { get; set; }

    /// <summary>When set, a check waits for it.</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    /// <summary>When set, a download reports <see cref="DownloadProgress"/> and then waits for it.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    public IReadOnlyList<int> DownloadProgress { get; set; } = [50];

    public int Checks { get; private set; }

    public List<string> Downloaded { get; } = [];

    /// <summary>The release handed to the updater on exit.</summary>
    public AppUpdateOffer? Applied { get; private set; }

    public async Task<AppUpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        if (CheckGate is { } gate)
        {
            await gate.Task;
        }

        if (CheckFailure is { } failure)
        {
            throw failure;
        }

        return Offer is { } version ? new AppUpdateOffer(version, null) : null;
    }

    public async Task DownloadAsync(AppUpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
    {
        foreach (var percent in DownloadProgress)
        {
            progress(percent);
        }

        if (DownloadGate is { } gate)
        {
            await gate.Task;
        }

        if (DownloadFailure is { } failure)
        {
            throw failure;
        }

        Downloaded.Add(offer.Version);
    }

    public void ApplyOnExit(AppUpdateOffer offer)
    {
        if (ApplyFailure is { } failure)
        {
            throw failure;
        }

        Applied = offer;
    }
}

/// <summary>Counts the quits instead of quitting.</summary>
public sealed class FakeAppQuit : IAppQuit
{
    public int Quits { get; private set; }

    public void Quit() => Quits++;
}
