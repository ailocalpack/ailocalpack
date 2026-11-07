using AiLocalPack.Core.Library;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.GamePage;

/// <summary>
/// The game page after a game update broke the installed pack (task 33): the amber installed block, the update banner
/// and its buttons, and the header's action for each finding of the re-check.
/// </summary>
public sealed class GamePageUpdateBannerTests
{
    private static RegistryPack PackOf(InstallBed bed) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single();

    /// <summary>Installs "lanterns", lets <paramref name="update"/> do what a game update does, re-checks, opens the page.</summary>
    private static async Task<GamePageViewModel> AfterUpdateAsync(InstallBed bed, Action<string> update)
    {
        (await bed.Services.GetRequiredService<PackInstallService>().InstallAsync(bed.Game, PackOf(bed), TestContext.Current.CancellationToken))
            .Succeeded.Should().BeTrue();
        update(Path.Combine(bed.GameDir, InstallBed.SystemFile));
        await bed.Services.GetRequiredService<InstalledPackMonitor>().CheckAllAsync(TestContext.Current.CancellationToken);
        return bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game);
    }

    private static async Task<InstallBed> BedAsync(bool autoReapply = true)
    {
        var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { ReapplyAfterGameUpdates = autoReapply });
        return bed;
    }

    /// <summary>Puts back the original file (read before the install).</summary>
    private static Action<string> PutBack(InstallBed bed)
    {
        var original = File.ReadAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile));
        return path => File.WriteAllBytes(path, original);
    }

    [Fact]
    public async Task Incompatible_pack_shows_the_banner_and_translate_new_strings()
    {
        // Arrange
        using var bed = await BedAsync();

        // Act
        var page = await AfterUpdateAsync(bed, File.Delete);

        // Assert
        (page.ShowUpdateBanner, page.UpdateBannerText, page.InstalledLabel, page.PrimaryText).Should().Be((
            true,
            "The game updated (build 1,000). The installed «lanterns» rev 1 is incompatible with it; some text may show in English.",
            "Installed · incompatible with the new version",
            "Translate new strings"));
    }

    [Fact]
    public async Task Pack_put_back_with_reapply_off_offers_reapply()
    {
        // Arrange
        using var bed = await BedAsync(autoReapply: false);

        // Act
        var page = await AfterUpdateAsync(bed, PutBack(bed));

        // Assert
        (page.UpdateBannerText, page.InstalledLabel, page.PrimaryText, page.PrimaryCaption, page.ShowBannerReapply).Should().Be((
            "The game update put back the original files: «lanterns» rev 1 still fits but isn't applied, so the game shows its original text.",
            "Installed · not applied after the game update",
            "Re-apply",
            "Writes «lanterns» rev 1 again from your files",
            false));
    }

    [Fact]
    public async Task Reapply_from_the_header_writes_the_pack_again()
    {
        // Arrange
        using var bed = await BedAsync(autoReapply: false);
        var page = await AfterUpdateAsync(bed, PutBack(bed));

        // Act
        page.PrimaryCommand.Execute(null);
        await Wait.UntilAsync(() => page.Status.Kind == LocalizationStatusKind.Installed);

        // Assert
        (bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal), page.ShowUpdateBanner).Should().Be((true, false));
    }

    [Fact]
    public async Task Failed_automatic_reapply_says_why_and_offers_details()
    {
        // Arrange
        using var bed = await BedAsync();
        var putBack = PutBack(bed);

        // Act
        var page = await AfterUpdateAsync(bed, path =>
        {
            putBack(path);
            bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        });

        // Assert
        (page.UpdateBannerText.StartsWith("The game update put back the original files, and re-applying «lanterns» rev 1 failed. ", StringComparison.Ordinal),
            page.ShowBannerDetails, page.PrimaryText).Should().Be((true, true, "Re-apply"));
    }

    [Fact]
    public async Task Pack_reapplied_automatically_shows_no_banner()
    {
        // Arrange
        using var bed = await BedAsync();

        // Act
        var page = await AfterUpdateAsync(bed, PutBack(bed));

        // Assert
        (page.ShowUpdateBanner, page.PrimaryText).Should().Be((false, "Play"));
    }
}
