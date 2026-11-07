using AiLocalPack.Core.Install;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>Whitelisted dependencies inside the install transaction (task 32): installed with the pack, shared, removed with the last user.</summary>
public sealed class PackInstallerDependencyTests : IDisposable
{
    private readonly InstallFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InstallRequest WithLoader(string packId = "full", string version = "5.4.23.2", IReadOnlyList<string>? generated = null) =>
        _fx.PackWith(packId, [_fx.Loader(version, generated)], ("data/Map001.json", $"map 1 {packId}"));

    [Fact]
    public void Install_writes_the_dependency_files_with_the_pack()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        installer.Install(WithLoader(), null, cancellationToken: Ct);

        // Assert
        new[] { _fx.Read("winhttp.dll"), _fx.Read("BepInEx/core/BepInEx.dll"), _fx.Read("data/Map001.json") }
            .Should().Equal("loader 5.4.23.2", "core 5.4.23.2", "map 1 full");
    }

    [Fact]
    public void Install_records_the_dependency_used_by_the_pack()
    {
        // Arrange
        var installer = _fx.Installer();

        // Act
        installer.Install(WithLoader(), null, cancellationToken: Ct);

        // Assert
        new InstalledPackStore(_fx.Paths).DependenciesOf(InstallFixture.Id).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = "bepinex", Version = "5.4.23.2", UsedBy = new[] { "full" }, AddedFolders = new[] { "BepInEx", "BepInEx/core" } });
    }

    [Fact]
    public void Uninstall_removes_the_dependency_no_other_pack_uses()
    {
        // Arrange
        var before = _fx.Snapshot();
        var installer = _fx.Installer();
        installer.Install(WithLoader(), null, cancellationToken: Ct);

        // Act
        var result = installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        (_fx.Snapshot(), result.RemovedDependencies.Select(d => d.Id), new InstalledPackStore(_fx.Paths).DependenciesOf(InstallFixture.Id))
            .Should().BeEquivalentTo((before, new[] { "bepinex" }, Array.Empty<InstalledDependency>()));
    }

    [Fact]
    public void Uninstall_deletes_what_the_dependency_made_while_the_game_ran()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader(generated: ["BepInEx/LogOutput.log", "BepInEx/cache/*", "BepInEx/cache/", "BepInEx/plugins/"]), null, cancellationToken: Ct);
        _fx.GameFolder.File("BepInEx/LogOutput.log", "log").File("BepInEx/cache/a.dat", "a").File("BepInEx/cache/b.dat", "b").Dir("BepInEx/plugins");

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Exists("BepInEx").Should().BeFalse();
    }

    [Fact]
    public void Uninstall_keeps_a_file_the_player_put_into_the_dependency_folders()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader(generated: ["BepInEx/plugins/"]), null, cancellationToken: Ct);
        _fx.GameFolder.File("BepInEx/plugins/MyMod.dll", "the player's mod");

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        (_fx.Read("BepInEx/plugins/MyMod.dll"), _fx.Exists("BepInEx/core")).Should().Be(("the player's mod", false));
    }

    [Fact]
    public void Generated_file_there_before_the_install_is_left_on_uninstall()
    {
        // Arrange
        _fx.GameFolder.File("BepInEx/LogOutput.log", "the player's own BepInEx log");
        var installer = _fx.Installer();
        installer.Install(WithLoader(generated: ["BepInEx/LogOutput.log"]), null, cancellationToken: Ct);

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Read("BepInEx/LogOutput.log").Should().Be("the player's own BepInEx log");
    }

    [Fact]
    public void Replacement_keeps_the_dependency_the_next_pack_shares()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader("first"), null, cancellationToken: Ct);
        installer.Uninstall(_fx.Game, cancellationToken: Ct, keep: [new DependencyKey("bepinex", "5.4.23.2")]);

        // Act
        var result = installer.Install(WithLoader("second"), null, cancellationToken: Ct);

        // Assert
        (result.SharedDependencies, result.Dependencies.Single().UsedBy, _fx.Read("winhttp.dll"))
            .Should().BeEquivalentTo((new[] { "bepinex" }, new[] { "second" }, "loader 5.4.23.2"));
    }

    [Fact]
    public void Shared_dependency_may_be_named_without_its_files()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader("first"), null, cancellationToken: Ct);
        installer.Uninstall(_fx.Game, cancellationToken: Ct, keep: [new DependencyKey("bepinex", "5.4.23.2")]);

        // Act
        var result = installer.Install(_fx.PackWith("second", [new InstallDependency("bepinex", "5.4.23.2", [])], ("data/Map001.json", "map 1 second")), null, cancellationToken: Ct);

        // Assert
        (result.Succeeded, result.Dependencies.Single().UsedBy).Should().BeEquivalentTo((true, new[] { "second" }));
    }

    [Fact]
    public void Dependency_named_without_files_that_isnt_installed_is_refused()
    {
        // Arrange
        var request = _fx.PackWith("full", [new InstallDependency("bepinex", "5.4.23.2", [])], ("data/Map001.json", "map 1 ru"));

        // Act
        var act = () => _fx.Installer().Install(request, null, cancellationToken: Ct);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Shared_dependency_goes_with_its_last_user()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader("first"), null, cancellationToken: Ct);
        installer.Uninstall(_fx.Game, cancellationToken: Ct, keep: [new DependencyKey("bepinex", "5.4.23.2")]);
        installer.Install(WithLoader("second"), null, cancellationToken: Ct);

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        (_fx.Exists("winhttp.dll"), _fx.Exists("BepInEx"), new InstalledPackStore(_fx.Paths).DependenciesOf(InstallFixture.Id).Count)
            .Should().Be((false, false, 0));
    }

    [Fact]
    public void Dependency_kept_for_the_next_pack_keeps_its_backup()
    {
        // Arrange: winhttp.dll was the player's own, so its original sits in the first pack's backup
        _fx.GameFolder.File("winhttp.dll", "the player's dll");
        var installer = _fx.Installer();
        installer.Install(WithLoader("first"), null, cancellationToken: Ct);
        installer.Uninstall(_fx.Game, cancellationToken: Ct, keep: [new DependencyKey("bepinex", "5.4.23.2")]);
        installer.Install(WithLoader("second"), null, cancellationToken: Ct);

        // Act
        installer.Uninstall(_fx.Game, cancellationToken: Ct);

        // Assert
        _fx.Read("winhttp.dll").Should().Be("the player's dll");
    }

    [Fact]
    public void Unused_dependencies_are_removed()
    {
        // Arrange: a replacement whose next pack never came
        var installer = _fx.Installer();
        installer.Install(WithLoader("first"), null, cancellationToken: Ct);
        installer.Uninstall(_fx.Game, cancellationToken: Ct, keep: [new DependencyKey("bepinex", "5.4.23.2")]);

        // Act
        var result = installer.RemoveUnusedDependencies(_fx.Game, cancellationToken: Ct);

        // Assert
        (result.RemovedDependencies.Count, _fx.Exists("winhttp.dll"), Directory.Exists(Path.Combine(_fx.Paths.Backups, "steam-620"))).Should().Be((1, false, false));
    }

    [Fact]
    public void Another_version_installed_is_a_conflict()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader("first", "5.4.22"), null, cancellationToken: Ct);
        installer.Uninstall(_fx.Game, cancellationToken: Ct, keep: [new DependencyKey("bepinex", "5.4.22")]);
        var before = _fx.Snapshot();

        // Act
        var result = installer.Install(WithLoader("second", "5.4.23.2"), null, cancellationToken: Ct);

        // Assert
        (result.Failure?.Problem, _fx.Snapshot()).Should().BeEquivalentTo(((InstallProblem?)InstallProblem.DependencyConflict, before));
    }

    [Fact]
    public void Dependency_writing_a_file_of_the_pack_is_a_conflict()
    {
        // Arrange
        var request = _fx.PackWith("full", [_fx.Dependency("bepinex", "1", null, ("data/Map001.json", "loader's"))], ("data/Map001.json", "pack's"));

        // Act
        var result = _fx.Installer().Install(request, null, cancellationToken: Ct);

        // Assert
        (result.Failure?.Problem, result.Failure?.File).Should().Be((InstallProblem.DependencyConflict, "data/Map001.json"));
    }

    [Fact]
    public void Failure_rolls_back_the_dependency_files_too()
    {
        // Arrange
        var before = _fx.Snapshot();
        var installer = _fx.Installer();
        var writes = 0;
        installer.BeforeFile = (stage, _) =>
        {
            if (stage == InstallStage.Writing && ++writes == 3)
            {
                throw new IOException("Simulated failure.");
            }
        };

        // Act
        var result = installer.Install(WithLoader(), null, cancellationToken: Ct);

        // Assert
        (result.Succeeded, _fx.Snapshot(), new InstalledPackStore(_fx.Paths).DependenciesOf(InstallFixture.Id).Count).Should().BeEquivalentTo((false, before, 0));
    }

    [Fact]
    public void Interrupted_uninstall_with_a_dependency_is_finished_on_recovery()
    {
        // Arrange
        var before = _fx.Snapshot();
        _fx.Installer().Install(WithLoader(), null, cancellationToken: Ct);
        var crashing = _fx.Installer();
        crashing.RollBackOnFailure = false;
        crashing.BeforeFile = (stage, path) =>
        {
            if (stage == InstallStage.Restoring && path == "winhttp.dll")
            {
                throw new IOException("Simulated crash.");
            }
        };
        var act = () => crashing.Uninstall(_fx.Game, cancellationToken: Ct);
        act.Should().Throw<IOException>();

        // Act
        var recovery = _fx.Installer().Recover(Ct);

        // Assert
        (recovery.Outcome, _fx.Snapshot(), new InstalledPackStore(_fx.Paths).DependenciesOf(InstallFixture.Id).Count)
            .Should().BeEquivalentTo((RecoveryOutcome.UninstallFinished, before, 0));
    }

    [Fact]
    public void Reapply_writes_a_wiped_dependency_file_again()
    {
        // Arrange
        var installer = _fx.Installer();
        installer.Install(WithLoader(), null, cancellationToken: Ct);
        File.Delete(_fx.GameFolder.Combine("winhttp.dll"));

        // Act
        installer.Reapply(WithLoader(), null, cancellationToken: Ct);

        // Assert
        _fx.Read("winhttp.dll").Should().Be("loader 5.4.23.2");
    }

    [Fact]
    public void Store_reads_back_the_dependency_records()
    {
        // Arrange
        _fx.Installer().Install(WithLoader(generated: ["BepInEx/cache/*"]), null, cancellationToken: Ct);

        // Act
        var dependency = new InstalledPackStore(_fx.Paths).DependenciesOf(InstallFixture.Id).Single();

        // Assert
        (dependency.Files.Count, dependency.GeneratedFiles).Should().BeEquivalentTo((2, new[] { "BepInEx/cache/*" }));
    }
}
