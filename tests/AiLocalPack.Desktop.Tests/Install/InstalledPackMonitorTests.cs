using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>
/// The re-check of installed packs after a game update (task 33) on a real install: intact, files put back and the pack
/// re-applied, re-apply off, incompatible, re-apply failure, manifest gone, later library refreshes, the start-up step.
/// </summary>
public sealed class InstalledPackMonitorTests
{
    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    private static InstalledPackMonitor Monitor(InstallBed bed) => bed.Services.GetRequiredService<InstalledPackMonitor>();

    private static InstalledPackState? Installed(InstallBed bed) => bed.Services.GetRequiredService<GameStatuses>().Resolve(bed.Game).Installed;

    /// <summary>Installs "lanterns" and returns the original bytes of the translated <see cref="InstallBed.SystemFile"/>.</summary>
    private static async Task<byte[]> InstallAsync(InstallBed bed)
    {
        var original = File.ReadAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile));
        var outcome = await bed.Services.GetRequiredService<PackInstallService>().InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);
        outcome.Succeeded.Should().BeTrue();
        return original;
    }

    /// <summary>The start-up with settings saved in English: loading them applies the UI language to the whole test run.</summary>
    private static AppStartup Startup(InstallBed bed)
    {
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { UiLanguage = "en" });
        return bed.Services.GetRequiredService<AppStartup>();
    }

    /// <summary>What a Steam file check does: the original file back.</summary>
    private static void PutBack(InstallBed bed, byte[] original) =>
        File.WriteAllBytes(Path.Combine(bed.GameDir, InstallBed.SystemFile), original);

    [Fact]
    public async Task Intact_pack_is_left_alone()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAsync(bed);
        var installedAt = bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!.InstalledAt;

        // Act
        var results = await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (results.Single().Decision, bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!.InstalledAt)
            .Should().Be((PackRecheckDecision.Intact, installedAt));
    }

    [Fact]
    public async Task Files_put_back_by_the_store_get_the_pack_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));

        // Act
        var results = await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (results.Single().Reapplied, bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal), Installed(bed)!.Problem)
            .Should().Be((true, true, InstalledPackProblem.None));
    }

    [Fact]
    public async Task Reapply_after_an_update_backs_up_the_new_original()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        var before = bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!.Backup;

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!.Backup.Should().NotBe(before);
    }

    [Fact]
    public async Task With_reapply_off_the_pack_waits_for_the_player()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        bed.Services.GetRequiredService<SettingsStore>().Update(s => s with { ReapplyAfterGameUpdates = false });

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (Installed(bed)!.Problem, bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal)).Should().Be((InstalledPackProblem.Wiped, false));
    }

    [Fact]
    public async Task Update_that_removes_a_touched_file_makes_the_pack_incompatible()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAsync(bed);
        File.Delete(Path.Combine(bed.GameDir, InstallBed.SystemFile));

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (bed.Services.GetRequiredService<GameStatuses>().Resolve(bed.Game).Kind, Installed(bed)!.Problem)
            .Should().Be((LocalizationStatusKind.NeedsUpdate, InstalledPackProblem.Incompatible));
    }

    [Fact]
    public async Task Failed_reapply_needs_a_decision_and_keeps_the_failure()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        (Installed(bed)!.Problem, Monitor(bed).Find(InstallBed.Alpha)?.Failure?.Error?.Transaction?.Problem)
            .Should().Be((InstalledPackProblem.ReapplyFailed, (InstallProblem?)InstallProblem.GameRunning));
    }

    [Fact]
    public async Task Successful_reapply_by_hand_clears_the_failure()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        var processes = bed.Services.GetRequiredService<FakeProcesses>();
        processes.Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);
        processes.ExitAll();

        // Act
        await bed.Services.GetRequiredService<PackInstallService>().ReapplyAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (Installed(bed)!.Problem, Monitor(bed).Find(InstallBed.Alpha)).Should().Be((InstalledPackProblem.None, (InstalledPackHealth?)null));
    }

    [Fact]
    public async Task Pack_whose_manifest_is_gone_waits_for_the_player()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        bed.Services.GetRequiredService<PackCache>().Clear();
        File.Delete(Path.Combine(bed.Registry.Path, "games/steam-10/ru/lanterns/manifest.json"));

        // Act
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        Installed(bed)!.Problem.Should().Be(InstalledPackProblem.Wiped);
    }

    [Fact]
    public async Task Unavailable_game_is_not_checked()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAsync(bed);
        var catalog = bed.Services.GetRequiredService<GameLibrary>().Catalog;
        catalog.Update(InstallBed.Alpha, g => g with { IsAvailable = false });

        // Act
        var results = await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Later_library_refresh_checks_again()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var original = await InstallAsync(bed);
        await Monitor(bed).CheckAllAsync(TestContext.Current.CancellationToken);
        PutBack(bed, original);

        // Act
        await bed.Services.GetRequiredService<GameLibrary>().RefreshAsync();
        await Wait.UntilAsync(() => bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal)
            && bed.Services.GetRequiredService<PackInstallService>().Current is null);

        // Assert
        Installed(bed)!.Problem.Should().Be(InstalledPackProblem.None);
    }

    [Fact]
    public async Task Startup_step_names_the_reapplied_game()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        PutBack(bed, await InstallAsync(bed));
        var startup = Startup(bed);
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        var step = startup.Progress.Find(AppStartup.PacksStep)!;
        await Wait.UntilAsync(() => step.IsFinished);

        // Assert
        (step.IsOk, step.Detail).Should().Be((true, "Re-applied after a game update: Alpha"));
    }

    [Fact]
    public async Task Startup_step_warns_about_a_broken_pack()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        await InstallAsync(bed);
        File.Delete(Path.Combine(bed.GameDir, InstallBed.SystemFile));
        var startup = Startup(bed);
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        var step = startup.Progress.Find(AppStartup.PacksStep)!;
        await Wait.UntilAsync(() => step.IsFinished);

        // Assert
        (step.IsWarn, step.Detail).Should().Be((true, "A game update broke the translation: Alpha"));
    }

    [Fact]
    public async Task Startup_step_is_skipped_without_installed_packs()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var startup = Startup(bed);
        await startup.RunBlockingAsync();

        // Act
        startup.StartBackground();
        var step = startup.Progress.Find(AppStartup.PacksStep)!;
        await Wait.UntilAsync(() => step.IsFinished);

        // Assert
        (step.IsSkipped, step.Detail).Should().Be((true, "No installed translations to check"));
    }
}
