using System.Net;
using System.Security.Cryptography;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Fonts;

public sealed class FontStoreTests : IDisposable
{
    private const string Url = "https://fonts.example.org/Test-Regular.otf";

    private static readonly byte[] Content = [.. "OTTO test font bytes"u8];

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _http = new();

    public void Dispose()
    {
        _dir.Dispose();
        _http.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WhitelistFont Font(byte[] content) => new(
        "test-font",
        "Test Font",
        "OFL-1.1",
        ["ja"],
        false,
        [new WhitelistFontFile(FontFace.Regular, "Test-Regular.otf", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), new Uri(Url))]);

    private FontStore Store() => new(_dir.Path, _http.CreateClient(), options: new PackFetchOptions(TimeSpan.FromSeconds(5), Attempts: 1));

    [Fact]
    public async Task Every_bundled_font_ships_with_the_size_and_hash_the_whitelist_gives()
    {
        // Arrange
        var store = Store();
        var files = FontWhitelist.Bundled.Fonts.SelectMany(f => f.Files.Where(file => file.IsBundled).Select(file => (Font: f, File: file))).ToList();

        // Act
        var results = await Task.WhenAll(files.Select(f => store.GetAsync(f.Font, f.File, cancellationToken: Ct)));

        // Assert
        results.Select(r => r.Content is { } bytes ? (bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes))) : (0, string.Empty))
            .Should().Equal(files.Select(f => (f.File.Size, f.File.Sha256)));
    }

    [Fact]
    public async Task A_bundled_font_is_read_without_the_network()
    {
        // Arrange
        var font = FontWhitelist.Bundled.Find("noto-sans")!;

        // Act
        var result = await Store().GetAsync(font, font.File(FontFace.Regular)!, cancellationToken: Ct);

        // Assert
        (result.Problem, result.Content?.Length, _http.Requests.Count).Should().Be((FontFetchProblem.None, 621572, 0));
    }

    [Fact]
    public async Task A_downloaded_font_is_checked_and_kept_in_the_cache()
    {
        // Arrange
        _http.OnGet(Url, Content);
        var font = Font(Content);
        var store = Store();
        await store.GetAsync(font, font.Files[0], cancellationToken: Ct);

        // Act
        var again = await store.GetAsync(font, font.Files[0], cancellationToken: Ct);

        // Assert
        (again.Content, again.Downloaded, _http.Requests.Count, File.Exists(store.CachedPath(font, font.Files[0])))
            .Should().BeEquivalentTo((Content, 0L, 1, true));
    }

    [Fact]
    public async Task A_download_with_another_hash_is_deleted()
    {
        // Arrange
        _http.OnGet(Url, [.. "OTTO other bytes!!!!"u8]);
        var font = Font(Content);
        var store = Store();

        // Act
        var result = await store.GetAsync(font, font.Files[0], cancellationToken: Ct);

        // Assert
        (result.Problem, result.Content, store.IsAvailable(font, font.Files[0])).Should().Be((FontFetchProblem.HashMismatch, null, false));
    }

    [Fact]
    public async Task A_font_gone_from_its_address_is_reported_missing()
    {
        // Arrange
        _http.OnGet(Url, HttpStatusCode.NotFound);
        var font = Font(Content);

        // Act
        var result = await Store().GetAsync(font, font.Files[0], cancellationToken: Ct);

        // Assert
        (result.Problem, result.MayPass).Should().Be((FontFetchProblem.FileMissing, false));
    }

    [Fact]
    public async Task No_connection_is_a_problem_that_may_pass()
    {
        // Arrange
        _http.On(HttpMethod.Get, Url, _ => throw new HttpRequestException("No route to host"));
        var font = Font(Content);

        // Act
        var result = await Store().GetAsync(font, font.Files[0], cancellationToken: Ct);

        // Assert
        (result.Problem, result.MayPass).Should().Be((FontFetchProblem.Offline, true));
    }
}
