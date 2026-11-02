using AiLocalPack.Core.Settings;
using Avalonia;
using Avalonia.Styling;

namespace AiLocalPack.Desktop;

/// <summary>Switches the app between light, dark and following the OS.</summary>
public interface IAppTheming
{
    void Apply(AppTheme theme);
}

internal sealed class AvaloniaAppTheming : IAppTheming
{
    public void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Dark => ThemeVariant.Dark,
                AppTheme.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
        }
    }
}
