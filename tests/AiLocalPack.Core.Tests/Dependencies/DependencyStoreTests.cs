using System.Security.Cryptography;
using AiLocalPack.Core.Dependencies;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Dependencies;

public sealed class DependencyStoreTests : IDisposable
{
    private const string Url = "https://github.com/example/loader/releases/download/v1/Loader_win_x64_1.0.zip";

    private static readonly byte[] Content = [.. "PK release archive bytes"u8];

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose()
    {
        _dir.Dispose();
        _http.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DependencyChoice Choice(byte[] expected)
    {
        var asset = new DependencyAsset(new DependencyPlatform(OsKind.Windows, "x64"), new Uri(Url), expected.Length, Convert.ToHexStringLower(SHA256.HashData(expected)));
        var version = new DependencyVersion("1.0", [asset]);
        return new DependencyChoice(new WhitelistedDependency("loader", "Loader", new Uri("https://example.org"), DependencyLayout.Root, [], [version]), version, asset);
    }

    private DependencyStore Store() => new(_dir.Path, _http.CreateClient(), options: new PackFetchOptions(TimeSpan.FromSeconds(5), Attempts: 1));

    [Fact]
    public async Task Release_file_is_downloaded_into_the_cache()
    {
        // Arrange
        _http.OnGet(Url, Content);

        // Act
        var result = await Store().GetAsync(Choice(Content), cancellationToken: Ct);

        // Assert
        (result.Problem, result.FromCache, File.ReadAllBytes(result.Path!)).Should().BeEquivalentTo((PackFetchProblem.None, false, Content));
    }

    [Fact]
    public async Task Cached_release_file_is_used_without_the_network()
    {
        // Arrange
        _http.OnGet(Url, Content);
        await Store().GetAsync(Choice(Content), cancellationToken: Ct);

        // Act
        var result = await Store().GetAsync(Choice(Content), cancellationToken: Ct);

        // Assert
        (result.FromCache, _http.Requests.Count).Should().Be((true, 1));
    }

    [Fact]
    public async Task File_not_matching_the_whitelist_hash_is_deleted()
    {
        // Arrange
        byte[] tampered = [.. "PK release archive BYTES"u8];
        _http.OnGet(Url, tampered);
        var store = Store();

        // Act
        var result = await store.GetAsync(Choice(Content), cancellationToken: Ct);

        // Assert
        (result.Problem, result.Path, Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories).Count()).Should().Be((PackFetchProblem.HashMismatch, null, 0));
    }

    [Fact]
    public async Task No_connection_may_pass()
    {
        // Arrange
        _http.On(HttpMethod.Get, Url, _ => throw new HttpRequestException("No route to host"));

        // Act
        var result = await Store().GetAsync(Choice(Content), cancellationToken: Ct);

        // Assert
        (result.Problem, result.MayPass).Should().Be((PackFetchProblem.Offline, true));
    }
}
