using System.Net;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;
using static AiLocalPack.Core.Tests.GitHub.GitHubFakes;

namespace AiLocalPack.Core.Tests.GitHub;

public sealed class GitHubSessionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _home = new();
    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly FakeClock _clock = new(Start);
    private readonly FakeSecretStore _keychain = new();

    public GitHubSessionTests() => _client = _http.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _home.Dispose();
    }

    private AppPaths Paths => AppPaths.UnderHome(_home.Path);

    /// <summary>Signed in after the first poll, one 5 s interval after <see cref="Start"/>.</summary>
    private static readonly GitHubAccount Octo = new(
        "octo", "Octo Cat", new Uri("https://avatars.example/u/1"), new Uri("https://github.com/octo"), ["public_repo"], Start.AddSeconds(5));

    private GitHubSession Session() => new(
        _keychain,
        new GitHubDeviceFlow(_client, App, _clock, new RecordedDelays(_clock).Delay),
        new GitHubUserClient(_client, App),
        Paths,
        App,
        _clock);

    private static DeviceAuthorization Authorization() =>
        new("dev-123", "WDJB-MJHT", new Uri("https://github.com/login/device"), Start.AddMinutes(15), TimeSpan.FromSeconds(5));

    private Task<GitHubSignInResult> SignIn(GitHubSession? session = null) =>
        (session ?? Session()).FinishSignInAsync(Authorization(), TestContext.Current.CancellationToken);

    /// <summary>A finished sign-in as octo with token <c>gho_secret</c>.</summary>
    private async Task SignedInBefore()
    {
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());
        await SignIn();
    }

    [Fact]
    public async Task Sign_in_keeps_the_token_in_the_keychain()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());

        // Act
        await SignIn();

        // Assert
        _keychain.Secrets.Should().Equal(new Dictionary<string, string> { [GitHubSession.TokenSecret] = "gho_secret" });
    }

    [Fact]
    public async Task Sign_in_returns_and_remembers_the_account()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());
        var session = Session();

        // Act
        var result = await SignIn(session);

        // Assert
        result.Should().BeEquivalentTo(new GitHubSignInResult.SignedIn(Octo));
        Session().Account.Should().BeEquivalentTo(Octo);
    }

    [Fact]
    public async Task Sign_in_never_writes_the_token_into_the_account_file()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());

        // Act
        await SignIn();

        // Assert
        File.ReadAllText(Paths.GitHubAccountFile).Should().NotContain("gho_secret");
    }

    [Fact]
    public async Task Sign_in_raises_changed()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());
        var session = Session();
        var changes = 0;
        session.Changed += (_, _) => changes++;

        // Act
        await SignIn(session);

        // Assert
        changes.Should().Be(1);
    }

    [Fact]
    public async Task Without_a_keychain_the_sign_in_is_not_kept()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());
        _keychain.FailWith = SecretResult.Unavailable("No keyring is running.");

        // Act
        var result = await SignIn();

        // Assert
        result.Should().BeOfType<GitHubSignInResult.NotSaved>().Which.Keychain.Status.Should().Be(SecretStatus.Unavailable);
        File.Exists(Paths.GitHubAccountFile).Should().BeFalse();
    }

    [Fact]
    public async Task A_denied_sign_in_keeps_nothing()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("access_denied")));

        // Act
        var result = await SignIn();

        // Assert
        result.Should().BeOfType<GitHubSignInResult.Denied>();
        _keychain.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_code_ends_the_sign_in_as_expired()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Error("expired_token")));

        // Act
        var result = await SignIn();

        // Assert
        result.Should().BeOfType<GitHubSignInResult.Expired>();
    }

    [Fact]
    public async Task Github_out_of_reach_after_the_approval_fails_the_sign_in_as_network()
    {
        // Arrange
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.OnGet(UserUrl, HttpStatusCode.BadGateway);

        // Act
        var result = await SignIn();

        // Assert
        result.Should().BeOfType<GitHubSignInResult.Failed>().Which.Problem.Should().Be(GitHubAuthProblem.Network);
        _keychain.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_confirms_the_account_with_github()
    {
        // Arrange
        await SignedInBefore();

        // Act
        var result = await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeEquivalentTo(new GitHubAccountCheck.SignedIn(Octo));
    }

    [Fact]
    public async Task Check_refreshes_a_changed_name()
    {
        // Arrange
        await SignedInBefore();
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse(UserJson.Replace("Octo Cat", "Octocat", StringComparison.Ordinal)));

        // Act
        await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        Session().Account!.Name.Should().Be("Octocat");
    }

    [Fact]
    public async Task Check_signs_out_when_github_rejects_the_token()
    {
        // Arrange
        await SignedInBefore();
        _http.OnGet(UserUrl, """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);

        // Act
        var result = await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new GitHubAccountCheck.Revoked("octo"));
        _keychain.Secrets.Should().BeEmpty();
        Session().Account.Should().BeNull();
    }

    [Fact]
    public async Task Check_offline_returns_the_remembered_account_unverified()
    {
        // Arrange
        await SignedInBefore();
        _http.On(HttpMethod.Get, UserUrl, _ => throw new HttpRequestException("Network is down"));

        // Act
        var result = await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<GitHubAccountCheck.Unverified>().Which.Account.Should().BeEquivalentTo(Octo);
    }

    [Fact]
    public async Task Check_signs_out_when_the_token_was_removed_from_the_keychain()
    {
        // Arrange
        await SignedInBefore();
        await _keychain.DeleteAsync(GitHubSession.TokenSecret, TestContext.Current.CancellationToken);

        // Act
        var result = await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<GitHubAccountCheck.SignedOut>();
        Session().Account.Should().BeNull();
    }

    [Fact]
    public async Task Check_reports_a_keychain_that_cannot_be_read()
    {
        // Arrange
        await SignedInBefore();
        _keychain.FailWith = SecretResult.Failed("The unlock prompt was dismissed.");

        // Act
        var result = await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<GitHubAccountCheck.KeychainProblem>().Which.Account.Should().BeEquivalentTo(Octo);
    }

    [Fact]
    public async Task Check_without_a_sign_in_is_signed_out()
    {
        // Act
        var result = await Session().CheckAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<GitHubAccountCheck.SignedOut>();
    }

    [Fact]
    public async Task Sign_out_deletes_the_token_and_the_account()
    {
        // Arrange
        await SignedInBefore();

        // Act
        var result = await Session().SignOutAsync(TestContext.Current.CancellationToken);

        // Assert
        result.IsOk.Should().BeTrue();
        _keychain.Secrets.Should().BeEmpty();
        Session().Account.Should().BeNull();
    }

    [Fact]
    public async Task Sign_out_keeps_the_account_when_the_keychain_refuses_to_delete_the_token()
    {
        // Arrange
        await SignedInBefore();
        _keychain.FailWith = SecretResult.Failed("Access was refused.");

        // Act
        var result = await Session().SignOutAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Status.Should().Be(SecretStatus.Failed);
        Session().Account.Should().NotBeNull();
    }

    [Fact]
    public void A_damaged_account_file_reads_as_signed_out_and_is_moved_aside()
    {
        // Arrange
        _home.File("data/github-account.json", "{ not json");

        // Act
        var account = Session().Account;

        // Assert
        account.Should().BeNull();
        Directory.GetFiles(_home.Combine("data"), "github-account.json.*.bak").Should().ContainSingle();
    }

    [Fact]
    public async Task A_newer_format_account_file_is_never_overwritten()
    {
        // Arrange
        const string Newer = """{"schemaVersion":2,"login":"someone"}""";
        _home.File("data/github-account.json", Newer);
        _http.OnPostSequence(TokenUrl, () => Reply(Token()));
        _http.On(HttpMethod.Get, UserUrl, _ => UserResponse());

        // Act
        await SignIn();

        // Assert
        File.ReadAllText(Paths.GitHubAccountFile).Should().Be(Newer);
    }

    [Fact]
    public void The_connections_page_names_the_client_id()
    {
        // Act
        var page = App.ConnectionsPage;

        // Assert
        page.Should().Be(new Uri("https://github.example/settings/connections/applications/test-client"));
    }
}
