using AiLocalPack.Core.Games;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Tests.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>The reference packs a new translation learns from (decided 2026-11-16), downloaded from the registry.</summary>
public sealed class ReferencePacksServiceTests
{
    /// <summary>Steam game 10 with a pack into <paramref name="language"/> whose string table translates key <c>k</c> as "Fire!".</summary>
    private static TestRegistry Registry(string language, PackStatus status)
    {
        var registry = new TestRegistry();
        registry.Pack(10, language, "full", status).Write();
        using var compressed = new MemoryStream();
        StringTableSerializer.Write(new StringTable(language, new Dictionary<string, string> { ["k"] = "Fire!" }), compressed);
        var bytes = compressed.ToArray();
        var manifest = TestRegistry.ManifestModel(10, language, "full", "1000", new Dictionary<string, string> { ["data/Map001.json"] = new('a', 64) })
            with { Strings = new PackStringsFile(PackStringsFile.DefaultPath, PackCache.Sha256Hex(bytes), bytes.LongLength) };
        registry.WriteFile($"games/steam-10/{language}/full/manifest.json", PackManifestSerializer.Write(manifest));
        var strings = Path.Combine(registry.Path, "games", "steam-10", language, "full", "strings", "strings.json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(strings)!);
        File.WriteAllBytes(strings, bytes);
        return registry;
    }

    private static Game Alpha(ServiceProvider services) =>
        services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10));

    [Fact]
    public async Task A_confirmed_pack_of_the_game_is_downloaded_as_a_reference()
    {
        // Arrange
        using var registry = Registry("en", PackStatus.Verified);
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"), registry: registry.Path);
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Act
        var references = await services.GetRequiredService<GenerationService>().LoadReferencesAsync(Alpha(services), "ru", TestContext.Current.CancellationToken);

        // Assert
        references.Select(r => (r.Manifest.Language, r.Strings?.Strings["k"])).Should().Equal(("en", "Fire!"));
    }

    [Fact]
    public async Task An_unconfirmed_pack_is_no_reference()
    {
        // Arrange
        using var registry = Registry("en", PackStatus.Alpha);
        using var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"), registry: registry.Path);
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Act
        var references = await services.GetRequiredService<GenerationService>().LoadReferencesAsync(Alpha(services), "ru", TestContext.Current.CancellationToken);

        // Assert
        references.Should().BeEmpty();
    }
}
