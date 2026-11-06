using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>The install flow (design §4a): no dialog vs. confirmation, done / failure toasts, Undo, Details, best pack, uninstall.</summary>
public sealed class InstallFlowTests
{
    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    private static InstallFlow Flow(InstallBed bed) => bed.Services.GetRequiredService<InstallFlow>();

    private static IDialogService Dialogs(InstallBed bed) => bed.Services.GetRequiredService<IDialogService>();

    private static ToastService Toasts(InstallBed bed) => bed.Services.GetRequiredService<ToastService>();

    private static InstalledPack? Installed(InstallBed bed) => bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha);

    [Fact]
    public async Task Verified_compatible_pack_installs_without_asking()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);

        // Assert
        (Dialogs(bed).Current, Installed(bed)?.PackId).Should().Be(((DialogViewModel?)null, "lanterns"));
    }

    [Fact]
    public async Task Done_toast_offers_play_and_undo()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Toasts(bed).Items.Count > 0);

        // Assert
        Toasts(bed).Items.Single().Should().BeEquivalentTo(new
        {
            Tone = ToastTone.Ok,
            Text = "Installed: «lanterns».",
            Actions = new[] { new { Text = "Play" }, new { Text = "Undo" } },
        });
    }

    [Fact]
    public async Task Undo_restores_the_original_files()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var before = bed.Read(InstallBed.SystemFile);
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Toasts(bed).Items.Count > 0);

        // Act
        Toasts(bed).Items.Single().Actions.Single(a => a.Text == "Undo").Command.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is null && !Flow(bed).IsBusy);

        // Assert
        bed.Read(InstallBed.SystemFile).Should().Be(before);
    }

    [Fact]
    public async Task Play_on_the_done_toast_launches_the_game_through_steam()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Toasts(bed).Items.Count > 0);
        var processes = bed.Services.GetRequiredService<FakeProcesses>();

        // Act
        Toasts(bed).Items.Single().Actions.Single(a => a.Text == "Play").Command.Execute(null);
        await Wait.UntilAsync(() => processes.OpenedUrls.Count > 0);

        // Assert
        processes.OpenedUrls.Should().Equal(new Uri("steam://rungameid/10"));
    }

    [Fact]
    public async Task Alpha_pack_asks_first()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", PackStatus.Alpha));

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));

        // Assert
        (Dialogs(bed).Current as InstallConfirmDialogViewModel)?.Lines.Select(l => l.Kind).Should().Equal(InstallRiskKind.Alpha);
    }

    [Fact]
    public async Task Nothing_is_installed_while_the_confirmation_is_open()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", PackStatus.Alpha));

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));

        // Assert
        (Installed(bed), Flow(bed).IsBusy).Should().Be(((InstalledPack?)null, false));
    }

    [Fact]
    public async Task Confirming_installs()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", PackStatus.Alpha));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        var dialog = (InstallConfirmDialogViewModel)Dialogs(bed).Current!;

        // Act
        dialog.ConfirmCommand.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);

        // Assert
        Installed(bed)!.PackId.Should().Be("lanterns");
    }

    [Fact]
    public async Task Cancelling_installs_nothing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", PackStatus.Alpha));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        var dialog = (InstallConfirmDialogViewModel)Dialogs(bed).Current!;

        // Act
        dialog.CancelCommand.Execute(null);

        // Assert
        (Dialogs(bed).Current, Installed(bed)).Should().Be(((DialogViewModel?)null, (InstalledPack?)null));
    }

    [Fact]
    public async Task Another_installed_pack_is_named_in_the_confirmation()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns").RecipePack("full"));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "full"));

        // Assert
        ((InstallConfirmDialogViewModel)Dialogs(bed).Current!).Lines.Single().Text.Should().Be("Replaces the installed «lanterns» rev 1.");
    }

    [Fact]
    public async Task Failure_toast_offers_retry_and_details()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));

        // Act
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Toasts(bed).Items.Count > 0);

        // Assert
        Toasts(bed).Items.Single().Should().BeEquivalentTo(new
        {
            Tone = ToastTone.Bad,
            Text = "Couldn't install; files restored.",
            Actions = new[] { new { Text = "Retry" }, new { Text = "Details" } },
        });
    }

    [Fact]
    public async Task Details_show_the_plain_reason()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Toasts(bed).Items.Count > 0);

        // Act
        Toasts(bed).Items.Single().Actions.Single(a => a.Text == "Details").Command.Execute(null);

        // Assert
        Dialogs(bed).Current.Should().BeOfType<InstallDetailsDialogViewModel>().Which.Should().BeEquivalentTo(new
        {
            Reason = "The game is running. Close it and retry.",
            RestoredText = "The game files are as they were before.",
            CanRetry = true,
        });
    }

    [Fact]
    public async Task Retry_after_the_game_closed_installs()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var processes = bed.Services.GetRequiredService<FakeProcesses>();
        processes.Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Toasts(bed).Items.Count > 0);
        processes.ExitAll();

        // Act
        Toasts(bed).Items.Single().Actions.Single(a => a.Text == "Retry").Command.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);

        // Assert
        Installed(bed)!.PackId.Should().Be("lanterns");
    }

    [Fact]
    public async Task Best_pack_skips_one_that_cannot_be_read()
    {
        // Arrange: «top» ranks first (likes) but has no manifest
        using var bed = await InstallBed.CreateAsync(b =>
        {
            b.Registry.Pack(10, "ru", "top", PackStatus.Verified, likes: 99);
            b.RecipePack("lanterns");
        });

        // Act
        await Flow(bed).InstallBestAsync(bed.Game, () => { });
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);

        // Assert
        Installed(bed)!.PackId.Should().Be("lanterns");
    }

    [Fact]
    public async Task Best_pack_without_any_installable_one_says_so()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.Registry.Pack(10, "ru", "top", PackStatus.Verified));
        var told = false;

        // Act
        await Flow(bed).InstallBestAsync(bed.Game, () => told = true);

        // Assert
        (told, Installed(bed)).Should().Be((true, (InstalledPack?)null));
    }

    [Fact]
    public async Task Uninstall_asks_first()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);

        // Act
        Flow(bed).Uninstall(bed.Game);

        // Assert
        (Dialogs(bed).Current as ConfirmDialogViewModel)?.Title.Should().Be("Uninstall «lanterns»?");
    }

    [Fact]
    public async Task Confirmed_uninstall_restores_the_files()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var before = bed.Read(InstallBed.SystemFile);
        await Flow(bed).InstallAsync(bed.Game, PackOf(bed, "lanterns"));
        await Wait.UntilAsync(() => Installed(bed) is not null && !Flow(bed).IsBusy);
        Flow(bed).Uninstall(bed.Game);

        // Act
        ((ConfirmDialogViewModel)Dialogs(bed).Current!).ConfirmCommand.Execute(null);
        await Wait.UntilAsync(() => Installed(bed) is null && !Flow(bed).IsBusy);

        // Assert
        bed.Read(InstallBed.SystemFile).Should().Be(before);
    }
}
