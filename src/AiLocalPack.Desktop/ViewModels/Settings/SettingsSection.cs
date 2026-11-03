using System.Collections.ObjectModel;
using AiLocalPack.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>
/// One entry of the Settings section list and the page it opens. A section without groups shows "coming in a later
/// version". The badge slot shows problems the player has to act on (count, amber or red); otherwise a quiet hint.
/// Sections are singletons (registered in <see cref="Composition"/> in display order), so their rows keep one state.
/// </summary>
public class SettingsSection : ObservableObject, ISearchableSection
{
    private readonly string _titleKey;
    private readonly string _descriptionKey;
    private int _badgeCount;
    private bool _badgeIsError;
    private string? _hint;

    public SettingsSection(SettingsSectionId id, string iconKey, string titleKey, string descriptionKey, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Id = id;
        IconKey = iconKey;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        Localizer = localizer;
        Localizer.CultureChanged += (_, _) => OnCultureChanged();
    }

    public SettingsSectionId Id { get; }

    /// <summary>Resource key of the icon geometry in <c>Styles/Theme.axaml</c>.</summary>
    public string IconKey { get; }

    public string Title => Localizer[_titleKey];

    public string Description => Localizer[_descriptionKey];

    public virtual IReadOnlyList<SettingsGroup> Groups => [];

    public bool IsComingSoon => Groups.Count == 0;

    /// <summary>The section's own problems, shown on top of its page; sections that track problems keep the badge in step.</summary>
    public ObservableCollection<SettingsAlert> Alerts { get; } = [];

    /// <summary>Problems to act on in this section; 0 hides the badge.</summary>
    public int BadgeCount
    {
        get => _badgeCount;
        set
        {
            if (SetProperty(ref _badgeCount, value))
            {
                OnPropertyChanged(nameof(HasBadge));
                OnPropertyChanged(nameof(HasHint));
            }
        }
    }

    /// <summary>Red badge (an error) instead of amber (a warning).</summary>
    public bool BadgeIsError
    {
        get => _badgeIsError;
        set => SetProperty(ref _badgeIsError, value);
    }

    public bool HasBadge => BadgeCount > 0;

    /// <summary>Quiet text shown instead of a badge ("9.8 GB", "update").</summary>
    public string? Hint
    {
        get => _hint;
        set
        {
            if (SetProperty(ref _hint, value))
            {
                OnPropertyChanged(nameof(HasHint));
            }
        }
    }

    public bool HasHint => !HasBadge && !string.IsNullOrEmpty(Hint);

    protected Localizer Localizer { get; }

    IEnumerable<string> ISearchableSection.SearchTexts => [Title, Localizer.English(_titleKey)];

    IEnumerable<ISearchableGroup> ISearchableSection.Groups => Groups;

    protected virtual void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
    }
}
