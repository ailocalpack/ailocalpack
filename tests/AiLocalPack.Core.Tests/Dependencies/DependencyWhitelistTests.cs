using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Dependencies;

public sealed class DependencyWhitelistTests
{
    private static readonly PackDependency BepInEx = new("bepinex", "5.4.23.2");

    private static string Policy => File.ReadAllText(Path.Combine(RegistryFixtures.Folder, "policy", "dependencies.json"));

    private static DependencyWhitelist Sample => DependencyWhitelist.Parse(Policy).Whitelist!;

    private static string Entry(string asset = """{"platform": "windows-x64", "url": "https://example.org/a.zip", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000"}""", string generated = "[]") =>
        $$"""{"schemaVersion": 1, "dependencies": [{"id": "loader", "name": "Loader", "homepage": "https://example.org", "generated": {{generated}}, "versions": [{"version": "1.0", "assets": [{{asset}}]}]}]}""";

    [Fact]
    public void Sample_policy_lists_bepinex_for_five_platforms()
    {
        // Act
        var (whitelist, errors) = DependencyWhitelist.Parse(Policy);

        // Assert
        (errors.Count, whitelist!.Find("bepinex")!.Find("5.4.23.2")!.Assets.Select(a => a.Platform.ToString()))
            .Should().BeEquivalentTo((0, new[] { "windows-x64", "windows-x86", "linux-x64", "linux-x86", "macos-x64" }));
    }

    [Fact]
    public void Http_release_address_is_refused()
    {
        // Act
        var (whitelist, errors) = DependencyWhitelist.Parse(Entry("""{"platform": "windows-x64", "url": "http://example.org/a.zip", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000"}"""));

        // Assert
        (whitelist, errors.Single().Field).Should().Be((null, "dependencies[0].versions[0].assets[0].url"));
    }

    [Fact]
    public void Bad_hash_is_refused()
    {
        // Act
        var (_, errors) = DependencyWhitelist.Parse(Entry("""{"platform": "windows-x64", "url": "https://example.org/a.zip", "size": 10, "sha256": "ABC"}"""));

        // Assert
        errors.Single().Field.Should().Be("dependencies[0].versions[0].assets[0].sha256");
    }

    [Fact]
    public void Unknown_platform_is_refused()
    {
        // Act
        var (_, errors) = DependencyWhitelist.Parse(Entry("""{"platform": "dos-x86", "url": "https://example.org/a.zip", "size": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000"}"""));

        // Assert
        errors.Single().Field.Should().Be("dependencies[0].versions[0].assets[0].platform");
    }

    [Theory]
    [InlineData("*.log")]
    [InlineData("../outside.log")]
    [InlineData("Bep*/LogOutput.log")]
    [InlineData("BepInEx/cache*/")]
    public void Generated_path_that_could_reach_the_games_own_files_is_refused(string path)
    {
        // Act
        var (_, errors) = DependencyWhitelist.Parse(Entry(generated: $"[\"{path}\"]"));

        // Assert
        errors.Single().Field.Should().Be("dependencies[0].generated[0]");
    }

    [Fact]
    public void Newer_policy_format_is_refused()
    {
        // Act
        var (_, errors) = DependencyWhitelist.Parse("""{"schemaVersion": 2, "dependencies": []}""");

        // Assert
        errors.Single().Message.Should().Contain("Update the app");
    }

    [Fact]
    public void Pick_takes_the_asset_of_the_game_build()
    {
        // Act
        var choice = Sample.Pick(BepInEx, OsKind.Windows, ["x86"], out _);

        // Assert
        choice!.Asset.FileName.Should().Be("BepInEx_win_x86_5.4.23.2.zip");
    }

    [Fact]
    public void Pick_falls_back_to_another_cpu_of_a_universal_binary()
    {
        // Act
        var choice = Sample.Pick(BepInEx, OsKind.MacOS, ["arm64", "x64"], out _);

        // Assert
        choice!.Asset.Platform.ToString().Should().Be("macos-x64");
    }

    [Fact]
    public void Pick_without_an_asset_for_the_platform_knows_the_dependency()
    {
        // Act
        var choice = Sample.Pick(BepInEx, OsKind.Linux, ["arm64"], out var known);

        // Assert
        (choice, known).Should().Be((null, true));
    }

    [Fact]
    public void Pick_of_an_unlisted_version_is_unknown()
    {
        // Act
        var choice = Sample.Pick(new PackDependency("bepinex", "6.0.0"), OsKind.Windows, ["x64"], out var known);

        // Assert
        (choice, known).Should().Be((null, false));
    }

    [Fact]
    public void Index_carries_the_whitelist_round_trip()
    {
        // Arrange
        var json = RegistryIndexSerializer.Write(RegistryFixtures.Index(RegistryFixtures.Pack()) with { Dependencies = Sample });

        // Act
        var read = RegistryIndexSerializer.Read(json);

        // Assert
        (read.Errors.Count, RegistryIndexSerializer.Write(read.Index!)).Should().Be((0, json));
    }

    [Fact]
    public void Index_with_the_whitelist_follows_the_schema()
    {
        // Arrange
        var json = RegistryIndexSerializer.Write(RegistryFixtures.Index(RegistryFixtures.Pack()) with { Dependencies = Sample });

        // Act
        var errors = RegistryFixtures.SchemaErrors(json);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Damaged_dependency_in_the_index_is_left_out_with_the_rest_kept()
    {
        // Arrange
        var json = RegistryFixtures.IndexJson(RegistryFixtures.Pack()).TrimEnd().TrimEnd('}')
            + """, "dependencies": [{"id": "Bad ID"}, {"id": "ok", "name": "OK", "homepage": "https://example.org", "versions": [{"version": "1", "assets": [{"platform": "linux-x64", "url": "https://example.org/a.zip", "size": 1, "sha256": "0000000000000000000000000000000000000000000000000000000000000000"}]}]}]}""";

        // Act
        var read = RegistryIndexSerializer.Read(json);

        // Assert
        (read.Index!.Dependencies.Dependencies.Select(d => d.Id), read.Index.PackCount, read.Errors.All(e => e.Field.StartsWith("dependencies[0]", StringComparison.Ordinal)))
            .Should().BeEquivalentTo((new[] { "ok" }, 1, true));
    }
}
