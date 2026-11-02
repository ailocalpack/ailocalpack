using System.ComponentModel;
using System.Globalization;
using System.Resources;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.Localization;

/// <summary>
/// UI strings in the current UI language, switchable at runtime. XAML uses it through <c>{l:Loc Key}</c>, code through
/// the generated <see cref="Strings"/> class (keys) or <see cref="Format"/> / <see cref="Plural"/>.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private const string IndexerName = "Item[]";
    private static readonly ResourceManager Resources = Strings.ResourceManager;

    private readonly bool _appliesToProcess;

    /// <param name="culture">Initial UI culture (already resolved, see <see cref="UiLanguages.Resolve"/>).</param>
    /// <param name="appliesToProcess">
    /// Whether switching also sets the process UI culture and <see cref="Strings.Culture"/>. Only the app's
    /// <see cref="Current"/> does; tests create isolated instances.
    /// </param>
    public Localizer(CultureInfo culture, bool appliesToProcess = false)
    {
        _appliesToProcess = appliesToProcess;
        Culture = culture;
        Apply();
    }

    /// <summary>The app-wide instance: used by XAML markup and registered in DI. Starts in the OS language.</summary>
    public static Localizer Current { get; } = new(UiLanguages.Resolve(null, UiLanguages.Os), appliesToProcess: true);

    public CultureInfo Culture { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after <see cref="Culture"/> changes; every UI string should be re-read.</summary>
    public event EventHandler? CultureChanged;

    /// <summary>The string for <paramref name="key"/>; English when the language lacks it; <c>[key]</c> when no language has it.</summary>
    public string this[string key] => Resources.GetString(key, Culture) ?? $"[{key}]";

    /// <summary>The English string for <paramref name="key"/> whatever the UI language (search matches English words too).</summary>
    public string English(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) ?? $"[{key}]";

    /// <summary>Switches the UI language: <paramref name="setting"/> (a language tag or <c>null</c> for "follow the OS"), see <see cref="UiLanguages.Resolve"/>.</summary>
    public void SetLanguage(string? setting) => SetCulture(UiLanguages.Resolve(setting, UiLanguages.Os));

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (culture.Equals(Culture))
        {
            return;
        }

        Culture = culture;
        Apply();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerName));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The string for <paramref name="key"/> with <c>{0}</c>… filled in; numbers and dates formatted for <see cref="Culture"/>.</summary>
    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    /// <summary>
    /// The plural form for <paramref name="count"/> (resources <c>Base_One</c>, <c>Base_Few</c>, ...), with <c>{0}</c> = the
    /// count with digit grouping for <see cref="Culture"/> (1,234 / 1 234) and <paramref name="args"/> as <c>{1}</c>…. <paramref name="key"/> is the
    /// base name or any form's name, so callers can pass <c>nameof(Strings.Base_One)</c>.
    /// </summary>
    public string Plural(string key, long count, params object?[] args)
    {
        key = PluralRules.BaseKey(key);
        var category = PluralRules.Select(Culture, count);
        var form = Resources.GetString($"{key}_{category}", Culture)
            ?? Resources.GetString($"{key}_{PluralCategory.Other}", Culture)
            ?? $"[{key}]";
        return string.Format(Culture, form, [count.ToString("N0", Culture), .. args]);
    }

    /// <summary>Observes <paramref name="key"/>: the current value now and again after every language switch.</summary>
    public IObservable<string> Observe(string key) => new KeyObservable(this, key);

    private void Apply()
    {
        if (_appliesToProcess)
        {
            Strings.Culture = Culture;
            CultureInfo.CurrentUICulture = Culture;
            CultureInfo.DefaultThreadCurrentUICulture = Culture;
        }
    }

    private sealed class KeyObservable(Localizer localizer, string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            void OnChanged(object? sender, EventArgs e) => observer.OnNext(localizer[key]);

            localizer.CultureChanged += OnChanged;
            observer.OnNext(localizer[key]);
            return new Unsubscriber(() => localizer.CultureChanged -= OnChanged);
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
