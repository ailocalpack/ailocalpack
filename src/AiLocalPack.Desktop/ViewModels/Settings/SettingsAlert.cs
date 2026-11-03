using System.Windows.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings;

/// <summary>
/// A problem shown on top of its section (design §8): a tone card with a plain-language reason and the fixing button.
/// Each alert counts towards the section's badge.
/// </summary>
/// <param name="Title">What is wrong ("A Steam library isn't available").</param>
/// <param name="Text">Why it matters and what is not affected.</param>
/// <param name="ActionText">The fixing button ("Rescan"); <c>null</c> for none.</param>
/// <param name="Action">What the button does.</param>
/// <param name="IsError">Red (an error) instead of amber (a warning).</param>
public sealed record SettingsAlert(string Title, string Text, string? ActionText = null, ICommand? Action = null, bool IsError = false)
{
    public bool HasAction => ActionText is not null && Action is not null;
}
