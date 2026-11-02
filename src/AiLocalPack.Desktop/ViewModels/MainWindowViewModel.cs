using AiLocalPack.Desktop.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>Global chrome (logo, back, Activity, Settings) around the current page.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;

    public MainWindowViewModel(INavigationService navigation, IPageFactory pages)
    {
        _navigation = navigation;
        _pages = pages;
        _navigation.Navigated += (_, _) => OnNavigated();
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
    public string BackLabel => _navigation.Previous is { } previous ? $"‹ {previous.Title}" : string.Empty;

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
