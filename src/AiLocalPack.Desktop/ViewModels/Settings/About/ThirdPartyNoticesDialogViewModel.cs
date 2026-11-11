using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings.About;

/// <summary>The third-party notices the build generated (<c>build/ThirdPartyNotices.cs</c>), embedded in the app.</summary>
public static class ThirdPartyNoticesText
{
    public const string ResourceName = "AiLocalPack.ThirdPartyNotices.txt";

    /// <summary>The notices (English, as the licenses are); <c>null</c> when the build has none.</summary>
    public static string? Load()
    {
        using var stream = typeof(ThirdPartyNoticesText).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Settings → About → <c>Third-party notices</c>: the components the app includes and their licenses, in a scrollable box.</summary>
public sealed class ThirdPartyNoticesDialogViewModel : DialogViewModel
{
    public ThirdPartyNoticesDialogViewModel(IDialogService dialogs, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(localizer);
        Text = ThirdPartyNoticesText.Load() ?? localizer[nameof(Strings.Notices_Missing)];
        CloseCommand = new RelayCommand(() => dialogs.Close(this));
    }

    public string Text { get; }

    public IRelayCommand CloseCommand { get; }
}
