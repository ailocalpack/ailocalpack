namespace AiLocalPack.TestKit;

/// <summary>
/// An <see cref="IProgress{T}"/> that records every report synchronously (unlike <see cref="Progress{T}"/>, which posts to
/// a synchronization context), with an optional callback run on each report, e.g. to cancel a scan midway.
/// </summary>
public sealed class RecordingProgress<T>(Action<T>? onReport = null) : IProgress<T>
{
    private readonly List<T> _reports = [];

    public IReadOnlyList<T> Reports => _reports;

    public void Report(T value)
    {
        _reports.Add(value);
        onReport?.Invoke(value);
    }
}
