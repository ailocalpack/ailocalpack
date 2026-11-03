using Avalonia.Threading;

namespace AiLocalPack.Desktop.Services;

/// <summary>Runs view-model updates on the UI thread (catalog and scan events arrive from background threads).</summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> now when already on the UI thread, otherwise queues it there.</summary>
    void Post(Action action);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
