using Avalonia;
using Avalonia.Markup.Xaml;

namespace AiLocalPack.Desktop.Localization;

/// <summary>
/// <c>Text="{l:Loc Library_Title}"</c>: a UI string from resources that updates when the UI language switches.
/// Keys are the names in <c>Resources/Strings.resx</c>.
/// </summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Current.Observe(Key).ToBinding();
}
