using System.Security.Cryptography;
using System.Text;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class FileHashCacheTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Rewrites the file with same-length content and puts the old write time back: invisible to the cache.</summary>
    private static void ChangeQuietly(string path, string content)
    {
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, time);
    }

    [Fact]
    public void Hash_is_the_lowercase_sha256_of_the_content()
    {
        // Arrange
        _dir.File("game.exe", "game binary");
        var cache = new FileHashCache();

        // Act
        var hash = cache.Hash(_dir.Combine("game.exe"), Ct);

        // Assert
        hash.Should().Be(Sha256("game binary"));
    }

    [Fact]
    public void A_file_with_unchanged_size_and_time_is_not_read_again()
    {
        // Arrange
        _dir.File("game.exe", "version 1");
        var path = _dir.Combine("game.exe");
        var cache = new FileHashCache();
        cache.Hash(path, Ct);
        ChangeQuietly(path, "version 2");

        // Act
        var hash = cache.Hash(path, Ct);

        // Assert
        hash.Should().Be(Sha256("version 1"));
    }

    [Fact]
    public void A_file_with_a_new_size_is_hashed_again()
    {
        // Arrange
        _dir.File("game.exe", "version 1");
        var path = _dir.Combine("game.exe");
        var cache = new FileHashCache();
        cache.Hash(path, Ct);
        File.WriteAllText(path, "version 2, longer");

        // Act
        var hash = cache.Hash(path, Ct);

        // Assert
        hash.Should().Be(Sha256("version 2, longer"));
    }

    [Fact]
    public void A_file_with_a_new_write_time_is_hashed_again()
    {
        // Arrange
        _dir.File("game.exe", "version 1");
        var path = _dir.Combine("game.exe");
        var cache = new FileHashCache();
        cache.Hash(path, Ct);
        File.WriteAllText(path, "version 2");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        // Act
        var hash = cache.Hash(path, Ct);

        // Assert
        hash.Should().Be(Sha256("version 2"));
    }

    [Fact]
    public void Saved_hashes_are_used_by_a_later_cache()
    {
        // Arrange
        _dir.File("game.exe", "version 1");
        var path = _dir.Combine("game.exe");
        var file = _dir.Combine("cache/file-hashes.json");
        var first = FileHashCache.Load(file);
        first.Hash(path, Ct);
        first.Save();
        ChangeQuietly(path, "version 2");

        // Act
        var hash = FileHashCache.Load(file).Hash(path, Ct);

        // Assert
        hash.Should().Be(Sha256("version 1"));
    }

    [Fact]
    public void Save_drops_files_that_no_longer_exist()
    {
        // Arrange
        _dir.File("a.exe", "a").File("b.exe", "b");
        var file = _dir.Combine("file-hashes.json");
        var cache = FileHashCache.Load(file);
        cache.Hash(_dir.Combine("a.exe"), Ct);
        cache.Hash(_dir.Combine("b.exe"), Ct);
        File.Delete(_dir.Combine("b.exe"));

        // Act
        cache.Save();

        // Assert
        FileHashCache.Load(file).Count.Should().Be(1);
    }

    [Fact]
    public void A_corrupted_cache_file_starts_empty()
    {
        // Arrange
        _dir.File("file-hashes.json", "{ not json");

        // Act
        var cache = FileHashCache.Load(_dir.Combine("file-hashes.json"));

        // Assert
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void A_missing_file_throws_file_not_found()
    {
        // Arrange
        var cache = new FileHashCache();

        // Act
        var act = () => cache.Hash(_dir.Combine("missing.exe"), Ct);

        // Assert
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Cancelled_hashing_throws_and_remembers_nothing()
    {
        // Arrange
        _dir.File("big.pak", new byte[3 << 20]);
        var cache = new FileHashCache();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = () => cache.Hash(_dir.Combine("big.pak"), cts.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void A_save_into_an_unwritable_place_does_not_throw()
    {
        // Arrange
        _dir.File("game.exe", "x").File("blocker", "a file where the cache folder should be");
        var cache = FileHashCache.Load(_dir.Combine("blocker/file-hashes.json"));
        cache.Hash(_dir.Combine("game.exe"), Ct);

        // Act
        var act = cache.Save;

        // Assert
        act.Should().NotThrow();
    }
}
