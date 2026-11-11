using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiLocalPack.Core.GitHub;

/// <summary>The signed-in GitHub account.</summary>
/// <param name="Name">The display name, when the player set one.</param>
public sealed record GitHubUser(string Login, string? Name, Uri? AvatarUrl, Uri ProfileUrl);

/// <summary>The answer to <see cref="GitHubUserClient.GetUserAsync"/>.</summary>
public abstract record GitHubUserResult
{
    private GitHubUserResult()
    {
    }

    /// <param name="Scopes">From <c>X-OAuth-Scopes</c>: what the token may do.</param>
    public sealed record Found(GitHubUser User, IReadOnlyList<string> Scopes) : GitHubUserResult;

    /// <summary>401: the token was revoked on GitHub (or never valid).</summary>
    public sealed record Unauthorized : GitHubUserResult;

    /// <param name="IsNetwork">GitHub couldn't be reached (offline, timeout, 5xx), as opposed to an answer the app can't use.</param>
    public sealed record Failed(bool IsNetwork, string Detail) : GitHubUserResult;
}

/// <summary>
/// <c>GET /user</c> with a token: who is signed in. The REST conventions every GitHub call of the app follows: bearer
/// token, <c>application/vnd.github+json</c>, a pinned API version. Never throws except for cancellation.
/// </summary>
public sealed class GitHubUserClient(HttpClient http, GitHubOAuthApp app)
{
    public const string ApiVersion = "2022-11-28";

    public async Task<GitHubUserResult> GetUserAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using var request = CreateRequest(HttpMethod.Get, new Uri(app.Api, "user"), token);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new GitHubUserResult.Unauthorized();
            }

            if (!response.IsSuccessStatusCode)
            {
                var network = (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
                return new GitHubUserResult.Failed(network, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for the account.");
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (ReadUser(body) is not { } user)
            {
                return new GitHubUserResult.Failed(false, "GitHub's account answer has no login.");
            }

            var scopes = response.Headers.TryGetValues("X-OAuth-Scopes", out var values)
                ? values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
                : [];
            return new GitHubUserResult.Found(user, scopes);
        }
        catch (HttpRequestException e)
        {
            return new GitHubUserResult.Failed(true, $"Can't reach {app.Api.Host}: {e.Message}");
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitHubUserResult.Failed(true, $"{app.Api.Host} didn't answer in time: {e.Message}");
        }
    }

    /// <summary>A REST request with the token (none: an anonymous read of public data) and the headers GitHub asks for.</summary>
    public static HttpRequestMessage CreateRequest(HttpMethod method, Uri url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private GitHubUser? ReadUser(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object || Text(json, "login") is not { Length: > 0 } login)
            {
                return null;
            }

            var profile = Url(json, "html_url") ?? new Uri(app.Web, Uri.EscapeDataString(login));
            return new GitHubUser(login, Text(json, "name") is { Length: > 0 } name ? name : null, Url(json, "avatar_url"), profile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? Url(JsonElement json, string name) =>
        Uri.TryCreate(Text(json, name), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.IsLoopback) ? url : null;
}
