using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class AppCacheTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private AppPaths Paths => AppPaths.UnderHome(_home.Path);

    private void FillCache() => _home
        .File("cache/registry/index.json", new byte[10])
        .File("cache/packs/steam-620/ru/full/r1-abc/manifest.json", new byte[20])
        .File("cache/fonts/noto-sans-jp/abc-NotoSansJP.otf", new byte[30])
        .File("cache/dependencies/bepinex/5.4/abc-BepInEx.zip", new byte[40])
        .File("cache/covers/620.jpg", new byte[50])
        .File("cache/file-hashes.json", new byte[3])
        .File("cache/anti-cheat-rules.json", new byte[2]);

    [Fact]
    public void Clear_deletes_the_downloads()
    {
        // Arrange
        FillCache();
        var cache = new AppCache(Paths);

        // Act
        var result = cache.Clear();

        // Assert
        (result, cache.Downloads.Any(Directory.Exists)).Should().Be((new CacheClearResult(false, 150, 0), false));
    }

    [Fact]
    public void Clear_keeps_the_hash_cache_and_the_rules_stamp()
    {
        // Arrange
        FillCache();
        var cache = new AppCache(Paths);

        // Act
        cache.Clear();

        // Assert
        (cache.Size(TestContext.Current.CancellationToken), File.Exists(Paths.FileHashes), File.Exists(Paths.AntiCheatRulesFile)).Should().Be((5L, true, true));
    }

    [Fact]
    public void Clear_while_an_install_runs_deletes_nothing()
    {
        // Arrange
        FillCache();
        Directory.CreateDirectory(Paths.Data);
        using var held = new FileStream(Paths.InstallLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var cache = new AppCache(Paths);

        // Act
        var result = cache.Clear();

        // Assert
        (result.Busy, cache.Size(TestContext.Current.CancellationToken)).Should().Be((true, 155L));
    }

    [Fact]
    public void Clear_of_an_empty_cache_frees_nothing()
    {
        // Arrange
        var cache = new AppCache(Paths);

        // Act
        var result = cache.Clear();

        // Assert
        result.Should().Be(new CacheClearResult(false, 0, 0));
    }

    [Fact]
    public void A_download_in_use_stays_and_the_rest_goes()
    {
        // Arrange
        FillCache();
        var cache = new AppCache(Paths);
        using var inUse = OperatingSystem.IsWindows()
            ? new FileStream(Path.Combine(Paths.Packs, "steam-620/ru/full/r1-abc/manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None)
            : null;

        // Act
        var result = cache.Clear();

        // Assert
        result.LeftBytes.Should().Be(OperatingSystem.IsWindows() ? 20 : 0);
    }
}
