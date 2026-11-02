using AiLocalPack.Core.Settings;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>An on / off setting (toggle switch).</summary>
public sealed class ToggleSettingRow : SettingRow
{
    private readonly Func<AppSettings, string?> _helpKey;
    private readonly Func<AppSettings, bool> _read;
    private readonly Func<AppSettings, bool, AppSettings> _write;
    private bool _isOn;

    public ToggleSettingRow(
        SettingRowServices services,
        string labelKey,
        Func<AppSettings, string?> helpKey,
        string? keywordsKey,
        Func<AppSettings, bool> read,
        Func<AppSettings, bool, AppSettings> write)
        : base(services, labelKey, keywordsKey)
    {
        _helpKey = helpKey;
        _read = read;
        _write = write;
        _isOn = read(Settings);
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value) && _read(Settings) != value)
            {
                Save(s => _write(s, value));
            }
        }
    }

    protected override string? HelpKey(AppSettings settings) => _helpKey(settings);

    protected override void OnSettingsChanged(AppSettings settings)
    {
        SetProperty(ref _isOn, _read(settings), nameof(IsOn));
        base.OnSettingsChanged(settings);
    }
}
