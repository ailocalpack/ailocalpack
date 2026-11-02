using AiLocalPack.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>One option of a <see cref="ChoiceSettingRow"/>: a stored value and its text in the UI language.</summary>
public sealed class ChoiceOption(object? value, Func<string> display) : ObservableObject
{
    /// <summary>The value stored in settings (<c>null</c> = "System").</summary>
    public object? Value { get; } = value;

    public string Display => display();

    internal void Refresh() => OnPropertyChanged(nameof(Display));

    public override string ToString() => Display;
}

/// <summary>A setting with one value out of a list. The options stay the same objects across language switches, so the selection is kept.</summary>
public abstract class ChoiceSettingRow : SettingRow
{
    private readonly string? _helpKey;
    private readonly Func<AppSettings, object?> _read;
    private readonly Func<AppSettings, object?, AppSettings> _write;
    private ChoiceOption? _selectedOption;

    protected ChoiceSettingRow(
        SettingRowServices services,
        string labelKey,
        string? helpKey,
        string? keywordsKey,
        IReadOnlyList<ChoiceOption> options,
        Func<AppSettings, object?> read,
        Func<AppSettings, object?, AppSettings> write)
        : base(services, labelKey, keywordsKey)
    {
        ArgumentOutOfRangeException.ThrowIfZero(options.Count);
        _helpKey = helpKey;
        _read = read;
        _write = write;
        Options = options;
        _selectedOption = Match(read(Settings));
    }

    public IReadOnlyList<ChoiceOption> Options { get; }

    public ChoiceOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            // Controls push null while their items are being rebuilt; that's not a choice.
            if (value is null || !SetProperty(ref _selectedOption, value) || Equals(_read(Settings), value.Value))
            {
                return;
            }

            Save(s => _write(s, value.Value));
        }
    }

    protected override string? HelpKey(AppSettings settings) => _helpKey;

    protected override void OnSettingsChanged(AppSettings settings)
    {
        SetProperty(ref _selectedOption, Match(_read(settings)), nameof(SelectedOption));
        base.OnSettingsChanged(settings);
    }

    protected override void OnCultureChanged()
    {
        foreach (var option in Options)
        {
            option.Refresh();
        }

        base.OnCultureChanged();
    }

    /// <summary>The option for a stored value; the first option (System) for a value not in the list.</summary>
    private ChoiceOption Match(object? value) => Options.FirstOrDefault(o => Equals(o.Value, value)) ?? Options[0];
}

/// <summary>A choice shown as a drop-down list (languages).</summary>
public sealed class DropdownSettingRow(
    SettingRowServices services,
    string labelKey,
    string? helpKey,
    string? keywordsKey,
    IReadOnlyList<ChoiceOption> options,
    Func<AppSettings, object?> read,
    Func<AppSettings, object?, AppSettings> write,
    string? helpLinkTextKey = null,
    Uri? helpLinkUri = null)
    : ChoiceSettingRow(services, labelKey, helpKey, keywordsKey, options, read, write)
{
    public override string? HelpLinkText => helpLinkTextKey is null ? null : Localizer[helpLinkTextKey];

    public override Uri? HelpLinkUri => helpLinkUri;
}

/// <summary>A choice shown as a segmented control (theme): every option visible at once.</summary>
public sealed class SegmentedSettingRow(
    SettingRowServices services,
    string labelKey,
    string? helpKey,
    string? keywordsKey,
    IReadOnlyList<ChoiceOption> options,
    Func<AppSettings, object?> read,
    Func<AppSettings, object?, AppSettings> write)
    : ChoiceSettingRow(services, labelKey, helpKey, keywordsKey, options, read, write);
