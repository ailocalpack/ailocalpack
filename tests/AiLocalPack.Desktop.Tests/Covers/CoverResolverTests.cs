using System.Net;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Covers;

namespace AiLocalPack.Desktop.Tests.Covers;

public sealed class CoverResolverTests : IDisposable
{
    private const string NewCdn = "https://shared.steamstatic.com/store_item_assets/steam/apps/10/library_600x900.jpg";
    private const string OldCdn = "https://cdn.cloudflare.steamstatic.com/steam/apps/10/library_600x900.jpg";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _http = new();
    private readonly FakeClock _clock = new();
    private readonly AppPaths _paths;

    public CoverResolverTests() => _paths = AppPaths.UnderHome(_dir.Combine("app"));

    private string SteamRoot => _dir.Combine("Steam");

    public void Dispose()
    {
        _http.Dispose();
        _dir.Dispose();
    }

    private CoverResolver Create() =>
        new(() => [SteamRoot], _http.CreateClient(), _paths, _clock, new TestLogger<CoverResolver>());

    private static Task<string?> Resolve(CoverResolver resolver, uint appId = 10) =>
        resolver.ResolveAsync(TestGames.Steam(appId, "Alpha"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Finds_the_cover_in_the_current_steam_cache_layout()
    {
        // Arrange
        _dir.File("Steam/appcache/librarycache/10/library_600x900.jpg", Jpeg);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(_dir.Combine("Steam/appcache/librarycache/10/library_600x900.jpg"));
        _http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Finds_the_cover_in_a_hash_subfolder_of_the_current_layout()
    {
        // Arrange
        _dir.File("Steam/appcache/librarycache/10/0f3c9a/library_600x900.jpg", Jpeg);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(_dir.Combine("Steam/appcache/librarycache/10/0f3c9a/library_600x900.jpg"));
    }

    [Fact]
    public async Task Finds_the_cover_in_the_old_flat_steam_cache_layout()
    {
        // Arrange
        _dir.File("Steam/appcache/librarycache/10_library_600x900.jpg", Jpeg);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(_dir.Combine("Steam/appcache/librarycache/10_library_600x900.jpg"));
        _http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_an_empty_file_in_the_steam_cache()
    {
        // Arrange
        _dir.File("Steam/appcache/librarycache/10/library_600x900.jpg", []);
        _http.OnGet(NewCdn, Jpeg);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(Path.Combine(_paths.Covers, "steam-10.jpg"));
    }

    [Fact]
    public async Task Downloads_from_the_cdn_into_the_cache()
    {
        // Arrange
        _http.OnGet(NewCdn, Jpeg);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(Path.Combine(_paths.Covers, "steam-10.jpg"));
        File.ReadAllBytes(path!).Should().Equal(Jpeg);
    }

    [Fact]
    public async Task Tries_the_older_cdn_when_the_current_one_has_no_cover()
    {
        // Arrange
        _http.OnGet(NewCdn, HttpStatusCode.NotFound).OnGet(OldCdn, Jpeg);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        File.ReadAllBytes(path!).Should().Equal(Jpeg);
        _http.Requests.Select(r => r.Uri.ToString()).Should().Equal(NewCdn, OldCdn);
    }

    [Fact]
    public async Task Uses_the_downloaded_cover_without_asking_again()
    {
        // Arrange
        _http.OnGet(NewCdn, Jpeg);
        var resolver = Create();
        await Resolve(resolver);

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().NotBeNull();
        _http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Gives_no_cover_when_no_cdn_has_one()
    {
        // Arrange
        _http.OnGet(NewCdn, HttpStatusCode.NotFound).OnGet(OldCdn, HttpStatusCode.NotFound);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().BeNull();
    }

    [Fact]
    public async Task Remembers_a_cdn_miss_for_a_week()
    {
        // Arrange
        _http.OnGet(NewCdn, HttpStatusCode.NotFound).OnGet(OldCdn, HttpStatusCode.NotFound);
        var resolver = Create();
        await Resolve(resolver);
        _clock.Advance(TimeSpan.FromDays(6));

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().BeNull();
        _http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Asks_the_cdn_again_after_a_week()
    {
        // Arrange
        _http.OnGet(NewCdn, HttpStatusCode.NotFound).OnGet(OldCdn, HttpStatusCode.NotFound);
        var resolver = Create();
        await Resolve(resolver);
        _clock.Advance(TimeSpan.FromDays(8));
        _http.OnGet(NewCdn, Jpeg);

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(Path.Combine(_paths.Covers, "steam-10.jpg"));
    }

    [Fact]
    public async Task Gives_no_cover_when_offline_and_asks_again_next_time()
    {
        // Arrange
        _http.On(HttpMethod.Get, NewCdn, _ => throw new HttpRequestException("No route to host"));
        var resolver = Create();
        await Resolve(resolver);
        _http.OnGet(NewCdn, Jpeg);

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().Be(Path.Combine(_paths.Covers, "steam-10.jpg"));
    }

    [Fact]
    public async Task Gives_no_cover_when_the_cdn_fails()
    {
        // Arrange
        _http.OnGet(NewCdn, HttpStatusCode.InternalServerError);
        var resolver = Create();

        // Act
        var path = await Resolve(resolver);

        // Assert
        path.Should().BeNull();
        File.Exists(Path.Combine(_paths.Covers, "steam-10.miss")).Should().BeFalse();
    }

    [Fact]
    public async Task Gives_no_cover_for_a_manual_game_without_asking_the_cdn()
    {
        // Arrange
        var resolver = Create();

        // Act
        var path = await resolver.ResolveAsync(TestGames.Manual("Indie"), TestContext.Current.CancellationToken);

        // Assert
        path.Should().BeNull();
        _http.Requests.Should().BeEmpty();
    }
}
