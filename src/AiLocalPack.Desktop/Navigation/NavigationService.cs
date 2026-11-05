using AiLocalPack.Desktop.ViewModels;

namespace AiLocalPack.Desktop.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly List<PageViewModel> _stack = [];

    public PageViewModel? Current => _stack.Count > 0 ? _stack[^1] : null;

    public PageViewModel? Previous => _stack.Count > 1 ? _stack[^2] : null;

    public bool CanGoBack => _stack.Count > 1;

    public event EventHandler? Navigated;

    public void Reset(PageViewModel root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var page in _stack.Where(p => !ReferenceEquals(p, root)))
        {
            page.OnClosed();
        }

        _stack.Clear();
        _stack.Add(root);
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    public void NavigateTo(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Current is { } current && current.IsSamePage(page))
        {
            if (!ReferenceEquals(current, page))
            {
                page.OnClosed();
            }

            return;
        }

        _stack.Add(page);
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        var closed = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        closed.OnClosed();
        Navigated?.Invoke(this, EventArgs.Empty);
    }
}
