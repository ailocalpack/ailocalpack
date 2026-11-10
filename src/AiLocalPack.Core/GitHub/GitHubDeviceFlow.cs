using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Core.GitHub;

/// <summary>What the player types at <see cref="VerificationUri"/>, and what the app polls with.</summary>
/// <param name="Interval">The least time between two polls (grows on <c>slow_down</c>).</param>
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval)
{
    /// <summary>Keeps the device code out of logs.</summary>
    public override string ToString() => $"{UserCode} at {VerificationUri} until {ExpiresAt:O}";
}

public enum GitHubAuthProblem
{
    /// <summary>GitHub couldn't be reached (offline, DNS, timeout, a 5xx).</summary>
    Network,

    /// <summary>The OAuth App has Device Flow switched off (<c>device_flow_disabled</c>).</summary>
    DeviceFlowDisabled,

    /// <summary>GitHub doesn't know the client ID (a wrong <c>AILOCALPACK_GITHUB_CLIENT_ID</c>, a deleted app).</summary>
    UnknownClient,

    /// <summary>Anything else GitHub answered that the app doesn't expect.</summary>
    Unexpected,
}

/// <summary>The answer to <see cref="GitHubDeviceFlow.StartAsync"/>.</summary>
public abstract record DeviceCodeResult
{
    private DeviceCodeResult()
    {
    }

    public sealed record Started(DeviceAuthorization Authorization) : DeviceCodeResult;

    /// <param name="Detail">In English, for logs and the CLI.</param>
    public sealed record Failed(GitHubAuthProblem Problem, string Detail) : DeviceCodeResult;
}

/// <summary>How <see cref="GitHubDeviceFlow.WaitForTokenAsync"/> ended.</summary>
public abstract record DeviceTokenResult
{
    private DeviceTokenResult()
    {
    }

    /// <param name="Scopes">The scopes GitHub granted.</param>
    public sealed record Granted(string Token, IReadOnlyList<string> Scopes) : DeviceTokenResult
    {
        public override string ToString() => $"Granted {{ Scopes = {string.Join(",", Scopes)} }}";
    }

    /// <summary>The code ran out (15 minutes) before the player approved it.</summary>
    public sealed record Expired : DeviceTokenResult;

    /// <summary>The player pressed Cancel on GitHub's page.</summary>
    public sealed record Denied : DeviceTokenResult;

    public sealed record Failed(GitHubAuthProblem Problem, string Detail) : DeviceTokenResult;
}

/// <summary>
/// The OAuth 2.0 Device Authorization Grant against GitHub: <see cref="StartAsync"/> gets a user code to show,
/// <see cref="WaitForTokenAsync"/> polls until the player approves it. Polls no faster than GitHub's interval, adds
/// what <c>slow_down</c> asks for (5 s unless it names a new interval), stops at the code's expiry, and rides out network
/// errors while polling (the next poll tries again). Never throws except for cancellation.
/// </summary>
public sealed class GitHubDeviceFlow
{
    /// <summary>Polling never goes faster than this, whatever the server says.</summary>
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly GitHubOAuthApp _app;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    /// <param name="delay">Waits between polls (default <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>); tests move a fake clock.</param>
    public GitHubDeviceFlow(
        HttpClient http,
        GitHubOAuthApp app,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<GitHubDeviceFlow>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(app);
        _http = http;
        _app = app;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, ct) => Task.Delay(span, _time, ct));
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task<DeviceCodeResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var reply = await PostAsync(_app.DeviceCodeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = _app.ClientId,
            ["scope"] = GitHubOAuthApp.Scopes,
        }, cancellationToken).ConfigureAwait(false);
        if (reply.Problem is { } problem)
        {
            return new DeviceCodeResult.Failed(problem.Kind, problem.Detail);
        }

        var json = reply.Json!.Value;
        if (Error(json) is { } error)
        {
            return new DeviceCodeResult.Failed(ProblemOf(error, reply.Status), $"GitHub refused to start the sign-in: {Describe(json, error)}");
        }

        if (String(json, "device_code") is not { } deviceCode || String(json, "user_code") is not { } userCode
            || !Uri.TryCreate(String(json, "verification_uri"), UriKind.Absolute, out var verification)
            || verification.Scheme != Uri.UriSchemeHttps && !_app.Web.IsLoopback)
        {
            return new DeviceCodeResult.Failed(GitHubAuthProblem.Unexpected, "GitHub's answer has no device code, user code or https verification address.");
        }

        var expiresIn = Seconds(json, "expires_in") ?? TimeSpan.FromMinutes(15);
        var interval = Max(Seconds(json, "interval") ?? DefaultInterval, MinimumInterval);
        var authorization = new DeviceAuthorization(deviceCode, userCode, verification, _time.GetUtcNow() + expiresIn, interval);
        _logger.LogInformation("GitHub sign-in started: code {Code}, polling every {Interval}", authorization, interval);
        return new DeviceCodeResult.Started(authorization);
    }

    /// <summary>Polls GitHub until the player approves, denies, or the code expires.</summary>
    public async Task<DeviceTokenResult> WaitForTokenAsync(DeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var interval = authorization.Interval;
        while (true)
        {
            await _delay(interval, cancellationToken).ConfigureAwait(false);
            if (_time.GetUtcNow() >= authorization.ExpiresAt)
            {
                return new DeviceTokenResult.Expired();
            }

            var reply = await PostAsync(_app.TokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = _app.ClientId,
                ["device_code"] = authorization.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, cancellationToken).ConfigureAwait(false);
            if (reply.Problem is { } problem)
            {
                if (problem.Kind != GitHubAuthProblem.Network)
                {
                    return new DeviceTokenResult.Failed(problem.Kind, problem.Detail);
                }

                // Offline for a moment: the code stays valid, so the next poll tries again.
                _logger.LogWarning("Polling GitHub for the sign-in failed, trying again: {Detail}", problem.Detail);
                continue;
            }

            var json = reply.Json!.Value;
            if (String(json, "access_token") is { Length: > 0 } token)
            {
                var scopes = (String(json, "scope") ?? string.Empty).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                _logger.LogInformation("GitHub sign-in approved with scopes {Scopes}", string.Join(",", scopes));
                return new DeviceTokenResult.Granted(token, scopes);
            }

            switch (Error(json))
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = Max(Seconds(json, "interval") ?? interval + SlowDownStep, interval + SlowDownStep);
                    _logger.LogInformation("GitHub asked to poll slower: every {Interval} now", interval);
                    continue;
                case "expired_token":
                    return new DeviceTokenResult.Expired();
                case "access_denied":
                    return new DeviceTokenResult.Denied();
                case { } error:
                    return new DeviceTokenResult.Failed(ProblemOf(error, reply.Status), $"GitHub refused the sign-in: {Describe(json, error)}");
                default:
                    return new DeviceTokenResult.Failed(GitHubAuthProblem.Unexpected, "GitHub's answer has neither a token nor an error.");
            }
        }
    }

    private async Task<Reply> PostAsync(Uri endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
            {
                return new Reply(response.StatusCode, null, (GitHubAuthProblem.Network, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}."));
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return new Reply(response.StatusCode, document.RootElement.Clone(), null);
                }
            }
            catch (JsonException)
            {
                // Falls through: not JSON.
            }

            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized
                ? new Reply(response.StatusCode, null, (GitHubAuthProblem.UnknownClient, $"GitHub answered {(int)response.StatusCode}: it doesn't know the app's client ID {_app.ClientId}."))
                : new Reply(response.StatusCode, null, (GitHubAuthProblem.Unexpected, $"GitHub answered {(int)response.StatusCode} without JSON."));
        }
        catch (HttpRequestException e)
        {
            return new Reply(null, null, (GitHubAuthProblem.Network, $"Can't reach {endpoint.Host}: {e.Message}"));
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            return new Reply(null, null, (GitHubAuthProblem.Network, $"{endpoint.Host} didn't answer in time: {e.Message}"));
        }
    }

    private GitHubAuthProblem ProblemOf(string error, HttpStatusCode? status) => error switch
    {
        "device_flow_disabled" => GitHubAuthProblem.DeviceFlowDisabled,
        "incorrect_client_credentials" or "Not Found" => GitHubAuthProblem.UnknownClient,
        _ when status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized => GitHubAuthProblem.UnknownClient,
        _ => GitHubAuthProblem.Unexpected,
    };

    private static string Describe(JsonElement json, string error) =>
        String(json, "error_description") is { Length: > 0 } description ? $"{description} ({error})" : error;

    private static string? Error(JsonElement json) => String(json, "error");

    private static string? String(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TimeSpan? Seconds(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed record Reply(HttpStatusCode? Status, JsonElement? Json, (GitHubAuthProblem Kind, string Detail)? Problem);
}
