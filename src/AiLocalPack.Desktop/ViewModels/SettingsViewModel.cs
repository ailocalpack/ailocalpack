using System.Reflection;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>
/// Settings screen (design §8): section list with search on the left, the selected section or the search results on
/// the right. Open a section directly with <c>pages.Create&lt;SettingsViewModel&gt;(SettingsSectionId.Libraries)</c>.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly Localizer _localizer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavSelection))]
    private SettingsSection _selectedSection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(NavSelection), nameof(NoResultsText))]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private IReadOnlyList<SettingsSearchHit> _results = [];

    public SettingsViewModel(IEnumerable<SettingsSection> sections, Localizer localizer, SettingsSectionId section = SettingsSectionId.General)
    {
        ArgumentNullException.ThrowIfNull(sections);
        _localizer = localizer;
        Sections = [.. sections];
        ArgumentOutOfRangeException.ThrowIfZero(Sections.Count, nameof(sections));
        _selectedSection = Find(section);
        _localizer.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NoResultsText));
            Search();
        };
    }

    public override string Title => Strings.Settings_Title;

    public IReadOnlyList<SettingsSection> Sections { get; }

    /// <summary>The highlighted item of the section list: none while search results are shown. Picking one opens it.</summary>
    public SettingsSection? NavSelection
    {
        get => IsSearching ? null : SelectedSection;
        set
        {
            if (value is not null)
            {
                OpenSection(value);
            }
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(Query);

    public bool HasNoResults => IsSearching && Results.Count == 0;

    public string NoResultsText => _localizer.Format(nameof(Strings.Settings_Search_NoResults), Query.Trim());

    /// <summary>App version for the footer of the section list.</summary>
    public string Version { get; } = "ailocalpack " + AppVersion;

    private static string AppVersion
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var build = version.IndexOf('+', StringComparison.Ordinal);
            return build < 0 ? version : version[..build];
        }
    }

    /// <summary>Selects a section (deep link, Activity ⚙) and leaves the search.</summary>
    public void ShowSection(SettingsSectionId id) => OpenSection(Find(id));

    /// <summary>Esc: clears the search when there is one; otherwise the window goes back.</summary>
    public override bool TryHandleEscape()
    {
        if (Query.Length == 0)
        {
            return false;
        }

        Query = string.Empty;
        return true;
    }

    [RelayCommand]
    private void OpenSection(SettingsSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        SelectedSection = section;
        Query = string.Empty;
    }

    partial void OnQueryChanged(string value) => Search();

    private void Search() =>
        Results = [.. SettingsSearch.Run<SettingsSection, SettingRow>(Sections, Query).Select(r => new SettingsSearchHit(r.Section, r.Rows))];

    private SettingsSection Find(SettingsSectionId id) => Sections.FirstOrDefault(s => s.Id == id) ?? Sections[0];
}
