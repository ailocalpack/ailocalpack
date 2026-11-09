using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Install;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>The library card of a game whose last generation left a local pack (task 55).</summary>
public sealed class LibraryLocalPackTests
{
    private static GameCardViewModel Card(InstallBed bed) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>().Cards.Single(c => c.Game.Id == InstallBed.Alpha);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Waiting_local_pack_shows_your_pack_ready()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var card = Card(bed);

        // Assert
        (card.StatusKind, card.BadgeText, card.ContextLine, card.QuickActionText, card.IsQuickPrimary)
            .Should().Be((LocalizationStatusKind.PackAvailable, "Your pack ready", "Your pack · 100% of strings", "Install & play", true));
    }

    [Fact]
    public async Task Waiting_local_pack_counts_under_pack_available()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();

        // Act
        var library = bed.Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();

        // Assert
        library.FilterChips.Single(c => c.Filter == LibraryFilter.PackAvailable).Count.Should().Be(1);
    }

    [Fact]
    public async Task Registry_pack_installed_keeps_the_installed_status()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("full"));
        bed.LocalPack();
        await bed.Services.GetRequiredService<InstallFlow>().InstallAsync(bed.Game, bed.Services.GetRequiredService<Desktop.Registry.PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single());
        await Until(() => bed.Services.GetRequiredService<Core.Install.InstalledPackStore>().Find(InstallBed.Alpha) is not null);

        // Act
        var card = Card(bed);

        // Assert
        (card.StatusKind, card.LocalResult).Should().Be((LocalizationStatusKind.Installed, (GenerationResultState?)null));
    }

    [Fact]
    public async Task Quick_action_installs_the_local_pack_and_launches_the_game()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        bed.LocalPack();
        var card = Card(bed);

        // Act
        card.QuickActionCommand.Execute(null);

        // Assert
        await Until(() => bed.Services.GetRequiredService<FakeProcesses>().OpenedUrls.Count > 0);
        (bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal), bed.Services.GetRequiredService<FakeProcesses>().OpenedUrls.Count)
            .Should().Be((true, 1));
    }

    [Fact]
    public async Task Local_pack_installed_in_another_language_shows_installed()
    {
        // Arrange: the library translates into English, the pack is Russian.
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "en" });

        // Act
        var card = Card(bed);

        // Assert
        (card.StatusKind, card.BadgeText, card.ContextLine, card.HasQuickAction)
            .Should().Be((LocalizationStatusKind.Installed, "Installed", "Your pack · Русский", false));
    }
}
