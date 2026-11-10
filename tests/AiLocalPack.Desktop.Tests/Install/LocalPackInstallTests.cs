using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>Packs generated on this machine install through the same service as registry packs (task 55).</summary>
public sealed class LocalPackInstallTests
{
    [Fact]
    public async Task Local_pack_rebuilds_the_game_files_from_its_strings()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();

        // Act
        await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        bed.Read(InstallBed.SystemFile).Should().Contain("RU ");
    }

    [Fact]
    public async Task Local_pack_is_recorded_local_with_its_title()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();

        // Act
        var outcome = await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Installed!.Local, outcome.Installed.Title, outcome.Installed.PackId).Should().Be((true, "Claude Code translation", "claude-code"));
    }

    [Fact]
    public async Task Local_pack_install_takes_no_download_step()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var steps = new List<InstallStep>();
        service.Changed += (_, _) =>
        {
            if (service.Current is { } operation)
            {
                steps.Add(operation.Step);
            }
        };

        // Act
        await service.InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        steps.Should().NotContain(InstallStep.Download);
    }

    [Fact]
    public async Task Local_pack_replaces_the_installed_registry_pack()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("full", prefix: "AB "));
        var pack = bed.LocalPack();
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "full"), TestContext.Current.CancellationToken);

        // Act
        await service.InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        (bed.Read(InstallBed.SystemFile).Contains("AB ", StringComparison.Ordinal), bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!.Local)
            .Should().Be((false, true));
    }

    [Fact]
    public async Task Installed_local_pack_reapplies_from_its_folder()
    {
        // Arrange: a game update put the original file back.
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        var original = bed.Read(InstallBed.SystemFile);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(bed.GameDir, InstallBed.SystemFile), original);

        // Act
        var outcome = await service.ReapplyAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error, outcome.Installed?.Local).Should().Be(((InstallError?)null, true));
    }

    [Fact]
    public async Task Reapply_of_a_deleted_local_pack_says_it_is_gone()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);
        bed.Services.GetRequiredService<LocalPackStore>().Delete(InstallBed.Alpha, "ru", "claude-code");

        // Act
        var outcome = await service.ReapplyAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        outcome.Error!.Kind.Should().Be(InstallErrorKind.LocalMissing);
    }

    [Fact]
    public async Task Install_of_a_deleted_local_pack_touches_nothing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        var before = bed.Read(InstallBed.SystemFile);
        bed.Services.GetRequiredService<LocalPackStore>().Delete(InstallBed.Alpha, "ru", "claude-code");

        // Act
        var outcome = await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error!.Kind, bed.Read(InstallBed.SystemFile)).Should().Be((InstallErrorKind.LocalMissing, before));
    }

    [Fact]
    public async Task Installed_local_pack_is_titled_from_the_record()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPack();
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Act
        var title = service.TitleOf(InstallBed.Alpha, bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha));

        // Assert
        title.Should().Be("Claude Code translation");
    }

    [Fact]
    public async Task Installed_local_pack_is_no_registry_pack_of_the_same_id()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("claude-code"));
        var pack = bed.LocalPack();
        await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Act
        var status = bed.Services.GetRequiredService<GameStatuses>().Resolve(bed.Game);

        // Assert
        (status.Installed!.Local, Core.Library.GamePrimaryActions.Resolve(status).Pack).Should().Be((true, (RegistryPack?)null));
    }

    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    [Fact]
    public async Task Local_patch_pack_applies_its_archive_from_the_local_releases_folder()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var pack = bed.LocalPatchPack();

        // Act
        var outcome = await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile).Contains(InstallBed.PatchedTitle, StringComparison.Ordinal)).Should().Be((true, true));
    }

    [Fact]
    public async Task Local_patch_pack_uninstalls_back_to_the_original_file()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var before = bed.Read(InstallBed.SystemFile);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallLocalAsync(bed.Game, bed.LocalPatchPack(), TestContext.Current.CancellationToken);

        // Act
        await service.UninstallAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        bed.Read(InstallBed.SystemFile).Should().Be(before);
    }

    [Fact]
    public async Task Local_patch_pack_without_its_archive_fails_without_touching_the_game()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync();
        var before = bed.Read(InstallBed.SystemFile);
        var pack = bed.LocalPatchPack();
        File.Delete(bed.Services.GetRequiredService<LocalPackStore>().PatchArchivePath(pack.Manifest)!);

        // Act
        var outcome = await bed.Services.GetRequiredService<PackInstallService>().InstallLocalAsync(bed.Game, pack, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile)).Should().Be((false, before));
    }
}
