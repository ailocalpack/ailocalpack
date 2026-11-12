using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Patches;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>Binary patch packs (task 34) through the install service: the delta applied on a copy, then the same transaction.</summary>
public sealed class PatchPackInstallTests
{
    private static RegistryPack PackOf(InstallBed bed, string id) =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, "ru").Single(p => p.Id == id);

    private static PackInstallService Service(InstallBed bed) => bed.Services.GetRequiredService<PackInstallService>();

    [Fact]
    public async Task Install_writes_the_patched_file()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.PatchPack("patch"));

        // Act
        var outcome = await Service(bed).InstallAsync(bed.Game, PackOf(bed, "patch"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile).Contains(InstallBed.PatchedTitle, StringComparison.Ordinal)).Should().Be((true, true));
    }

    [Fact]
    public async Task Install_applies_the_patch_in_place_of_the_rebuild_step()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.PatchPack("patch"));
        var service = Service(bed);
        var steps = new List<InstallStep>();
        service.Changed += (_, _) =>
        {
            if (service.Current is { } operation && (steps.Count == 0 || steps[^1] != operation.Step))
            {
                steps.Add(operation.Step);
            }
        };

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "patch"), TestContext.Current.CancellationToken);

        // Assert
        steps.Should().Equal(InstallStep.Download, InstallStep.Check, InstallStep.Patch, InstallStep.BackUp, InstallStep.Write, InstallStep.Verify);
    }

    [Fact]
    public async Task A_game_file_other_than_the_patch_source_makes_the_pack_incompatible_and_touches_nothing()
    {
        // Arrange: the game's build is listed, so only the patch's own source check sees the change.
        using var bed = await InstallBed.CreateAsync(b => b.PatchPack("patch"));
        File.AppendAllText(Path.Combine(bed.GameDir, InstallBed.SystemFile), " ");
        var before = bed.Read(InstallBed.SystemFile);

        // Act
        var outcome = await Service(bed).InstallAsync(bed.Game, PackOf(bed, "patch"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.Compatibility?.Reason, outcome.Error?.Compatibility?.ChangedFiles.Single(), bed.Read(InstallBed.SystemFile))
            .Should().Be((InstallErrorKind.Incompatible, PackCompatibilityReason.PatchFilesChanged, InstallBed.SystemFile, before));
    }

    [Fact]
    public async Task A_damaged_delta_fails_as_a_patch_error_and_touches_nothing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.PatchPack("patch", delta: [0xD6, 0xC3, 0xC4, 0x00, 0x00, 0x05]));
        var before = bed.Read(InstallBed.SystemFile);

        // Act
        var outcome = await Service(bed).InstallAsync(bed.Game, PackOf(bed, "patch"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.Patch, outcome.Error?.File, bed.Read(InstallBed.SystemFile))
            .Should().Be((InstallErrorKind.Patch, (PatchProblem?)PatchProblem.Decode, InstallBed.SystemFile, before));
    }

    [Fact]
    public async Task Reapply_patches_a_file_an_update_reverted()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.PatchPack("patch"));
        var original = bed.Read(InstallBed.SystemFile);
        await Service(bed).InstallAsync(bed.Game, PackOf(bed, "patch"), TestContext.Current.CancellationToken);
        var patched = bed.Read(InstallBed.SystemFile);
        File.WriteAllText(Path.Combine(bed.GameDir, InstallBed.SystemFile), original);

        // Act
        var outcome = await Service(bed).ReapplyAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile)).Should().Be((true, patched));
    }

    [Fact]
    public async Task Reapply_over_an_intact_patch_keeps_the_file()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.PatchPack("patch"));
        await Service(bed).InstallAsync(bed.Game, PackOf(bed, "patch"), TestContext.Current.CancellationToken);
        var patched = bed.Read(InstallBed.SystemFile);

        // Act
        var outcome = await Service(bed).ReapplyAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile)).Should().Be((true, patched));
    }
}
