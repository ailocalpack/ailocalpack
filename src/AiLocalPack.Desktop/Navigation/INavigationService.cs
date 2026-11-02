using AiLocalPack.Desktop.ViewModels;

namespace AiLocalPack.Desktop.Navigation;

/// <summary>View-model-first navigation with a back stack. The root page (Library) is never popped.</summary>
public interface INavigationService
{
    PageViewModel? Current { get; }

    /// <summary>The page <see cref="GoBack"/> returns to, or <c>null</c> on the root.</summary>
    PageViewModel? Previous { get; }

    bool CanGoBack { get; }

    event EventHandler? Navigated;

    /// <summary>Clears the back stack and shows <paramref name="root"/>.</summary>
    void Reset(PageViewModel root);

    /// <summary>Pushes <paramref name="page"/>. No-op when it is the same page as <see cref="Current"/>.</summary>
    void NavigateTo(PageViewModel page);

    /// <summary>Returns to the previous page. No-op on the root.</summary>
    void GoBack();
}
