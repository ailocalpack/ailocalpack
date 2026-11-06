using System.Text;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Storage;
using AiLocalPack.Core.Tests.Registry;

namespace AiLocalPack.Core.Tests.Packs;

/// <summary>Where downloaded packs live (task 26): keys, complete entries, size.</summary>
public sealed class PackCacheTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly PackCache _cache;

    public PackCacheTests() => _cache = new PackCache(AppPaths.UnderHome(_home.Path));

    public void Dispose() => _home.Dispose();

    private static string FixtureText =>
        File.ReadAllText(Path.Combine(RegistryFixtures.Folder, "games", "steam-620", "ru", "full", "manifest.json"));

    private static PackManifest Manifest(string text) => PackManifestSerializer.Read(text).Value!;

    private static string Sha(string text) => PackCache.Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>Writes a complete entry the way the fetcher does: payload, then the manifest.</summary>
    private CachedPack Store(string manifestText)
    {
        var manifest = Manifest(manifestText);
        var folder = _cache.EntryFolder(manifest, Sha(manifestText));
        var strings = Path.Combine(folder, "strings", "strings.json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(strings)!);
        File.WriteAllBytes(strings, new byte[manifest.Strings!.Size]);
        File.WriteAllText(Path.Combine(folder, PackCache.ManifestFileName), manifestText);
        return new CachedPack(manifest, folder, Sha(manifestText));
    }

    [Fact]
    public void Manifests_of_the_same_revision_with_different_content_get_different_entries()
    {
        // Arrange
        var edited = FixtureText.Replace("\"verified\"", "\"alpha\"", StringComparison.Ordinal);

        // Act
        var folders = new[] { FixtureText, edited }.Select(t => _cache.EntryFolder(Manifest(t), Sha(t))).ToList();

        // Assert
        folders.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Same_pack_id_in_other_games_and_languages_gets_different_folders()
    {
        // Arrange
        var keys = new[]
        {
            (GameId.Steam(620), "ru"),
            (GameId.Steam(6200), "ru"),
            (GameId.Steam(620), "pt-BR"),
            (GameId.Steam(620), "pt-PT"),
            (GameId.Parse("fp:" + new string('a', 64)), "ru"),
        };

        // Act
        var folders = keys.Select(k => _cache.PackFolder(k.Item1, k.Item2, "full")).ToList();

        // Assert
        folders.Distinct().Should().HaveCount(keys.Length);
    }

    [Fact]
    public void Entry_folder_names_the_revision_and_the_manifest_hash()
    {
        // Arrange
        var manifest = Manifest(FixtureText);

        // Act
        var folder = _cache.EntryFolder(manifest, Sha(FixtureText));

        // Assert
        Path.GetFileName(folder).Should().Be($"r2-{Sha(FixtureText)}");
    }

    [Fact]
    public void Pack_id_that_is_not_a_slug_is_refused()
    {
        // Arrange
        var game = GameId.Steam(620);

        // Act
        var act = () => _cache.PackFolder(game, "ru", "../escape");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Complete_entry_is_found_by_revision()
    {
        // Arrange
        var stored = Store(FixtureText);

        // Act
        var found = _cache.Find(GameId.Steam(620), "ru", "full", 2);

        // Assert
        found?.Folder.Should().Be(stored.Folder);
    }

    [Fact]
    public void Entry_without_its_string_table_is_not_complete()
    {
        // Arrange
        var stored = Store(FixtureText);
        File.Delete(stored.StringsFile!);

        // Act
        var found = _cache.Find(GameId.Steam(620), "ru", "full", 2);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void Entry_whose_manifest_was_changed_on_disk_is_ignored()
    {
        // Arrange
        var stored = Store(FixtureText);
        File.WriteAllText(stored.ManifestFile, FixtureText.Replace("\"verified\"", "\"alpha\"", StringComparison.Ordinal));

        // Act
        var found = _cache.Find(Manifest(FixtureText), Encoding.UTF8.GetBytes(FixtureText));

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void Size_counts_every_cached_file()
    {
        // Arrange
        var stored = Store(FixtureText);

        // Act
        var size = _cache.Size();

        // Assert
        size.Should().Be(stored.Manifest.Strings!.Size + new FileInfo(stored.ManifestFile).Length);
    }

    [Fact]
    public void Clear_empties_the_cache()
    {
        // Arrange
        Store(FixtureText);

        // Act
        _cache.Clear();

        // Assert
        _cache.Size().Should().Be(0);
    }
}
