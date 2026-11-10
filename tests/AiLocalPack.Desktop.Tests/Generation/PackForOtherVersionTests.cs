using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>
/// A local pack installed over files a game update changed (probably compatible, task 56) over a real RPG Maker game:
/// it counts as made for another version, so the Generation tab offers the update form (the real <see cref="UpdateBases"/>
/// reading the local pack) instead of its own result.
/// </summary>
public sealed class PackForOtherVersionTests
{
    /// <summary>The bed with the generated pack installed, after <paramref name="update"/> changed the game's items when true.</summary>
    private static async Task<InstallBed> InstalledAsync(bool update)
    {
        var bed = await InstallBed.CreateAsync(configure: services => services.AddSingleton<IUpdateBases, UpdateBases>());
        var pack = bed.LocalPack();
        if (update)
        {
            var items = Path.Combine(bed.GameDir, "www", "data", "Items.json");
            await File.WriteAllTextAsync(
                items,
                (await File.ReadAllTextAsync(items, TestContext.Current.CancellationToken)).Replace("Restores 500 HP", "Restores 800 HP", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
        }

        await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);
        return bed;
    }

    private static GenerationTabViewModel Tab(InstallBed bed) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, GamePageTab.Generation).Generation;

    [Fact]
    public async Task A_pack_installed_over_the_files_it_was_made_for_fits()
    {
        // Arrange
        using var bed = await InstalledAsync(update: false);

        // Act
        var state = bed.Services.GetRequiredService<IInstalledPacks>().Find(InstallBed.Alpha, "ru");

        // Assert
        state!.ForOtherVersion.Should().BeFalse();
    }

    [Fact]
    public async Task A_pack_installed_over_changed_files_is_for_another_version()
    {
        // Arrange
        using var bed = await InstalledAsync(update: true);

        // Act
        var state = bed.Services.GetRequiredService<IInstalledPacks>().Find(InstallBed.Alpha, "ru");

        // Assert
        state!.ForOtherVersion.Should().BeTrue();
    }

    [Fact]
    public async Task The_installed_result_gives_way_to_the_update_form()
    {
        // Arrange
        using var bed = await InstalledAsync(update: true);

        // Act
        var tab = Tab(bed);

        // Assert
        new { tab.ShowForm, tab.ShowResult, tab.IsUpdateMode }.Should().Be(new { ShowForm = true, ShowResult = false, IsUpdateMode = true });
    }
}
