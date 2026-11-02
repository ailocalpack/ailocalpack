using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>A screen shown in the main window's content area.</summary>
public abstract class PageViewModel : ObservableObject
{
    /// <summary>Short name, used for the back button ("‹ Library").</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Whether <paramref name="other"/> shows the same screen as this page, so navigating to it is a no-op.
    /// Default: same type. Pages with parameters (a game page) compare them too.
    /// </summary>
    public virtual bool IsSamePage(PageViewModel other) => other.GetType() == GetType();
}
