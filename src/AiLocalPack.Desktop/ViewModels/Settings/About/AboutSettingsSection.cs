using System.Runtime.InteropServices;
using AiLocalPack.Core;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.Startup;
using AiLocalPack.Desktop.Updates;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.About;

/// <summary>
/// Settings → About (design §8, task 67): the header (logo, version, channel · platform), <em>Updates</em>
/// (<see cref="AppUpdateRow"/> over <see cref="AppUpdater"/>), <em>Links</em> (source, registry, report a problem, DMCA
/// and takedowns), <em>Legal</em> (licenses with <c>Third-party notices</c>, the disclaimer). The section list shows the
/// quiet hint "update" while a downloaded update waits; nothing here is a problem to act on, so no badge.
/// </summary>
public sealed class AboutSettingsSection : SettingsSection
{
    public static readonly Uri SourceUri = VelopackUpdateSource.Repository;
    public static readonly Uri RegistryUri = new("https://github.com/ailocalpack/registry");
    public static readonly Uri ReportUri = new("https://github.com/ailocalpack/ailocalpack/issues/new");
    public static readonly Uri LegalUri = new("https://github.com/ailocalpack/registry/blob/master/LEGAL.md");

    private readonly AppUpdater _updater;

    public AboutSettingsSection(
        AppUpdater updater,
        AppUpdateRestart restart,
        ISystemShell shell,
        IDialogService dialogs,
        IUiDispatcher ui,
        TimeProvider time,
        SettingsStore store,
        Localizer localizer,
        ILogger<AboutSettingsSection> logger)
        : base(SettingsSectionId.About, "IconInfo", nameof(Strings.Settings_About_Title), nameof(Strings.Settings_About_Description), localizer)
    {
        ArgumentNullException.ThrowIfNull(restart);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(dialogs);
        _updater = updater;
        var services = new SettingRowServices(store, localizer, logger);
        void Open(Uri uri) => shell.OpenUrl(uri);

        AboutHeader = new AboutHeader($"{AppInfo.DisplayName} {updater.CurrentVersion ?? AppVersion.Current}", Meta);
        Updates = new AppUpdateRow(services, updater, ui, time, restart.Request, Open);
        Links =
        [
            new LinkSettingRow(services, nameof(Strings.Settings_About_Source), null, "ailocalpack/ailocalpack", "BrandGitHub", SourceUri, Open),
            new LinkSettingRow(services, nameof(Strings.Settings_About_Registry), null, "ailocalpack/registry", "IconBox", RegistryUri, Open),
            new LinkSettingRow(services, nameof(Strings.Settings_About_Report), nameof(Strings.Settings_About_Report_Help), null, "IconBug", ReportUri, Open),
            new LinkSettingRow(services, nameof(Strings.Settings_About_Dmca), nameof(Strings.Settings_About_Dmca_Help), null, "IconScale", LegalUri, Open),
        ];
        Licenses = new LicensesRow(services, () => dialogs.Show<ThirdPartyNoticesDialogViewModel>());
        Disclaimer = new InfoSettingRow(services, nameof(Strings.Settings_About_Disclaimer_Label), nameof(Strings.Settings_About_Disclaimer), nameof(Strings.Settings_About_Disclaimer_Keywords));
        Groups =
        [
            new SettingsGroup(services, nameof(Strings.Settings_Group_Updates), [Updates]),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Links), Links),
            new SettingsGroup(services, nameof(Strings.Settings_Group_Legal), [Licenses, Disclaimer]),
        ];

        updater.Changed += (_, _) => ui.Post(UpdateHint);
        UpdateHint();
    }

    public AboutHeader AboutHeader { get; }

    public override object? Header => AboutHeader;

    public AppUpdateRow Updates { get; }

    public IReadOnlyList<LinkSettingRow> Links { get; }

    public LicensesRow Licenses { get; }

    public InfoSettingRow Disclaimer { get; }

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>"Stable channel · macOS · arm64 · © AI Local Pack contributors".</summary>
    public string Meta()
    {
        var channel = _updater.Channel is { } name
            ? Localizer.Format(nameof(Strings.Settings_About_Channel), name)
            : Localizer[nameof(Strings.Settings_About_ChannelStable)];
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        var platform = _updater.Install switch
        {
            AppInstallKind.Development => Localizer.Format(nameof(Strings.Settings_About_Platform_Development), os),
            // Velopack calls every macOS .app portable: only the Windows portable release is worth naming.
            AppInstallKind.Portable when OperatingSystem.IsWindows() => Localizer.Format(nameof(Strings.Settings_About_Platform_Portable), os),
            _ when OperatingSystem.IsLinux() => "Linux (AppImage)",
            _ => os,
        };
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return Localizer.Format(nameof(Strings.Settings_About_Meta), channel, $"{platform} · {architecture}");
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        AboutHeader.Refresh();
        UpdateHint();
    }

    private void UpdateHint() =>
        Hint = _updater.Status == AppUpdateStatus.Ready ? Localizer[nameof(Strings.Settings_About_Hint_Update)] : null;
}
