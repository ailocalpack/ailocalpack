using System.Net;
using AiLocalPack.Core.GitHub;
using static AiLocalPack.Core.Tests.GitHub.GitHubFakes;

namespace AiLocalPack.Core.Tests.GitHub;

public sealed class GitHubUserClientTests : IDisposable
{
    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;

    public GitHubUserClientTests() => _client = _http.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    private Task<GitHubUserResult> GetUser() => new GitHubUserClient(_client, App).GetUserAsync("gho_secret", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Returns_the_account_and_the_granted_scopes()
    {
        // Arrange
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse(scopes: "public_repo, read:user"));

        // Act
        var result = await GetUser();

        // Assert
        result.Should().BeEquivalentTo(new GitHubUserResult.Found(
            new GitHubUser("octo", "Octo Cat", new Uri("https://avatars.example/u/1"), new Uri("https://github.com/octo")),
            ["public_repo", "read:user"]));
    }

    [Fact]
    public async Task Sends_the_token_and_the_pinned_api_version()
    {
        // Arrange
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());

        // Act
        await GetUser();

        // Assert
        var request = _http.Requests.Should().ContainSingle().Subject;
        request.Header("Authorization").Should().Be("Bearer gho_secret");
        request.Header("X-GitHub-Api-Version").Should().Be("2022-11-28");
    }

    [Fact]
    public async Task A_missing_name_reads_as_none_and_the_profile_falls_back_to_the_login()
    {
        // Arrange
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse("""{"login":"octo","name":null}"""));

        // Act
        var result = await GetUser();

        // Assert
        result.Should().BeOfType<GitHubUserResult.Found>().Which.User.Should()
            .Be(new GitHubUser("octo", null, null, new Uri("https://github.example/octo")));
    }

    [Fact]
    public async Task A_rejected_token_is_unauthorized()
    {
        // Arrange
        _http.OnGet(UserUrl, """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);

        // Act
        var result = await GetUser();

        // Assert
        result.Should().BeOfType<GitHubUserResult.Unauthorized>();
    }

    [Fact]
    public async Task A_server_error_is_a_network_failure()
    {
        // Arrange
        _http.OnGet(UserUrl, HttpStatusCode.ServiceUnavailable);

        // Act
        var result = await GetUser();

        // Assert
        result.Should().BeOfType<GitHubUserResult.Failed>().Which.IsNetwork.Should().BeTrue();
    }

    [Fact]
    public async Task No_connection_is_a_network_failure()
    {
        // Arrange
        _http.On(HttpMethod.Get, UserUrl, _ => throw new HttpRequestException("No route to host"));

        // Act
        var result = await GetUser();

        // Assert
        result.Should().BeOfType<GitHubUserResult.Failed>().Which.IsNetwork.Should().BeTrue();
    }

    [Fact]
    public async Task An_answer_without_a_login_is_a_failure()
    {
        // Arrange
        _http.OnGet(UserUrl, """{"name":"Octo"}""");

        // Act
        var result = await GetUser();

        // Assert
        result.Should().BeOfType<GitHubUserResult.Failed>().Which.IsNetwork.Should().BeFalse();
    }
}
