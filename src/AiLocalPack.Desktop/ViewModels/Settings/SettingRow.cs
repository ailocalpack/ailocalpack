using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>
/// One setting: label and a one-line explanation on the left, the control on the right. Rows are registered by their
/// section with search keywords, edit <see cref="AppSettings"/> through <see cref="SettingsStore"/> (changes apply
/// immediately, no Save) and follow the store and the UI language.
/// </summary>
public abstract class SettingRow : ObservableObject, ISearchableRow
{
    private readonly string _labelKey;
    private readonly string? _keywordsKey;

    protected SettingRow(SettingRowServices services, string labelKey, string? keywordsKey)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(labelKey);
        Services = services;
        _labelKey = labelKey;
        _keywordsKey = keywordsKey;
        services.Store.Changed += OnStoreChanged;
        services.Localizer.CultureChanged += OnLocalizerCultureChanged;
    }

    public virtual string Label => Localizer[_labelKey];

    /// <summary>The one-line explanation under the label; <c>null</c> for none.</summary>
    public virtual string? Help => HelpKey(Settings) is { } key ? Localizer[key] : null;

    /// <summary>Link after <see cref="Help"/> ("Fix it on GitHub"); <c>null</c> for none.</summary>
    public virtual string? HelpLinkText => null;

    public virtual Uri? HelpLinkUri => null;

    public bool HasHelpLink => HelpLinkUri is not null;

    /// <summary>Label and keywords in the UI language and in English, so "theme" finds the row in any language.</summary>
    public virtual IEnumerable<string> SearchTexts
    {
        get
        {
            yield return Label;
            yield return Localizer.English(_labelKey);
            if (_keywordsKey is not null)
            {
                yield return Localizer[_keywordsKey];
                yield return Localizer.English(_keywordsKey);
            }
        }
    }

    protected SettingRowServices Services { get; }

    protected Localizer Localizer => Services.Localizer;

    protected AppSettings Settings => Services.Store.Current;

    /// <summary>The resource key of <see cref="Help"/> for <paramref name="settings"/> (some explanations depend on other settings).</summary>
    protected virtual string? HelpKey(AppSettings settings) => null;

    /// <summary>Saves <paramref name="change"/>. If saving fails the row shows the stored value again; the app keeps running.</summary>
    protected void Save(Func<AppSettings, AppSettings> change)
    {
        try
        {
            Services.Store.Update(change);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Services.Logger.LogError(e, "Can't save the setting {Setting}", _labelKey);
            OnSettingsChanged(Settings);
        }
    }

    /// <summary>
    /// Stops following the store and the UI language. For rows a section creates and drops at run time (one per library
    /// folder or game), so a dropped row isn't kept alive by the singletons it listens to.
    /// </summary>
    public void Detach()
    {
        Services.Store.Changed -= OnStoreChanged;
        Services.Localizer.CultureChanged -= OnLocalizerCultureChanged;
    }

    /// <summary>Re-reads the row's value from <paramref name="settings"/>. Base: the explanation.</summary>
    protected virtual void OnSettingsChanged(AppSettings settings) => OnPropertyChanged(nameof(Help));

    private void OnStoreChanged(object? sender, AppSettings settings) => OnSettingsChanged(settings);

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => OnCultureChanged();

    protected virtual void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(HelpLinkText));
    }
}
