using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Desktop.ViewModels.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>
/// What the screens show around an install (design §4a): the header's installed block (progress, then the pack and its
/// ⋯ menu), the primary action, the Packs tab's row and buttons, the library card's badge.
/// </summary>
public sealed class InstallStateTests
{
    private static GamePageViewModel Page(InstallBed bed) =>
        bed.Services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(bed.Game, GamePageTab.Packs);

    private static PackInstallService Service(InstallBed bed) => bed.Services.GetRequiredService<PackInstallService>();

    private static InstalledPack? Installed(InstallBed bed) => bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha);

    /// <summary>Runs <paramref name="act"/> and takes <paramref name="snapshot"/> when the operation reaches <see cref="InstallStep.Write"/>.</summary>
    private static async Task<T> DuringWriteAsync<T>(InstallBed bed, Func<Task> act, Func<T> snapshot)
    {
        T? taken = default;
        var done = false;
        Service(bed).Changed += (_, _) =>
        {
            if (!done && Service(bed).Current is { Step: InstallStep.Write })
            {
                taken = snapshot();
                done = true;
            }
        };
        await act();
        await Wait.UntilAsync(() => !bed.Services.GetRequiredService<InstallFlow>().IsBusy);
        return taken!;
    }

    private static async Task InstallAsync(InstallBed bed, GamePageViewModel page)
    {
        page.PrimaryCommand.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is not null && !bed.Services.GetRequiredService<InstallFlow>().IsBusy);
    }

    [Fact]
    public async Task Header_block_shows_the_step_while_installing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var page = Page(bed);

        // Act
        var seen = await DuringWriteAsync(bed, () => InstallAsync(bed, page), () => new
        {
            page.ShowInstalled,
            page.IsOperating,
            page.ShowInstalledPack,
            page.OperationTitle,
            page.OperationStep,
            page.CanRunPrimary,
        });

        // Assert
        seen.Should().BeEquivalentTo(new
        {
            ShowInstalled = true,
            IsOperating = true,
            ShowInstalledPack = false,
            OperationTitle = "Installing «lanterns» · step 5 of 6",
            OperationStep = "Writing the new files",
            CanRunPrimary = false,
        });
    }

    [Fact]
    public async Task Header_shows_the_installed_pack_and_play_after_installing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var page = Page(bed);

        // Act
        await InstallAsync(bed, page);

        // Assert
        new { page.IsOperating, page.ShowInstalledPack, page.InstalledText, page.Action.Kind, page.CanChangeInstalled }.Should().BeEquivalentTo(new
        {
            IsOperating = false,
            ShowInstalledPack = true,
            InstalledText = "«lanterns» · alice · rev 1",
            Kind = GamePrimaryActionKind.Play,
            CanChangeInstalled = true,
        });
    }

    [Fact]
    public async Task Packs_row_shows_installing_and_other_installs_are_off()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns").RecipePack("full"));
        var page = Page(bed);
        await page.Packs.WhenIdle;
        var lanterns = page.Packs.Rows.Single(r => r.Pack.Id == "lanterns");
        var full = page.Packs.Rows.Single(r => r.Pack.Id == "full");

        // Act
        var seen = await DuringWriteAsync(bed, () => { lanterns.InstallCommand.Execute(null); return Task.CompletedTask; }, () => new
        {
            lanterns.IsInstalling,
            lanterns.InstallText,
            OtherCanInstall = full.CanInstall,
        });

        // Assert
        seen.Should().BeEquivalentTo(new { IsInstalling = true, InstallText = "Installing…", OtherCanInstall = false });
    }

    [Fact]
    public async Task Packs_row_of_the_installed_pack_offers_uninstall()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var page = Page(bed);

        // Act
        await InstallAsync(bed, page);

        // Assert
        var row = page.Packs.Rows.Single();
        new { row.IsInstalled, row.InstallText, row.ShowUninstall, row.CanInstall }.Should().BeEquivalentTo(new
        {
            IsInstalled = true,
            InstallText = "Installed",
            ShowUninstall = true,
            CanInstall = false,
        });
    }

    [Fact]
    public async Task Menu_uninstall_asks_before_restoring()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var page = Page(bed);
        await InstallAsync(bed, page);

        // Act
        page.UninstallCommand.Execute(null);

        // Assert
        bed.Services.GetRequiredService<IDialogService>().Current.Should().BeOfType<ConfirmDialogViewModel>();
    }

    [Fact]
    public async Task Show_in_packs_selects_the_installed_pack()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", likes: 9).RecipePack("full"));
        var page = Page(bed);
        await page.Packs.WhenIdle;
        page.Packs.Selected = page.Packs.Rows.Single(r => r.Pack.Id == "lanterns");
        page.Packs.Rows.Single(r => r.Pack.Id == "full").InstallCommand.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is not null && !bed.Services.GetRequiredService<InstallFlow>().IsBusy);
        page.SelectedTab = GamePageTab.Audio;

        // Act
        page.ShowInPacksCommand.Execute(null);

        // Assert
        (page.SelectedTab, page.Packs.Selected?.Pack.Id).Should().Be((GamePageTab.Packs, "full"));
    }

    [Fact]
    public async Task Card_badge_shows_install_progress()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var library = bed.Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();
        var card = library.Cards.Single(c => c.Name == "Alpha");

        // Act
        var seen = await DuringWriteAsync(bed, () => { card.QuickActionCommand.Execute(null); return Task.CompletedTask; }, () => new
        {
            card.IsInstalling,
            card.ShowProgress,
            card.HasQuickAction,
            Badge = card.BadgeText.StartsWith("Installing · ", StringComparison.Ordinal),
        });

        // Assert
        seen.Should().BeEquivalentTo(new { IsInstalling = true, ShowProgress = true, HasQuickAction = false, Badge = true });
    }

    [Fact]
    public async Task Card_shows_installed_after_the_quick_action()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var library = bed.Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();
        var card = library.Cards.Single(c => c.Name == "Alpha");

        // Act
        card.QuickActionCommand.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is not null && !bed.Services.GetRequiredService<InstallFlow>().IsBusy);

        // Assert
        (card.StatusKind, card.BadgeText, card.IsInstalling).Should().Be((LocalizationStatusKind.Installed, "Installed", false));
    }

    [Fact]
    public async Task Other_cards_quick_actions_are_off_while_installing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var library = bed.Services.GetRequiredService<IPageFactory>().Create<LibraryViewModel>();
        var alpha = library.Cards.Single(c => c.Name == "Alpha");
        var beta = library.Cards.Single(c => c.Name == "Beta");

        // Act
        var canRun = await DuringWriteAsync(bed, () => { alpha.QuickActionCommand.Execute(null); return Task.CompletedTask; }, () => beta.QuickActionCommand.CanExecute(null));

        // Assert
        canRun.Should().BeFalse();
    }

    [Fact]
    public async Task Alpha_pack_from_the_header_asks_with_its_risk()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", PackStatus.Alpha));
        var page = Page(bed);

        // Act
        page.PrimaryCommand.Execute(null);
        await Wait.UntilAsync(() => bed.Services.GetRequiredService<IDialogService>().Current is not null);

        // Assert
        bed.Services.GetRequiredService<IDialogService>().Current.Should().BeOfType<InstallConfirmDialogViewModel>()
            .Which.Lines.Single().Text.Should().Be("Alpha: not yet confirmed by other players.");
    }
}
