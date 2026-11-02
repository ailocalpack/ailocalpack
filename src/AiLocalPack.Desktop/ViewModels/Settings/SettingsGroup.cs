using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>A titled card of rows inside a section ("Language", "Appearance"), with an optional note under it.</summary>
public sealed class SettingsGroup : ObservableObject, ISearchableGroup
{
    private readonly Localizer _localizer;
    private readonly string _titleKey;
    private readonly Func<AppSettings, string?>? _noteKey;
    private readonly SettingsStore _store;

    public SettingsGroup(SettingRowServices services, string titleKey, IReadOnlyList<SettingRow> rows, Func<AppSettings, string?>? noteKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _localizer = services.Localizer;
        _store = services.Store;
        _titleKey = titleKey;
        _noteKey = noteKey;
        Rows = rows;
        _store.Changed += (_, _) => OnPropertyChanged(nameof(Note));
        _localizer.CultureChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Note));
        };
    }

    public string Title => _localizer[_titleKey];

    public string? Note => _noteKey?.Invoke(_store.Current) is { } key ? _localizer[key] : null;

    public bool HasNote => _noteKey is not null;

    public IReadOnlyList<SettingRow> Rows { get; }

    IEnumerable<string> ISearchableGroup.SearchTexts => [Title, _localizer.English(_titleKey)];

    IEnumerable<ISearchableRow> ISearchableGroup.Rows => Rows;
}
