using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Install;

public sealed class InstallRisksTests
{
    private static readonly GameId Id = GameId.Steam(620);
    private static readonly DateTimeOffset Date = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static RegistryPack Pack(PackStatus status = PackStatus.Verified, string id = "lanterns", string language = "ru") =>
        new(id, Id, language, "Lanterns", new PackAuthor("alice"), status, 1, Date, new PackCoverage(10, 10), ["1000"], 1,
            $"games/steam-620/{language}/{id}/manifest.json", 100, new RegistryRating(0, 0, 0));

    private static PackCompatibility Compatibility(PackCompatibilityVerdict verdict) => new(
        verdict,
        verdict == PackCompatibilityVerdict.Compatible ? PackCompatibilityReason.BuildListed : PackCompatibilityReason.FilesChanged,
        ["1000"],
        "1001",
        "1000",
        3,
        verdict == PackCompatibilityVerdict.Compatible ? [] : ["data/Map001.json"],
        []);

    private static PackManifest Manifest(params PackDependency[] dependencies) => new(
        "lanterns",
        Id,
        "ru",
        "Lanterns",
        [new PackAuthor("alice")],
        [new PackRevision(1, Date, "alice")],
        PackStatus.Verified,
        null,
        [new PackBuild("1000", new Dictionary<string, string>())],
        new PackRecipe("rpgmaker", "1.0.0"),
        new PackStringsFile(PackStringsFile.DefaultPath, new string('0', 64), 10),
        null,
        dependencies,
        [],
        new PackCoverage(10, 10),
        Date,
        Date);

    private static InstalledPack Installed(string packId = "old", string language = "ru", int revision = 3) =>
        new(Id, language, packId, revision, Date, "/games/x", "backup", [], []);

    [Fact]
    public void Verified_compatible_pack_without_dependencies_and_nothing_installed_has_no_risk()
    {
        // Act
        var risks = InstallRisks.For(Pack(), Manifest(), Compatibility(PackCompatibilityVerdict.Compatible), null);

        // Assert
        risks.Should().BeEmpty();
    }

    [Fact]
    public void Alpha_pack_is_a_risk()
    {
        // Act
        var risks = InstallRisks.For(Pack(PackStatus.Alpha), Manifest(), Compatibility(PackCompatibilityVerdict.Compatible), null);

        // Assert
        risks.Should().Equal(new InstallRisk(InstallRiskKind.Alpha));
    }

    [Fact]
    public void Probably_compatible_pack_is_a_risk_with_its_check()
    {
        // Arrange
        var compatibility = Compatibility(PackCompatibilityVerdict.ProbablyCompatible);

        // Act
        var risks = InstallRisks.For(Pack(), Manifest(), compatibility, null);

        // Assert
        risks.Should().Equal(new InstallRisk(InstallRiskKind.ProbablyCompatible, Compatibility: compatibility));
    }

    [Fact]
    public void Each_dependency_is_a_risk()
    {
        // Arrange
        var bepinex = new PackDependency("bepinex", "5.4.22");
        var xunity = new PackDependency("xunity-autotranslator", "5.3.0");

        // Act
        var risks = InstallRisks.For(Pack(), Manifest(bepinex, xunity), Compatibility(PackCompatibilityVerdict.Compatible), null);

        // Assert
        risks.Should().Equal(
            new InstallRisk(InstallRiskKind.Dependency, Dependency: bepinex),
            new InstallRisk(InstallRiskKind.Dependency, Dependency: xunity));
    }

    [Fact]
    public void Dependency_risk_carries_its_whitelist_name()
    {
        // Arrange
        var bepinex = new PackDependency("bepinex", "5.4.23.2");
        var whitelist = DependencyWhitelist.Parse(File.ReadAllText(Path.Combine(RegistryFixtures.Folder, "policy", "dependencies.json"))).Whitelist;

        // Act
        var risks = InstallRisks.For(Pack(), Manifest(bepinex), Compatibility(PackCompatibilityVerdict.Compatible), null, dependencies: whitelist);

        // Assert
        risks.Single().DependencyName.Should().Be("BepInEx");
    }

    [Fact]
    public void Another_installed_pack_is_replaced()
    {
        // Arrange
        var installed = Installed();

        // Act
        var risks = InstallRisks.For(Pack(), Manifest(), Compatibility(PackCompatibilityVerdict.Compatible), installed, "Old lanterns");

        // Assert
        risks.Should().Equal(new InstallRisk(InstallRiskKind.Replaces, Replaced: installed, ReplacedTitle: "Old lanterns"));
    }

    [Fact]
    public void Replaced_pack_without_a_title_is_named_by_its_id()
    {
        // Act
        var risks = InstallRisks.For(Pack(), Manifest(), Compatibility(PackCompatibilityVerdict.Compatible), Installed());

        // Assert
        risks.Single().ReplacedTitle.Should().Be("old");
    }

    [Fact]
    public void Same_pack_in_another_language_is_replaced()
    {
        // Act
        var risks = InstallRisks.For(Pack(), Manifest(), Compatibility(PackCompatibilityVerdict.Compatible), Installed("lanterns", "de"));

        // Assert
        risks.Should().ContainSingle(r => r.Kind == InstallRiskKind.Replaces);
    }

    [Fact]
    public void The_pack_itself_installed_is_not_a_replacement()
    {
        // Act
        var risks = InstallRisks.For(Pack(), Manifest(), Compatibility(PackCompatibilityVerdict.Compatible), Installed("lanterns"));

        // Assert
        risks.Should().BeEmpty();
    }

    [Fact]
    public void Unread_manifest_lists_no_dependency()
    {
        // Act
        var risks = InstallRisks.For(Pack(), null, Compatibility(PackCompatibilityVerdict.Compatible), null);

        // Assert
        risks.Should().BeEmpty();
    }

    [Fact]
    public void Every_risk_together_comes_in_order()
    {
        // Act
        var risks = InstallRisks.For(
            Pack(PackStatus.Alpha),
            Manifest(new PackDependency("bepinex", "5.4.22")),
            Compatibility(PackCompatibilityVerdict.ProbablyCompatible),
            Installed());

        // Assert
        risks.Select(r => r.Kind).Should().Equal(
            InstallRiskKind.Alpha, InstallRiskKind.ProbablyCompatible, InstallRiskKind.Dependency, InstallRiskKind.Replaces);
    }
}
