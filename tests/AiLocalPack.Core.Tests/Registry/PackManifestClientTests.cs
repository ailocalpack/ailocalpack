using System.Net;
using AiLocalPack.Core.Registry;

namespace AiLocalPack.Core.Tests.Registry;

/// <summary>Fetching one pack's manifest for the Packs tab (task 24): local registry folder or https.</summary>
public sealed class PackManifestClientTests
{
    private const string IndexUrl = "https://example.com/registry/index.json";
    private const string ManifestUrl = "https://example.com/registry/games/steam-620/ru/full/manifest.json";

    private readonly FakeHttpHandler _http = new();

    private static string FixtureManifest => File.ReadAllText(Path.Combine(RegistryFixtures.Folder, "games", "steam-620", "ru", "full", "manifest.json"));

    private static RegistryLocation Location(string text)
    {
        RegistryLocation.TryParse(text, out var location, out _);
        return location!;
    }

    private Task<PackManifestFetch> FetchAsync(RegistryPack pack, string location = IndexUrl, TimeSpan? timeout = null) =>
        new PackManifestClient(_http.CreateClient(), timeout).FetchAsync(Location(location), pack, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Local_registry_manifest_is_read()
    {
        // Arrange
        var pack = RegistryFixtures.Pack();

        // Act
        var fetch = await FetchAsync(pack, RegistryFixtures.Folder);

        // Assert
        (fetch.Problem, fetch.Manifest?.Id, fetch.Manifest?.Revision).Should().Be((PackManifestProblem.None, "full", 2));
    }

    [Fact]
    public async Task Local_registry_without_the_manifest_reports_it_missing()
    {
        // Arrange
        var pack = RegistryFixtures.Pack("gone");

        // Act
        var fetch = await FetchAsync(pack, RegistryFixtures.Folder);

        // Assert
        (fetch.Problem, fetch.IsLoaded).Should().Be((PackManifestProblem.FileMissing, false));
    }

    [Fact]
    public async Task Https_manifest_is_fetched_next_to_the_index()
    {
        // Arrange
        _http.OnGet(ManifestUrl, FixtureManifest);

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        (fetch.Manifest?.Id, _http.Requests.Single().Uri.AbsoluteUri).Should().Be(("full", ManifestUrl));
    }

    [Fact]
    public async Task Not_found_reports_the_manifest_missing()
    {
        // Arrange
        _http.OnGet(ManifestUrl, HttpStatusCode.NotFound);

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        (fetch.Problem, fetch.Detail).Should().Be((PackManifestProblem.FileMissing, "HTTP 404 Not Found"));
    }

    [Fact]
    public async Task Server_error_may_pass()
    {
        // Arrange
        _http.OnGet(ManifestUrl, HttpStatusCode.ServiceUnavailable);

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        (fetch.Problem, fetch.MayPass).Should().Be((PackManifestProblem.HttpError, true));
    }

    [Fact]
    public async Task No_connection_is_offline()
    {
        // Arrange
        _http.OnAsync(ManifestUrl, (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known.")));

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        fetch.Problem.Should().Be(PackManifestProblem.Offline);
    }

    [Fact]
    public async Task Hanging_server_times_out()
    {
        // Arrange
        _http.OnAsync(ManifestUrl, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack(), timeout: TimeSpan.FromMilliseconds(50));

        // Assert
        fetch.Problem.Should().Be(PackManifestProblem.Timeout);
    }

    [Fact]
    public async Task Newer_manifest_format_asks_for_an_app_update()
    {
        // Arrange
        _http.OnGet(ManifestUrl, """{ "schemaVersion": 2, "id": "full", "somethingNew": true }""");

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        (fetch.Problem, fetch.MayPass).Should().Be((PackManifestProblem.NewerFormat, false));
    }

    [Fact]
    public async Task Invalid_manifest_lists_its_errors()
    {
        // Arrange
        _http.OnGet(ManifestUrl, """{ "schemaVersion": 1, "id": "full" }""");

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        (fetch.Problem, fetch.Errors.Count > 0).Should().Be((PackManifestProblem.Invalid, true));
    }

    [Fact]
    public async Task Not_json_is_invalid()
    {
        // Arrange
        _http.OnGet(ManifestUrl, "<html>maintenance</html>");

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        fetch.Problem.Should().Be(PackManifestProblem.Invalid);
    }

    [Fact]
    public async Task Manifest_of_another_pack_is_a_mismatch()
    {
        // Arrange
        _http.OnGet(ManifestUrl, FixtureManifest);
        var pack = RegistryFixtures.Pack("menus") with { ManifestPath = "games/steam-620/ru/full/manifest.json" };

        // Act
        var fetch = await FetchAsync(pack);

        // Assert
        fetch.Problem.Should().Be(PackManifestProblem.Mismatch);
    }

    [Fact]
    public async Task Manifest_outside_the_registry_is_refused_without_a_request()
    {
        // Arrange
        var pack = RegistryFixtures.Pack() with { ManifestPath = "http://evil.test/manifest.json" };

        // Act
        var fetch = await FetchAsync(pack);

        // Assert
        (fetch.Problem, _http.Requests.Count).Should().Be((PackManifestProblem.Invalid, 0));
    }

    [Fact]
    public async Task Oversized_manifest_is_refused()
    {
        // Arrange
        _http.OnGet(ManifestUrl, new byte[PackManifestClient.MaxManifestBytes + 1]);

        // Act
        var fetch = await FetchAsync(RegistryFixtures.Pack());

        // Assert
        (fetch.Problem, fetch.Detail).Should().Be((PackManifestProblem.Invalid, "The manifest is too large."));
    }
}
