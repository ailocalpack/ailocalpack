using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>A screen shown in the main window's content area.</summary>
public abstract class PageViewModel : ObservableObject
{
    /// <summary>Short name in the current UI language, used for the back button ("‹ Library"). Read again after a language switch.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Whether <paramref name="other"/> shows the same screen as this page, so navigating to it is a no-op.
    /// Default: same type. Pages with parameters (a game page) compare them too.
    /// </summary>
    public virtual bool IsSamePage(PageViewModel other) => other.GetType() == GetType();

    /// <summary>Lets the page use Esc first (clear a search); returns <c>false</c> to let the window go back.</summary>
    public virtual bool TryHandleEscape() => false;

    /// <summary>
    /// The page left the back stack (went back from it, reset) or was never shown (navigating to the page already
    /// shown): it stops listening to the app's services, so the closed page can be collected.
    /// </summary>
    public virtual void OnClosed()
    {
    }
}
