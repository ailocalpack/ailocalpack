using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Packs;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>Whether an installed recipe pack was made for the files it replaced (task 56).</summary>
public sealed class PackFitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Old = new('a', 64);
    private static readonly string New = new('c', 64);

    private static PackManifest Manifest(params PackBuild[] builds) => new(
        "lanterns",
        GameId.Steam(10),
        "ru",
        "Фонарщики",
        [new PackAuthor("alice")],
        [new PackRevision(1, T0, "alice")],
        PackStatus.Alpha,
        null,
        builds,
        new PackRecipe("rpgmaker", "1.0.0"),
        new PackStringsFile(PackStringsFile.DefaultPath, new string('b', 64), 10),
        null,
        [],
        [],
        new PackCoverage(2, 2),
        T0,
        T0);

    private static PackBuild Build(string id, string itemsHash) =>
        new(id, new Dictionary<string, string> { ["data/Items.json"] = itemsHash, ["data/Map001.json"] = Old });

    private static InstalledPack Record(string? itemsOriginal) => new(
        GameId.Steam(10),
        "ru",
        "lanterns",
        1,
        T0,
        "/games/alpha",
        "backup",
        [new InstalledFile("data/Items.json", itemsOriginal, new string('d', 64)), new InstalledFile("data/Map001.json", Old, new string('e', 64))],
        []);

    [Fact]
    public void A_pack_whose_build_matches_the_replaced_files_fits()
    {
        // Act
        var other = PackFit.IsForOtherVersion(Record(Old), Manifest(Build("1000", Old)));

        // Assert
        other.Should().BeFalse();
    }

    [Fact]
    public void A_pack_installed_over_a_changed_file_is_for_another_version()
    {
        // Act
        var other = PackFit.IsForOtherVersion(Record(New), Manifest(Build("1000", Old)));

        // Assert
        other.Should().BeTrue();
    }

    [Fact]
    public void Any_build_matching_the_files_is_enough()
    {
        // Act
        var other = PackFit.IsForOtherVersion(Record(New), Manifest(Build("1000", Old), Build("1001", New)));

        // Assert
        other.Should().BeFalse();
    }

    [Fact]
    public void A_file_the_install_found_without_an_original_doesnt_match()
    {
        // Act
        var other = PackFit.IsForOtherVersion(Record(null), Manifest(Build("1000", Old)));

        // Assert
        other.Should().BeTrue();
    }

    [Fact]
    public void A_binary_patch_pack_is_never_for_another_version()
    {
        // Arrange
        var patch = Manifest(Build("1000", Old)) with
        {
            Recipe = null,
            Strings = null,
            Patch = new PackPatch(PackPatch.VcdiffFormat, new Uri("https://example.com/p.zip"), new string('f', 64), 10, new Dictionary<string, string>()),
        };

        // Act
        var other = PackFit.IsForOtherVersion(Record(New), patch);

        // Assert
        other.Should().BeFalse();
    }
}
