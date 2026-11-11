using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.GitHub;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GitHub;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.GitHub;

/// <summary>
/// Settings → GitHub (design §8; task 59): <em>Account</em> (<see cref="GitHubAccountRow"/> with the Device Flow inline)
/// and <em>My published packs</em> (task 62: the packs sent from this machine with their PR state). A missing account is
/// never a warning: no badge.
/// Shows the remembered account at once (preloading at start-up never reads the keychain or the network) and asks
/// GitHub each time the section opens.
/// </summary>
public sealed class GitHubSettingsSection : SettingsSection
{
    private readonly ILogger<GitHubSettingsSection> _logger;

    public GitHubSettingsSection(
        IGitHubHost host,
        ISystemShell shell,
        GitHubSignInViewModel signIn,
        PublishFlow publish,
        LocalPacks localPacks,
        GameLibrary library,
        SettingsStore store,
        Localizer localizer,
        IUiDispatcher ui,
        ILogger<GitHubSettingsSection> logger)
        : base(SettingsSectionId.GitHub, "IconGitHub", nameof(Strings.Settings_GitHub_Title), nameof(Strings.Settings_GitHub_Description), localizer)
    {
        _logger = logger;
        var services = new SettingRowServices(store, localizer, logger);
        Account = new GitHubAccountRow(services, host, shell, ui, signIn);
        PublishedPacks = new PublishedPacksRow(services, Account, publish, localPacks, library, ui);
        Groups =
        [
            new SettingsGroup(services, nameof(Strings.Settings_Group_GitHubAccount), [Account]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_PublishedPacks), [PublishedPacks]),
        ];
    }

    public GitHubAccountRow Account { get; }

    public PublishedPacksRow PublishedPacks { get; }

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    public override void OnShown()
    {
        PublishedPacks.Refresh();
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        try
        {
            await Account.CheckAsync();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Checking the GitHub sign-in failed");
        }
    }
}
