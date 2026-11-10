using System.Net;
using System.Text;
using AiLocalPack.Core.Publishing;

namespace AiLocalPack.Core.Tests.Publishing;

/// <summary>How GitHub's error answers become typed problems (task 60).</summary>
public sealed class GitHubRestClientTests : IDisposable
{
    private const string Url = "https://api.github.example/repos/a/b";

    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly FakeClock _clock = new(PublishBed.Now);

    public GitHubRestClientTests() => _client = _http.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    private async Task<GitHubApiError> ErrorOf(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        _http.On(HttpMethod.Get, Url, _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        var api = new GitHubRestClient(_client, PublishBed.App, "token", _clock);
        try
        {
            await api.GetAsync("repos/a/b", TestContext.Current.CancellationToken);
            throw new InvalidOperationException("No error.");
        }
        catch (GitHubApiException e)
        {
            return e.Error;
        }
    }

    [Fact]
    public async Task A_403_with_no_requests_left_is_a_rate_limit_until_the_reset()
    {
        // Act
        var error = await ErrorOf(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "1794321000"));

        // Assert
        error.Should().Be(new GitHubApiError(PublishProblem.RateLimited, 403, "API rate limit exceeded.", DateTimeOffset.FromUnixTimeSeconds(1794321000)));
    }

    [Fact]
    public async Task A_429_with_retry_after_is_a_rate_limit_for_those_seconds()
    {
        // Act
        var error = await ErrorOf(HttpStatusCode.TooManyRequests, "{}", ("retry-after", "30"));

        // Assert
        error.RetryAt.Should().Be(PublishBed.Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_403_that_says_rate_limit_is_a_rate_limit()
    {
        // Act
        var error = await ErrorOf(HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit"}""");

        // Assert
        error.Problem.Should().Be(PublishProblem.RateLimited);
    }

    [Fact]
    public async Task A_plain_403_is_forbidden()
    {
        // Act
        var error = await ErrorOf(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""", ("x-ratelimit-remaining", "4999"));

        // Assert
        error.Problem.Should().Be(PublishProblem.Forbidden);
    }

    [Fact]
    public async Task A_422_lists_githubs_reasons()
    {
        // Act
        var error = await ErrorOf(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for o:b."},"plain reason"]}""");

        // Assert
        error.Should().Be(new GitHubApiError(PublishProblem.Rejected, 422, "Validation Failed: A pull request already exists for o:b: plain reason."));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, PublishProblem.Network)]
    [InlineData(HttpStatusCode.RequestTimeout, PublishProblem.Network)]
    [InlineData(HttpStatusCode.Unauthorized, PublishProblem.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, PublishProblem.NotFound)]
    [InlineData(HttpStatusCode.Conflict, PublishProblem.Conflict)]
    public async Task A_status_has_its_problem(HttpStatusCode status, PublishProblem expected)
    {
        // Act
        var error = await ErrorOf(status, "{}");

        // Assert
        error.Problem.Should().Be(expected);
    }

    [Fact]
    public async Task No_answer_is_a_network_problem()
    {
        // Arrange
        _http.On(HttpMethod.Get, Url, _ => throw new HttpRequestException("connection refused"));
        var api = new GitHubRestClient(_client, PublishBed.App, "token", _clock);

        // Act
        var act = () => api.GetAsync("repos/a/b", TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.Error.Problem.Should().Be(PublishProblem.Network);
    }
}
