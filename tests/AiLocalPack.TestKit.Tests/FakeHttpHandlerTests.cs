using System.Net;

namespace AiLocalPack.TestKit.Tests;

public sealed class FakeHttpHandlerTests
{
    private const string IndexUrl = "https://ailocalpack.github.io/registry/index.json";

    [Fact]
    public async Task Returns_the_configured_response()
    {
        // Arrange
        using var handler = new FakeHttpHandler().OnGet(IndexUrl, """{"packs":[]}""");
        using var client = handler.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // Act
        using var response = await client.GetAsync(new Uri(IndexUrl), ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync(ct)).Should().Be("""{"packs":[]}""");
    }

    [Fact]
    public async Task Returns_configured_error_status_and_binary_body()
    {
        // Arrange
        using var handler = new FakeHttpHandler()
            .OnGet("https://example.test/missing", HttpStatusCode.NotFound)
            .OnGet("https://example.test/blob.bin", [0xFF, 0x00]);
        using var client = handler.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // Act
        using var missing = await client.GetAsync(new Uri("https://example.test/missing"), ct);
        var blob = await client.GetByteArrayAsync(new Uri("https://example.test/blob.bin"), ct);

        // Assert
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        blob.Should().Equal(0xFF, 0x00);
    }

    [Fact]
    public async Task Fails_on_an_unknown_route_and_names_it()
    {
        // Arrange
        using var handler = new FakeHttpHandler().OnGet(IndexUrl, "{}");
        using var client = handler.CreateClient();

        // Act
        var act = () => client.GetAsync(new Uri("https://example.test/other"), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*GET https://example.test/other*{IndexUrl}*");
    }

    [Fact]
    public async Task Matches_on_method_too()
    {
        // Arrange
        using var handler = new FakeHttpHandler().OnGet(IndexUrl, "{}");
        using var client = handler.CreateClient();
        using var content = new StringContent("x");

        // Act
        var act = () => client.PostAsync(new Uri(IndexUrl), content, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*POST*");
    }

    [Fact]
    public async Task Records_requests_with_their_bodies()
    {
        // Arrange
        const string url = "https://api.github.test/repos";
        using var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, url, _ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = handler.CreateClient();
        using var content = new StringContent("""{"name":"registry"}""");

        // Act
        using var response = await client.PostAsync(new Uri(url), content, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        handler.Requests.Should().ContainSingle()
            .Which.Should().Be(new RecordedRequest(HttpMethod.Post, new Uri(url), """{"name":"registry"}"""));
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        // Arrange
        using var handler = new FakeHttpHandler().OnGet(IndexUrl, "{}");
        using var client = handler.CreateClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var act = () => client.GetAsync(new Uri(IndexUrl), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
