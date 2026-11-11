using AiLocalPack.Core.Publishing;
using AiLocalPack.Desktop.Publishing;

namespace AiLocalPack.Desktop.Tests.Publishing;

/// <summary>
/// PR reads as the test sets them, so no test reaches GitHub: <see cref="Answer"/> per PR number (default: open, CI
/// running at head <c>h1</c>); every read is recorded.
/// </summary>
internal sealed class FakePullRequestStatus : IPullRequestStatus
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, PullRequestCheckResult> _answers = [];
    private readonly List<(RegistryRepo Registry, int Number)> _reads = [];

    public IReadOnlyList<(RegistryRepo Registry, int Number)> Reads
    {
        get
        {
            lock (_lock)
            {
                return [.. _reads];
            }
        }
    }

    public void Answer(int number, PullRequestCheckResult result)
    {
        lock (_lock)
        {
            _answers[number] = result;
        }
    }

    public void Answer(int number, PullRequestState state, CiState ci, string head = "h1", params CiAnnotation[] annotations) =>
        Answer(number, new PullRequestCheckResult.Checked(new PullRequestStatus(number, state, head, ci, annotations, state == PullRequestState.Open ? null : DateTimeOffset.UnixEpoch)));

    public Task<PullRequestCheckResult> CheckAsync(RegistryRepo registry, int number, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _reads.Add((registry, number));
            return Task.FromResult(_answers.GetValueOrDefault(number)
                ?? new PullRequestCheckResult.Checked(new PullRequestStatus(number, PullRequestState.Open, "h1", CiState.Pending, [])));
        }
    }
}
