using AiLocalPack.Desktop.Navigation;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>Library (home) screen. Stub: a few fake games to navigate to; the cover grid comes in task 11.</summary>
public sealed partial class LibraryViewModel(INavigationService navigation, IPageFactory pages) : PageViewModel
{
    public override string Title => "Library";

    public IReadOnlyList<StubGame> Games { get; } =
    [
        new("stub:1", "Sample game"),
        new("stub:2", "Another sample game"),
    ];

    [RelayCommand]
    private void OpenGame(StubGame game) => navigation.NavigateTo(pages.Create<GamePageViewModel>(game));
}
