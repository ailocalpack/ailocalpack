using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>Global chrome (logo, back, Activity, Settings) around the current page.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly Localizer _localizer;

    public MainWindowViewModel(INavigationService navigation, IPageFactory pages, Localizer localizer)
    {
        _navigation = navigation;
        _pages = pages;
        _localizer = localizer;
        _navigation.Navigated += (_, _) => OnNavigated();
        _localizer.CultureChanged += (_, _) => OnPropertyChanged(nameof(BackLabel));
        if (_navigation.Current is null)
        {
            _navigation.Reset(_pages.Create<LibraryViewModel>());
        }
        else
        {
            OnNavigated();
        }
    }

    public PageViewModel? CurrentPage => _navigation.Current;

    public bool CanGoBack => _navigation.CanGoBack;

    /// <summary>Back button text: "‹ " plus the title of the page it returns to.</summary>
    public string BackLabel =>
        _navigation.Previous is { } previous ? _localizer.Format(nameof(Strings.Chrome_BackTo), previous.Title) : string.Empty;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _navigation.GoBack();

    [RelayCommand]
    private void OpenSettings() => _navigation.NavigateTo(_pages.Create<SettingsViewModel>());

    /// <summary>Activity panel arrives in task 38; the button is already in the chrome.</summary>
    [RelayCommand]
    private static void OpenActivity()
    {
    }

    private void OnNavigated()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackLabel));
        GoBackCommand.NotifyCanExecuteChanged();
    }
}
