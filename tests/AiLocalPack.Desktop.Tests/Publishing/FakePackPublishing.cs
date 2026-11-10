using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Publishing;
using AiLocalPack.Desktop.Publishing;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>
/// Publishing as the test sets it, so no test reaches GitHub: records each request; by default opens PR #7 and records it
/// in the pack's report as the real client does. <see cref="Result"/> replaces the outcome; <see cref="Hold"/> keeps the
/// publish at a step until <see cref="Release"/>.
/// </summary>
internal sealed class FakePackPublishing(LocalPackStore store) : IPackPublishing
{
    private TaskCompletionSource? _hold;

    public RegistryRepo Registry { get; } = new("ailocalpack", "registry-test");

    public List<PublishRequest> Requests { get; } = [];

    /// <summary>The outcome for a request; <c>null</c> = PR #7 opened.</summary>
    public Func<PublishRequest, PublishResult>? Result { get; set; }

    /// <summary>The steps reported before the outcome (default: all three).</summary>
    public IReadOnlyList<PublishStep> Steps { get; set; } = [PublishStep.Fork, PublishStep.Commit, PublishStep.PullRequest];

    /// <summary>Stops after reporting the steps until <see cref="Release"/>.</summary>
    public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _hold?.TrySetResult();

    public static Uri PrUrl { get; } = new("https://github.com/ailocalpack/registry-test/pull/7");

    public async Task<PublishResult> PublishAsync(PublishRequest request, IProgress<PublishStep> progress, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        foreach (var step in Steps)
        {
            progress.Report(step);
        }

        if (_hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        if (Result?.Invoke(request) is { } result)
        {
            return result;
        }

        var local = request.Pack.Manifest;
        var (id, revision) = request.Target is PublishTarget.RevisionOf r ? (r.PackId, 2) : (local.Id, 1);
        var manifest = local with { Id = id, Revisions = [.. local.Revisions.Take(1).Select(x => x with { Number = revision })] };
        var branch = $"pack/steam-10/{local.Language}/{id}/rev-{revision}";
        store.UpdateReport(local.Game, local.Language, local.Id, report => report with
        {
            Published = new LocalPackPublication(Registry.FullName, id, revision, 7, PrUrl, "octocat/registry-test", branch, DateTimeOffset.UnixEpoch),
        });
        return new PublishResult.Published(7, PrUrl, manifest, "octocat/registry-test", branch, Resubmitted: false, Unchanged: false);
    }
}
