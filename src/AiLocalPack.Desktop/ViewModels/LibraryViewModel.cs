using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>Library (home) screen. Stub: a few fake games to navigate to; the cover grid comes in task 11.</summary>
public sealed partial class LibraryViewModel : PageViewModel
{
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly Localizer _localizer;

    public LibraryViewModel(INavigationService navigation, IPageFactory pages, Localizer localizer)
    {
        _navigation = navigation;
        _pages = pages;
        _localizer = localizer;
        _localizer.CultureChanged += (_, _) => OnPropertyChanged(nameof(GameCount));
    }

    public override string Title => Strings.Library_Title;

    public IReadOnlyList<StubGame> Games { get; } =
    [
        new("stub:1", "Sample game"),
        new("stub:2", "Another sample game"),
    ];

    /// <summary>"2 games" / "2 игры".</summary>
    public string GameCount => _localizer.Plural(nameof(Strings.Library_GameCount_One), Games.Count);

    [RelayCommand]
    private void OpenGame(StubGame game) => _navigation.NavigateTo(_pages.Create<GamePageViewModel>(game));
}
