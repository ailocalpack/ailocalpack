using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLocalPack.Core.GitHub;

namespace AiLocalPack.Core.Publishing;

/// <summary>Why publishing stopped.</summary>
public enum PublishProblem
{
    /// <summary>No GitHub token: the player has to sign in.</summary>
    NotSignedIn,

    /// <summary>The keychain couldn't give the token.</summary>
    Keychain,

    /// <summary>401: GitHub no longer accepts the token (revoked).</summary>
    Unauthorized,

    /// <summary>403 without a rate limit: the token may not do this (scope), or the repository refuses it.</summary>
    Forbidden,

    /// <summary>The account's request budget (primary or secondary rate limit) is used up for now.</summary>
    RateLimited,

    /// <summary>404: the registry (or a file it should have) isn't there.</summary>
    NotFound,

    /// <summary>422: GitHub refused what was sent (its message says why).</summary>
    Rejected,

    /// <summary>409: the repository is in a state that refuses the change (an empty fork, a conflict).</summary>
    Conflict,

    /// <summary>GitHub couldn't be reached, didn't answer in time, or answered 5xx.</summary>
    Network,

    /// <summary>The pack fails the registry's own checks (<see cref="Packs.PackValidator"/>) and would be refused by CI.</summary>
    Invalid,

    /// <summary>The game's publisher opted out of fan translations.</summary>
    OptedOut,

    /// <summary>The pack to make a new revision of isn't in the registry (removed, or never merged).</summary>
    TargetGone,

    /// <summary>The fork didn't become ready in time.</summary>
    ForkNotReady,

    /// <summary>An answer the app can't use, or a local file that can't be read.</summary>
    Unexpected,
}

/// <summary>A failed GitHub call: the kind, HTTP status (0 for no answer), GitHub's own message, and when to retry (rate limit).</summary>
public sealed record GitHubApiError(PublishProblem Problem, int Status, string Message, DateTimeOffset? RetryAt = null);

/// <summary>Thrown inside the publish client for a failed GitHub call; <see cref="PackPublisher"/> turns it into a result.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException()
    {
        Error = new GitHubApiError(PublishProblem.Unexpected, 0, "GitHub call failed.");
    }

    public GitHubApiException(string message)
        : base(message)
    {
        Error = new GitHubApiError(PublishProblem.Unexpected, 0, message);
    }

    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = new GitHubApiError(PublishProblem.Unexpected, 0, message);
    }

    public GitHubApiException(GitHubApiError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public GitHubApiError Error { get; }
}

/// <summary>
/// The GitHub REST calls publishing makes (task 60), with the conventions of <see cref="GitHubUserClient.CreateRequest"/>.
/// JSON in, JSON out (<see cref="JsonElement"/> clones); a failed call throws <see cref="GitHubApiException"/> with a
/// typed <see cref="GitHubApiError"/>: 401 → <see cref="PublishProblem.Unauthorized"/>; 403 / 429 with
/// <c>x-ratelimit-remaining: 0</c> (reset from <c>x-ratelimit-reset</c>), a <c>retry-after</c> header or a "rate limit"
/// message → <see cref="PublishProblem.RateLimited"/>; other 403 → <see cref="PublishProblem.Forbidden"/>; 404, 409,
/// 422 (GitHub's message and its <c>errors[].message</c>), 5xx / 408 / no answer → <see cref="PublishProblem.Network"/>.
/// Cancellation is never turned into an error.
/// </summary>
public sealed class GitHubRestClient(HttpClient http, GitHubOAuthApp app, string token, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public GitHubOAuthApp App => app;

    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, Api(path), null, cancellationToken);

    /// <summary>GET that answers <c>null</c> for 404 (and for 409 "Git Repository is empty" when <paramref name="emptyIsMissing"/>).</summary>
    public async Task<JsonElement?> TryGetAsync(string path, CancellationToken cancellationToken, bool emptyIsMissing = false)
    {
        try
        {
            return await GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException e) when (e.Error.Problem == PublishProblem.NotFound || (emptyIsMissing && e.Error.Problem == PublishProblem.Conflict))
        {
            return null;
        }
    }

    /// <summary>A file's bytes through the contents API (<c>application/vnd.github.raw+json</c>), or <c>null</c> for 404.</summary>
    public async Task<byte[]?> TryGetRawAsync(string path, CancellationToken cancellationToken)
    {
        using var request = GitHubUserClient.CreateRequest(HttpMethod.Get, Api(path), token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        var (status, body, headers) = await RoundTripAsync(request, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        return IsSuccess(status) ? body : throw new GitHubApiException(ErrorOf(status, body, headers));
    }

    public Task<JsonElement> PostAsync(string path, JsonObject body, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, Api(path), Json(body), cancellationToken);

    public Task<JsonElement> PatchAsync(string path, JsonObject body, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, Api(path), Json(body), cancellationToken);

    public Task<JsonElement> DeleteAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, Api(path), null, cancellationToken);

    /// <summary>POSTs raw bytes to an absolute URL (a Release asset upload to <c>uploads.github.com</c>).</summary>
    public Task<JsonElement> UploadAsync(Uri url, byte[] bytes, string mediaType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return SendAsync(HttpMethod.Post, url, content, cancellationToken);
    }

    private Uri Api(string path) => new(app.Api, path);

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private async Task<JsonElement> SendAsync(HttpMethod method, Uri url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = GitHubUserClient.CreateRequest(method, url, token);
        request.Content = content;
        var (status, body, headers) = await RoundTripAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            throw new GitHubApiException(ErrorOf(status, body, headers));
        }

        if (body.Length == 0)
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new GitHubApiException(new GitHubApiError(PublishProblem.Unexpected, (int)status, $"GitHub's answer to {method} {url.AbsolutePath} isn't JSON."), e);
        }
    }

    private async Task<(HttpStatusCode Status, byte[] Body, HttpResponseHeaders Headers)> RoundTripAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body, response.Headers);
        }
        catch (HttpRequestException e)
        {
            throw new GitHubApiException(new GitHubApiError(PublishProblem.Network, 0, $"Can't reach {request.RequestUri?.Host}: {e.Message}"), e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(new GitHubApiError(PublishProblem.Network, 0, $"{request.RequestUri?.Host} didn't answer in time."), e);
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private GitHubApiError ErrorOf(HttpStatusCode status, byte[] body, HttpResponseHeaders headers)
    {
        var code = (int)status;
        var message = MessageOf(body) ?? $"{code} {status}";
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && RetryAt(headers, message) is var (limited, retryAt) && limited)
        {
            return new GitHubApiError(PublishProblem.RateLimited, code, message, retryAt);
        }

        var problem = status switch
        {
            HttpStatusCode.Unauthorized => PublishProblem.Unauthorized,
            HttpStatusCode.Forbidden => PublishProblem.Forbidden,
            HttpStatusCode.NotFound => PublishProblem.NotFound,
            HttpStatusCode.Conflict => PublishProblem.Conflict,
            HttpStatusCode.UnprocessableEntity => PublishProblem.Rejected,
            HttpStatusCode.TooManyRequests => PublishProblem.RateLimited,
            HttpStatusCode.RequestTimeout => PublishProblem.Network,
            _ when code >= 500 => PublishProblem.Network,
            _ => PublishProblem.Unexpected,
        };
        return new GitHubApiError(problem, code, message);
    }

    private (bool Limited, DateTimeOffset? RetryAt) RetryAt(HttpResponseHeaders headers, string message)
    {
        if (Header(headers, "retry-after") is { } after && int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return (true, _time.GetUtcNow().AddSeconds(seconds));
        }

        if (Header(headers, "x-ratelimit-remaining") == "0")
        {
            return (true, Header(headers, "x-ratelimit-reset") is { } reset && long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null);
        }

        return (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase), null);
    }

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    /// <summary>GitHub's <c>message</c> plus each <c>errors[].message</c> (422 details).</summary>
    private static string? MessageOf(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var parts = new List<string>();
            if (json.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && message.GetString() is { Length: > 0 } text)
            {
                parts.Add(text.TrimEnd('.'));
            }

            if (json.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: > 0 } more)
                    {
                        parts.Add(more.TrimEnd('.'));
                    }
                    else if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } plain)
                    {
                        parts.Add(plain.TrimEnd('.'));
                    }
                }
            }

            return parts.Count == 0 ? null : string.Join(": ", parts) + ".";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
