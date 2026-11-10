using System.Net;
using AiLocalPack.Core.GitHub;
using static AiLocalPack.Core.Tests.GitHub.GitHubFakes;

namespace AiLocalPack.Core.Tests.GitHub;

public sealed class GitHubDeviceFlowTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly FakeClock _clock = new(Start);
    private readonly RecordedDelays _delays;

    public GitHubDeviceFlowTests()
    {
        _client = _http.CreateClient();
        _delays = new RecordedDelays(_clock);
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    private GitHubDeviceFlow Flow() => new(_client, App, _clock, _delays.Delay);

    private static DeviceAuthorization Authorization(int intervalSeconds = 5) =>
        new("dev-123", "WDJB-MJHT", new Uri("https://github.com/login/device"), Start.AddMinutes(15), TimeSpan.FromSeconds(intervalSeconds));

    private Task<DeviceTokenResult> Wait(DeviceAuthorization? authorization = null) =>
        Flow().WaitForTokenAsync(authorization ?? Authorization(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Start_returns_the_code_to_show_with_its_expiry_and_interval()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => Reply(DeviceCodeJson));

        // Act
        var result = await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new DeviceCodeResult.Started(new DeviceAuthorization(
            "dev-123", "WDJB-MJHT", new Uri("https://github.com/login/device"), Start.AddSeconds(900), TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Start_asks_for_the_public_repo_scope_with_the_client_id_and_json()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => Reply(DeviceCodeJson));

        // Act
        await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        var request = _http.Requests.Should().ContainSingle().Subject;
        request.Body.Should().Be("client_id=test-client&scope=public_repo");
        request.Header("Accept").Should().Be("application/json");
    }

    [Fact]
    public async Task Start_never_polls_faster_than_once_a_second()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => Reply(DeviceCodeJson.Replace("\"interval\":5", "\"interval\":0", StringComparison.Ordinal)));

        // Act
        var result = await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<DeviceCodeResult.Started>().Which.Authorization.Interval.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Start_reports_an_app_with_device_flow_switched_off()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => Reply(Error("device_flow_disabled"), HttpStatusCode.BadRequest));

        // Act
        var result = await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<DeviceCodeResult.Failed>().Which.Problem.Should().Be(GitHubAuthProblem.DeviceFlowDisabled);
    }

    [Fact]
    public async Task Start_reports_an_unknown_client_id()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") });

        // Act
        var result = await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<DeviceCodeResult.Failed>().Which.Problem.Should().Be(GitHubAuthProblem.UnknownClient);
    }

    [Fact]
    public async Task Start_reports_github_out_of_reach()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => throw new HttpRequestException("No such host"));

        // Act
        var result = await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<DeviceCodeResult.Failed>().Which.Problem.Should().Be(GitHubAuthProblem.Network);
    }

    [Fact]
    public async Task Start_refuses_an_answer_without_a_user_code()
    {
        // Arrange
        _http.On(HttpMethod.Post, DeviceCodeUrl, _ => Reply("""{"device_code":"dev-123","verification_uri":"https://github.com/login/device"}"""));

        // Act
        var result = await Flow().StartAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<DeviceCodeResult.Failed>().Which.Problem.Should().Be(GitHubAuthProblem.Unexpected);
    }

    [Fact]
    public async Task Wait_polls_while_pending_and_returns_the_token_with_its_scopes()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("authorization_pending")), () => Reply(Error("authorization_pending")), () => Reply(Token()));

        // Act
        var result = await Wait();

        // Assert
        result.Should().BeEquivalentTo(new DeviceTokenResult.Granted("gho_secret", ["public_repo"]));
    }

    [Fact]
    public async Task Wait_waits_the_interval_before_every_poll()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("authorization_pending")), () => Reply(Token()));

        // Act
        await Wait();

        // Assert
        _delays.Waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Wait_sends_the_device_code_with_the_device_grant_type()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));

        // Act
        await Wait();

        // Assert
        _http.Requests.Should().ContainSingle().Which.Body.Should()
            .Be("client_id=test-client&device_code=dev-123&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code");
    }

    [Fact]
    public async Task Wait_slows_down_by_five_seconds_when_asked_without_a_new_interval()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("slow_down")), () => Reply(Error("authorization_pending")), () => Reply(Token()));

        // Act
        await Wait();

        // Assert
        _delays.Waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Wait_slows_down_to_the_interval_github_names()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("slow_down", interval: 15)), () => Reply(Token()));

        // Act
        await Wait();

        // Assert
        _delays.Waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Wait_ends_expired_when_github_says_the_code_expired()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("authorization_pending")), () => Reply(Error("expired_token")));

        // Act
        var result = await Wait();

        // Assert
        result.Should().BeOfType<DeviceTokenResult.Expired>();
    }

    [Fact]
    public async Task Wait_ends_expired_at_the_code_expiry_without_asking_again()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("authorization_pending")));
        var authorization = Authorization() with { ExpiresAt = Start.AddSeconds(12) };

        // Act
        var result = await Wait(authorization);

        // Assert
        result.Should().BeOfType<DeviceTokenResult.Expired>();
        _http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Wait_ends_denied_when_the_player_cancels_on_github()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("access_denied")));

        // Act
        var result = await Wait();

        // Assert
        result.Should().BeOfType<DeviceTokenResult.Denied>();
    }

    [Fact]
    public async Task Wait_rides_out_a_network_error_and_polls_again()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => throw new HttpRequestException("Network is down"), () => new HttpResponseMessage(HttpStatusCode.BadGateway), () => Reply(Token()));

        // Act
        var result = await Wait();

        // Assert
        result.Should().BeOfType<DeviceTokenResult.Granted>();
    }

    [Fact]
    public async Task Wait_fails_on_an_error_it_does_not_know()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("unsupported_grant_type")));

        // Act
        var result = await Wait();

        // Assert
        result.Should().BeOfType<DeviceTokenResult.Failed>().Which.Problem.Should().Be(GitHubAuthProblem.Unexpected);
    }

    [Fact]
    public async Task Wait_stops_when_cancelled()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("authorization_pending")));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        // Act
        var act = () => Flow().WaitForTokenAsync(Authorization(), cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void A_granted_token_never_shows_in_its_text()
    {
        // Arrange
        var granted = new DeviceTokenResult.Granted("gho_secret", ["public_repo"]);

        // Act
        var text = granted.ToString();

        // Assert
        text.Should().NotContain("gho_secret");
    }
}
