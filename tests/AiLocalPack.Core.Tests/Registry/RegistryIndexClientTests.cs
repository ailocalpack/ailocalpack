using System.Net;
using System.Text;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Registry;

public sealed class RegistryIndexClientTests : IDisposable
{
    private const string Url = "https://example.com/registry/index.json";

    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _home = new();
    private readonly FakeHttpHandler _http = new();
    private readonly FakeClock _clock = new(Start);
    private readonly HttpClient _client;
    private readonly RegistryLocation _location;

    public RegistryIndexClientTests()
    {
        _client = _http.CreateClient();
        RegistryLocation.TryParse(Url, out var location, out _);
        _location = location!;
    }

    private AppPaths Paths => AppPaths.UnderHome(_home.Path);

    private RegistryIndexClient Client(TimeSpan? timeout = null) =>
        new(_client, Paths, _clock, options: new RegistryIndexClientOptions(timeout ?? TimeSpan.FromSeconds(10), Attempts: 3, RetryDelay: TimeSpan.Zero));

    private static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        response.Content.Headers.LastModified = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero);
        return response;
    }

    private Task<RegistryIndexSnapshot> Fetch(RegistryIndexClient? client = null, RegistryLocation? location = null) =>
        (client ?? Client()).FetchAsync(location ?? _location, TestContext.Current.CancellationToken);

    /// <summary>One good fetch at <see cref="Start"/>, so a saved copy exists; then the clock moves an hour on.</summary>
    private async Task SaveCopy(string? etag = "\"v1\"")
    {
        var body = RegistryFixtures.IndexJson(RegistryFixtures.Pack());
        _http.On(HttpMethod.Get, Url, _ => Json(body, etag));
        await Fetch();
        _clock.Advance(TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _home.Dispose();
    }

    [Fact]
    public async Task Ok_response_gives_a_fresh_index()
    {
        // Arrange
        _http.On(HttpMethod.Get, Url, _ => Json(RegistryFixtures.IndexJson(RegistryFixtures.Pack())));

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.Index!.PackCount, snapshot.FetchedAt).Should().Be((RegistryIndexState.Fresh, 1, Start));
    }

    [Fact]
    public async Task Ok_response_is_saved_for_later()
    {
        // Arrange
        _http.On(HttpMethod.Get, Url, _ => Json(RegistryFixtures.IndexJson(RegistryFixtures.Pack())));
        await Fetch();

        // Act
        var cached = Client().LoadCached(_location);

        // Assert
        (cached!.State, cached.Index!.PackCount, cached.FetchedAt).Should().Be((RegistryIndexState.Stale, 1, Start));
    }

    [Fact]
    public async Task Next_fetch_asks_only_for_changes()
    {
        // Arrange
        await SaveCopy();

        // Act
        await Fetch();

        // Assert
        var request = _http.Requests[^1];
        (request.Header("If-None-Match"), request.Header("If-Modified-Since"))
            .Should().Be(("\"v1\"", "Wed, 04 Nov 2026 11:00:00 GMT"));
    }

    [Fact]
    public async Task Not_modified_keeps_the_saved_index_as_fresh()
    {
        // Arrange
        await SaveCopy();
        _http.OnGet(Url, HttpStatusCode.NotModified);

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.Index!.PackCount, snapshot.FetchedAt).Should().Be((RegistryIndexState.Fresh, 1, Start.AddHours(1)));
    }

    [Fact]
    public async Task Not_modified_moves_the_saved_date()
    {
        // Arrange
        await SaveCopy();
        _http.OnGet(Url, HttpStatusCode.NotModified);
        await Fetch();

        // Act
        var cached = Client().LoadCached(_location);

        // Assert
        cached!.FetchedAt.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task Server_error_is_retried()
    {
        // Arrange
        await SaveCopy();
        _http.OnGet(Url, HttpStatusCode.InternalServerError);

        // Act
        await Fetch();

        // Assert
        _http.Requests.Should().HaveCount(1 + 3);
    }

    [Fact]
    public async Task Server_error_shows_the_saved_index_as_stale()
    {
        // Arrange
        await SaveCopy();
        _http.OnGet(Url, HttpStatusCode.InternalServerError);

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.Problem, snapshot.FetchedAt, snapshot.Index!.PackCount)
            .Should().Be((RegistryIndexState.Stale, RegistryIndexProblem.HttpError, Start, 1));
    }

    [Fact]
    public async Task Server_recovering_on_a_retry_gives_a_fresh_index()
    {
        // Arrange
        var calls = 0;
        var body = RegistryFixtures.IndexJson(RegistryFixtures.Pack());
        _http.On(HttpMethod.Get, Url, _ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(body));

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, calls).Should().Be((RegistryIndexState.Fresh, 2));
    }

    [Fact]
    public async Task Not_found_is_not_retried()
    {
        // Arrange
        _http.OnGet(Url, HttpStatusCode.NotFound);

        // Act
        var snapshot = await Fetch();

        // Assert
        (_http.Requests.Count, snapshot.Problem, snapshot.Detail).Should().Be((1, RegistryIndexProblem.HttpError, "HTTP 404 Not Found"));
    }

    [Fact]
    public async Task Timeout_shows_the_saved_index_as_stale()
    {
        // Arrange
        await SaveCopy();
        _http.OnAsync(Url, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Act
        var snapshot = await Fetch(Client(TimeSpan.FromMilliseconds(50)));

        // Assert
        (snapshot.State, snapshot.Problem, snapshot.FetchedAt).Should().Be((RegistryIndexState.Stale, RegistryIndexProblem.Timeout, Start));
    }

    [Fact]
    public async Task Malformed_index_keeps_the_saved_copy()
    {
        // Arrange
        await SaveCopy();
        _http.On(HttpMethod.Get, Url, _ => Json("{ \"schemaVersion\": 1, \"games\": "));
        await Fetch();

        // Act
        var cached = Client().LoadCached(_location);

        // Assert
        (cached!.Index!.PackCount, cached.FetchedAt).Should().Be((1, Start));
    }

    [Fact]
    public async Task Malformed_index_shows_the_saved_one_as_stale()
    {
        // Arrange
        await SaveCopy();
        _http.On(HttpMethod.Get, Url, _ => Json("<html>maintenance</html>"));

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.Problem, _http.Requests.Count).Should().Be((RegistryIndexState.Stale, RegistryIndexProblem.Malformed, 2));
    }

    [Fact]
    public async Task Newer_format_asks_for_an_app_update()
    {
        // Arrange
        _http.On(HttpMethod.Get, Url, _ => Json("""{ "schemaVersion": 2, "generated": "2026-11-04T12:00:00Z", "games": {} }"""));

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.Problem).Should().Be((RegistryIndexState.Unavailable, RegistryIndexProblem.NewerFormat));
    }

    [Fact]
    public async Task Offline_without_a_saved_copy_is_a_clear_error()
    {
        // Arrange
        _http.OnAsync(Url, (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known.")));

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.Index, snapshot.Problem, snapshot.Detail)
            .Should().Be((RegistryIndexState.Unavailable, (RegistryIndex?)null, RegistryIndexProblem.Offline, "No such host is known."));
    }

    [Fact]
    public async Task Offline_with_a_saved_copy_shows_it_with_its_date()
    {
        // Arrange
        await SaveCopy();
        _http.OnAsync(Url, (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known.")));

        // Act
        var snapshot = await Fetch();

        // Assert
        (snapshot.State, snapshot.FetchedAt, snapshot.Problem).Should().Be((RegistryIndexState.Stale, Start, RegistryIndexProblem.Offline));
    }

    [Fact]
    public async Task Saved_copy_from_another_registry_is_not_used()
    {
        // Arrange
        await SaveCopy();
        RegistryLocation.TryParse("https://other.example/index.json", out var other, out _);

        // Act
        var cached = Client().LoadCached(other!);

        // Assert
        cached.Should().BeNull();
    }

    [Fact]
    public async Task Damaged_saved_copy_is_not_used()
    {
        // Arrange
        await SaveCopy();
        await File.WriteAllTextAsync(Path.Combine(Paths.RegistryCache, "index.json"), "{}", TestContext.Current.CancellationToken);

        // Act
        var cached = Client().LoadCached(_location);

        // Assert
        cached.Should().BeNull();
    }

    [Fact]
    public async Task Index_over_the_size_limit_is_refused()
    {
        // Arrange
        var client = new RegistryIndexClient(_client, Paths, _clock, options: new RegistryIndexClientOptions(TimeSpan.FromSeconds(10), MaxIndexBytes: 100));
        _http.On(HttpMethod.Get, Url, _ => Json(RegistryFixtures.IndexJson(RegistryFixtures.Pack())));

        // Act
        var snapshot = await Fetch(client);

        // Assert
        (snapshot.Problem, snapshot.Detail).Should().Be((RegistryIndexProblem.Malformed, "The index is larger than 100 bytes."));
    }

    [Fact]
    public async Task Cancelling_stops_the_fetch()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _http.On(HttpMethod.Get, Url, _ => Json("{}"));

        // Act
        var act = () => Client().FetchAsync(_location, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Local_folder_is_read_without_network()
    {
        // Arrange
        RegistryLocation.TryParse(RegistryFixtures.Folder, out var local, out _);

        // Act
        var snapshot = await Fetch(location: local);

        // Assert
        (snapshot.State, snapshot.Index!.PackCount, _http.Requests.Count).Should().Be((RegistryIndexState.Fresh, 4, 0));
    }

    [Fact]
    public async Task Local_folder_without_an_index_is_a_clear_error()
    {
        // Arrange
        RegistryLocation.TryParse(_home.Combine("no-registry"), out var local, out _);

        // Act
        var snapshot = await Fetch(location: local);

        // Assert
        (snapshot.State, snapshot.Problem, snapshot.Detail)
            .Should().Be((RegistryIndexState.Unavailable, RegistryIndexProblem.LocalFileMissing, $"There is no index.json at {Path.Combine(_home.Combine("no-registry"), "index.json")}."));
    }

    [Fact]
    public async Task Local_malformed_index_is_unavailable()
    {
        // Arrange
        _home.File("registry/index.json", "{ broken");
        RegistryLocation.TryParse(_home.Combine("registry"), out var local, out _);

        // Act
        var snapshot = await Fetch(location: local);

        // Assert
        (snapshot.State, snapshot.Problem).Should().Be((RegistryIndexState.Unavailable, RegistryIndexProblem.Malformed));
    }

    [Fact]
    public async Task Local_folder_is_not_cached()
    {
        // Arrange
        RegistryLocation.TryParse(RegistryFixtures.Folder, out var local, out _);
        await Fetch(location: local);

        // Act
        var cached = Client().LoadCached(local!);

        // Assert
        (cached, Directory.Exists(Paths.RegistryCache)).Should().Be(((RegistryIndexSnapshot?)null, false));
    }
}
