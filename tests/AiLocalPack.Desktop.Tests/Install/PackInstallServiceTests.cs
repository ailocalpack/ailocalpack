using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Install;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.Unity;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Install;

/// <summary>The install service (task 29) on a real RPG Maker game and a local registry: download, check, rebuild, transaction.</summary>
public sealed class PackInstallServiceTests
{
    private static RegistryPack PackOf(InstallBed bed, string id, string language = "ru") =>
        bed.Services.GetRequiredService<PackIndex>().Current!.Index!.PacksFor(InstallBed.Alpha, language).Single(p => p.Id == id);

    [Fact]
    public async Task Unity_pack_installs_its_translations_through_the_recipe()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("unity-text"), unity: true);
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "unity-text"), TestContext.Current.CancellationToken);

        // Assert
        new UnityRecipe().Extract(bed.GameDir, TestContext.Current.CancellationToken).Strings
            .Should().OnlyContain(s => s.Original.StartsWith("RU ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unity_pack_uninstalls_to_the_original_bytes()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("unity-text"), unity: true);
        var path = Path.Combine(bed.GameDir, "Game_Data/resources.assets");
        var before = File.ReadAllBytes(path);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "unity-text"), TestContext.Current.CancellationToken);

        // Act
        await service.UninstallAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        File.ReadAllBytes(path).Should().Equal(before);
    }

    [Fact]
    public async Task Install_writes_the_translated_files()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile).Contains("RU ", StringComparison.Ordinal)).Should().Be((true, true));
    }

    [Fact]
    public async Task Install_of_a_Renpy_pack_adds_translation_files_and_records_what_Renpy_compiles()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"), renPy: true);
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read("game/tl/ailocalpack_ru/script.rpy").Contains("    s \"RU Hi there! How was class?\"", StringComparison.Ordinal),
            bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)!.GeneratedFiles.Contains("game/tl/ailocalpack_ru/script.rpyc"))
            .Should().Be((true, true, true));
    }

    [Fact]
    public async Task Uninstall_of_a_Renpy_pack_leaves_the_game_folder_as_it_was()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"), renPy: true);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var before = Directory.GetFileSystemEntries(bed.GameDir, "*", SearchOption.AllDirectories).Order().ToList();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(bed.GameDir, "game/tl/ailocalpack_ru/script.rpyc"), "compiled by Ren'Py on start");

        // Act
        var outcome = await service.UninstallAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, Directory.GetFileSystemEntries(bed.GameDir, "*", SearchOption.AllDirectories).Order().ToList())
            .Should().BeEquivalentTo((true, before));
    }

    [Fact]
    public async Task An_rpgmaker_pack_is_refused_where_the_toolkit_leaves_rpg_maker_alone_and_touches_nothing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(
            b => b.RecipePack("lanterns"),
            services => services.AddSingleton(new RecipeTools(RecipeTools.BuiltinTools(), RecipeTools.DisabledOn(OsKind.MacOS))));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var before = bed.Read(InstallBed.SystemFile);

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error?.Compatibility?.Reason, bed.Read(InstallBed.SystemFile))
            .Should().Be(((PackCompatibilityReason?)PackCompatibilityReason.EngineUnsupported, before));
    }

    [Fact]
    public async Task Install_saves_the_record()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)
            .Should().BeEquivalentTo(new { Language = "ru", PackId = "lanterns", Revision = 1 });
    }

    [Fact]
    public async Task Install_reports_each_step_in_order()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var steps = new List<InstallStep>();
        service.Changed += (_, _) =>
        {
            if (service.Current is { } operation && (steps.Count == 0 || steps[^1] != operation.Step))
            {
                steps.Add(operation.Step);
            }
        };

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        steps.Should().Equal(InstallStep.Download, InstallStep.Check, InstallStep.Rebuild, InstallStep.BackUp, InstallStep.Write, InstallStep.Verify);
    }

    [Fact]
    public async Task Install_while_the_game_runs_fails_and_leaves_the_files()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var before = bed.Read(InstallBed.SystemFile);
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error?.Transaction?.Problem, outcome.Error?.FilesRestored, bed.Read(InstallBed.SystemFile)).Should().Be((InstallProblem.GameRunning, true, before));
    }

    [Fact]
    public async Task Install_failing_on_a_read_only_file_restores_the_game_and_records_nothing()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var file = Path.Combine(bed.GameDir, "www", "data", "Map001.json");
        var before = bed.Read(InstallBed.SystemFile);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var service = bed.Services.GetRequiredService<PackInstallService>();

        try
        {
            // Act
            var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

            // Assert
            (outcome.Error?.Transaction?.Problem, bed.Read(InstallBed.SystemFile), bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha))
                .Should().Be((InstallProblem.ReadOnly, before, (InstalledPack?)null));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Failure_log_names_the_reason()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        bed.Services.GetRequiredService<FakeProcesses>().Run(Path.Combine(PathNames.RealPath(bed.GameDir), "Game.exe"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        outcome.Log.Should().Contain(line => line.Contains("The game is running. Close it and retry.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_string_table_fails_the_download()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", writeStrings: false));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.Fetch).Should().Be((InstallErrorKind.Fetch, PackFetchProblem.FileMissing));
    }

    [Fact]
    public async Task Pack_for_changed_files_of_a_manual_build_is_refused_as_incompatible()
    {
        // Arrange: made for build 900 and its files; the game's file then changes
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns", buildId: "900"));
        File.Delete(Path.Combine(bed.GameDir, "www", "data", "Map001.json"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Error?.Kind, outcome.Error?.Compatibility?.Reason).Should().Be((InstallErrorKind.Incompatible, PackCompatibilityReason.FilesMissing));
    }

    [Fact]
    public async Task Uninstall_restores_the_original_files()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var before = bed.Read(InstallBed.SystemFile);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Act
        var outcome = await service.UninstallAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile)).Should().Be((true, before));
    }

    [Fact]
    public async Task Installing_another_pack_replaces_the_installed_one()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns").RecipePack("full"));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Act
        var outcome = await service.InstallAsync(bed.Game, PackOf(bed, "full"), TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Services.GetRequiredService<InstalledPackStore>().Find(InstallBed.Alpha)?.PackId).Should().Be((true, "full"));
    }

    [Fact]
    public async Task Replacing_translates_the_original_text_not_the_previous_translation()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns").RecipePack("full"));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "full"), TestContext.Current.CancellationToken);

        // Assert
        bed.Read(InstallBed.SystemFile).Should().NotContain("RU RU ");
    }

    [Fact]
    public async Task Reapply_writes_back_a_file_an_update_reverted()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var original = bed.Read(InstallBed.SystemFile);
        var service = bed.Services.GetRequiredService<PackInstallService>();
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);
        var translated = bed.Read(InstallBed.SystemFile);
        File.WriteAllText(Path.Combine(bed.GameDir, InstallBed.SystemFile), original);

        // Act
        var outcome = await service.ReapplyAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        (outcome.Succeeded, bed.Read(InstallBed.SystemFile)).Should().Be((true, translated));
    }

    [Fact]
    public async Task A_second_operation_while_one_runs_is_refused_as_busy()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var service = bed.Services.GetRequiredService<PackInstallService>();
        var first = service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Act
        var second = await service.UninstallAsync(bed.Game, TestContext.Current.CancellationToken);

        // Assert
        await first;
        second.Error?.Kind.Should().Be(InstallErrorKind.Busy);
    }

    [Fact]
    public async Task Nothing_runs_once_an_operation_ended()
    {
        // Arrange
        using var bed = await InstallBed.CreateAsync(b => b.RecipePack("lanterns"));
        var service = bed.Services.GetRequiredService<PackInstallService>();

        // Act
        await service.InstallAsync(bed.Game, PackOf(bed, "lanterns"), TestContext.Current.CancellationToken);

        // Assert
        service.Current.Should().BeNull();
    }
}
