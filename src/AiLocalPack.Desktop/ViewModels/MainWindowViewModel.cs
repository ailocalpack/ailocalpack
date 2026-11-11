using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.Welcome;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>Global chrome (logo, back, Activity, Settings) around the current page, and the modal dialog over it.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPageFactory _pages;
    private readonly Localizer _localizer;
    private readonly IDialogService _dialogs;

    public MainWindowViewModel(
        INavigationService navigation,
        IPageFactory pages,
        Localizer localizer,
        IDialogService dialogs,
        StartupProgress startup,
        ToastService toasts,
        ActivityPanelViewModel? activity = null)
    {
        Toasts = toasts;
        Startup = startup;
        Activity = activity;
        _navigation = navigation;
        _pages = pages;
        _localizer = localizer;
        _dialogs = dialogs;
        _navigation.Navigated += (_, _) => OnNavigated();
        _dialogs.Changed += (_, _) => OnDialogChanged();
        _localizer.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(BackLabel));
        };
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

    /// <summary>Toasts in the bottom right corner (install done / failed).</summary>
    public ToastService Toasts { get; }

    /// <summary>Startup checks: "Checking for changes…" and the ring on 🔔 while background checks run, their list in the Activity panel.</summary>
    public StartupProgress Startup { get; }

    /// <summary>The Activity panel under 🔔 and its badge (task 38).</summary>
    public ActivityPanelViewModel? Activity { get; }

    public bool CanGoBack => _navigation.CanGoBack && !HasDialog;

    /// <summary>The modal dialog shown over the window (the page and chrome are disabled meanwhile), or <c>null</c>.</summary>
    public DialogViewModel? Dialog => _dialogs.Current;

    public bool HasDialog => _dialogs.Current is not null;

    /// <summary>Back button text: "‹ " plus the title of the page it returns to.</summary>
    public string BackLabel =>
        _navigation.Previous is { } previous ? _localizer.Format(nameof(Strings.Chrome_BackTo), previous.Title) : string.Empty;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _navigation.GoBack();

    /// <summary>Whether Settings is shown: the ⚙ button is drawn as active.</summary>
    public bool IsSettingsOpen => _navigation.Current is SettingsViewModel;

    /// <summary>Esc: an open dialog handles it (or closes); else the page handles it first (clears a search), otherwise back.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (_dialogs.Current is { } dialog)
        {
            if (!dialog.TryHandleEscape())
            {
                _dialogs.Close(dialog);
            }

            return;
        }

        if (_navigation.Current?.TryHandleEscape() != true)
        {
            _navigation.GoBack();
        }
    }

    /// <summary><c>?</c> in the chrome: the welcome tour again (design §10).</summary>
    [RelayCommand]
    private void ShowWelcomeTour() => _dialogs.Show<WelcomeTourViewModel>();

    [RelayCommand]
    private void OpenSettings() => _navigation.NavigateTo(_pages.Create<SettingsViewModel>());

    private void OnDialogChanged()
    {
        OnPropertyChanged(nameof(Dialog));
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private void OnNavigated()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackLabel));
        OnPropertyChanged(nameof(IsSettingsOpen));
        GoBackCommand.NotifyCanExecuteChanged();
    }
}
