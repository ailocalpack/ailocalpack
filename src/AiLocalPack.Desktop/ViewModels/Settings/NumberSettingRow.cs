using AiLocalPack.Core.Settings;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>A whole number within <see cref="Minimum"/>–<see cref="Maximum"/> (a number box with − / + buttons).</summary>
public sealed class NumberSettingRow : SettingRow
{
    private readonly string? _helpKey;
    private readonly Func<AppSettings, int> _read;
    private readonly Func<AppSettings, int, AppSettings> _write;
    private decimal? _value;

    public NumberSettingRow(
        SettingRowServices services,
        string labelKey,
        string? helpKey,
        string? keywordsKey,
        int minimum,
        int maximum,
        Func<AppSettings, int> read,
        Func<AppSettings, int, AppSettings> write)
        : base(services, labelKey, keywordsKey)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimum, maximum);
        _helpKey = helpKey;
        _read = read;
        _write = write;
        Minimum = minimum;
        Maximum = maximum;
        _value = read(Settings);
    }

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    /// <summary>
    /// The number shown; setting one stores it within the range. An emptied box (<c>null</c>) stores nothing and shows
    /// the stored value again.
    /// </summary>
    public decimal? Value
    {
        get => _value;
        set
        {
            if (value is not { } number)
            {
                // The box was emptied: show the stored value again.
                _value = _read(Settings);
                OnPropertyChanged();
                return;
            }

            var next = (int)Math.Clamp(Math.Round(number), Minimum, Maximum);
            SetProperty(ref _value, next);
            if (_read(Settings) != next)
            {
                Save(s => _write(s, next));
            }
        }
    }

    protected override string? HelpKey(AppSettings settings) => _helpKey;

    protected override void OnSettingsChanged(AppSettings settings)
    {
        SetProperty(ref _value, _read(settings), nameof(Value));
        base.OnSettingsChanged(settings);
    }
}
