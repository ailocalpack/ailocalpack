using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.Settings.GitHub;

/// <summary>
/// Settings → GitHub → My published packs while there are none (task 59; task 62 fills the list from the PRs it tracks):
/// signed in, where publishing starts; signed out, that signing in shows them.
/// </summary>
public sealed class PublishedPacksRow : SettingRow
{
    private readonly GitHubAccountRow _account;

    public PublishedPacksRow(SettingRowServices services, GitHubAccountRow account)
        : base(services, nameof(Strings.Settings_PublishedPacks_Label), nameof(Strings.Settings_PublishedPacks_Keywords))
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        account.StateChanged += (_, _) => OnPropertyChanged(nameof(Text));
    }

    /// <summary>The empty state's text.</summary>
    public string Text => Localizer[_account.IsSignedIn ? nameof(Strings.Settings_PublishedPacks_None) : nameof(Strings.Settings_PublishedPacks_SignIn)];

    public override string? Help => null;

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(Text));
    }
}
