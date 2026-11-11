using AiLocalPack.Desktop.Activity;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings.About;

/// <summary>The top of Settings → About: logo, "AI Local Pack 0.4.0", channel · platform · ©.</summary>
public sealed class AboutHeader(string title, Func<string> meta) : ObservableObject
{
    public string Title { get; } = title;

    public string Meta => meta();

    public void Refresh() => OnPropertyChanged(nameof(Meta));
}

/// <summary>
/// App updates (design §8): the state in words and its button: <c>Check for updates</c> → <i>Checking</i> → "latest" +
/// <c>Check again</c>, or "X is downloaded" + <c>Restart to update</c> and <c>What's new</c>; nothing to press while off.
/// </summary>
public sealed class AppUpdateRow : SettingRow
{
    private readonly AppUpdater _updater;
    private readonly TimeProvider _time;
    private string? _error;

    public AppUpdateRow(SettingRowServices services, AppUpdater updater, IUiDispatcher ui, TimeProvider time, Func<string?> restart, Action<Uri> open)
        : base(services, nameof(Strings.Settings_About_Updates_Label), nameof(Strings.Settings_About_Updates_Keywords))
    {
        ArgumentNullException.ThrowIfNull(restart);
        ArgumentNullException.ThrowIfNull(open);
        _updater = updater;
        _time = time;
        CheckCommand = new AsyncRelayCommand(async () =>
        {
            Error = null;
            await _updater.CheckAsync();
        }, () => CanCheck);
        RestartCommand = new RelayCommand(() => Error = restart(), () => Status == AppUpdateStatus.Ready);
        WhatsNewCommand = new RelayCommand(() => open(ReleaseNotes(_updater.Version!)), () => _updater.Version is not null);
        updater.Changed += (_, _) => ui.Post(Raise);
    }

    public AppUpdateStatus Status => _updater.Status;

    public override string? Help => Localizer.Format(
        Status switch
        {
            AppUpdateStatus.Off => nameof(Strings.Settings_About_Updates_Off),
            AppUpdateStatus.Checking => nameof(Strings.Settings_About_Updates_Checking),
            AppUpdateStatus.UpToDate => nameof(Strings.Settings_About_Updates_Latest),
            AppUpdateStatus.Downloading => nameof(Strings.Settings_About_Updates_Downloading),
            AppUpdateStatus.Ready => nameof(Strings.Settings_About_Updates_Ready),
            AppUpdateStatus.Restarting => nameof(Strings.Settings_About_Updates_Restarting),
            AppUpdateStatus.Failed => nameof(Strings.Settings_About_Updates_Failed),
            _ => nameof(Strings.Settings_About_Updates_Idle),
        },
        Status == AppUpdateStatus.UpToDate && _updater.CheckedAt is { } at ? ActivityTexts.Time(at, _time.LocalTimeZone, Localizer) : _updater.Version ?? string.Empty,
        _updater.Percent);

    /// <summary>The download's progress, 0–100.</summary>
    public int Percent => _updater.Percent;

    public IAsyncRelayCommand CheckCommand { get; }

    public IRelayCommand RestartCommand { get; }

    public IRelayCommand WhatsNewCommand { get; }

    /// <summary><c>Check for updates</c> / <c>Check again</c> is shown.</summary>
    public bool CanCheck => Status is AppUpdateStatus.Idle or AppUpdateStatus.UpToDate or AppUpdateStatus.Failed;

    public bool IsChecking => Status == AppUpdateStatus.Checking;

    public bool IsDownloading => Status == AppUpdateStatus.Downloading;

    public bool IsReady => Status == AppUpdateStatus.Ready;

    public bool IsRestarting => Status == AppUpdateStatus.Restarting;

    public bool IsFailed => Status == AppUpdateStatus.Failed;

    public bool IsLatest => Status == AppUpdateStatus.UpToDate;

    public string CheckText => Localizer[Status == AppUpdateStatus.Idle ? nameof(Strings.Settings_About_Check) : nameof(Strings.Settings_About_CheckAgain)];

    public string CheckingText => Localizer[nameof(Strings.Settings_About_Checking)];

    public string RestartText => Localizer[nameof(Strings.Settings_About_Restart)];

    public string RestartingText => Localizer[nameof(Strings.Settings_About_Restarting)];

    public string WhatsNewText => Localizer[nameof(Strings.Settings_About_WhatsNew)];

    /// <summary>Why <c>Restart to update</c> didn't restart (an install runs); <c>null</c> otherwise.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    /// <summary>The release's page on GitHub (Releases are tagged <c>v&lt;version&gt;</c>).</summary>
    public static Uri ReleaseNotes(string version) => new($"{VelopackUpdateSource.Repository.AbsoluteUri.TrimEnd('/')}/releases/tag/v{Uri.EscapeDataString(version)}");

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        Raise();
    }

    private void Raise()
    {
        if (Status != AppUpdateStatus.Ready)
        {
            Error = null;
        }

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsRestarting));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsLatest));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(CheckingText));
        OnPropertyChanged(nameof(RestartText));
        OnPropertyChanged(nameof(RestartingText));
        OnPropertyChanged(nameof(WhatsNewText));
        CheckCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        WhatsNewCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A link to a page on GitHub: icon, title, a muted line under it, ↗; opens in the browser.</summary>
public sealed class LinkSettingRow : SettingRow
{
    private readonly string? _helpKey;
    private readonly string? _helpText;

    public LinkSettingRow(SettingRowServices services, string labelKey, string? helpKey, string? helpText, string iconKey, Uri uri, Action<Uri> open)
        : base(services, labelKey, nameof(Strings.Settings_About_Links_Keywords))
    {
        ArgumentNullException.ThrowIfNull(open);
        _helpKey = helpKey;
        _helpText = helpText;
        IconKey = iconKey;
        Uri = uri;
        OpenCommand = new RelayCommand(() => open(Uri));
    }

    /// <summary>Resource key of the icon (a brand mark when it starts with <c>Brand</c>).</summary>
    public string IconKey { get; }

    public bool IsBrand => IconKey.StartsWith("Brand", StringComparison.Ordinal);

    public Uri Uri { get; }

    public override string? Help => _helpKey is not null ? Localizer[_helpKey] : _helpText;

    public IRelayCommand OpenCommand { get; }

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Uri.AbsoluteUri];
}

/// <summary>Licenses: the app's and the registry's, and <c>Third-party notices</c>.</summary>
public sealed class LicensesRow : SettingRow
{
    public LicensesRow(SettingRowServices services, Action notices)
        : base(services, nameof(Strings.Settings_About_Licenses_Label), nameof(Strings.Settings_About_Licenses_Keywords))
    {
        ArgumentNullException.ThrowIfNull(notices);
        NoticesCommand = new RelayCommand(notices);
    }

    public override string? Help => Localizer[nameof(Strings.Settings_About_Licenses_Help)];

    public IRelayCommand NoticesCommand { get; }

    public string NoticesText => Localizer[nameof(Strings.Settings_About_Notices)];

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(NoticesText));
    }
}

/// <summary>A row of text only: label and a paragraph (the disclaimer).</summary>
public sealed class InfoSettingRow(SettingRowServices services, string labelKey, string textKey, string? keywordsKey)
    : SettingRow(services, labelKey, keywordsKey)
{
    public override string? Help => Localizer[textKey];
}
