using System.Net;
using System.Net.Http.Headers;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.GitHub;

/// <summary>
/// The app's GitHub OAuth App (in the <c>ailocalpack</c> organization, Device Flow on) and the addresses it talks to.
/// The client ID is public by design: Device Flow needs no client secret, so the app has none, which also means it
/// can't revoke a token itself (GitHub's revoke API needs the secret); signing out deletes the token here and points
/// to <see cref="ConnectionsPage"/>.
/// </summary>
/// <param name="ClientId">The OAuth App's client ID.</param>
/// <param name="Web">github.com: the device-code and token endpoints, profile pages.</param>
/// <param name="Api">The REST API (<c>https://api.github.com/</c>).</param>
public sealed record GitHubOAuthApp(string ClientId, Uri Web, Uri Api)
{
    /// <summary>The <c>ailocalpack</c> OAuth App.</summary>
    public const string DefaultClientId = "Ov23li3Id70x7uMXFEeK";

    /// <summary>Dev: another OAuth App's client ID (a test app with Device Flow on).</summary>
    public const string ClientIdVariable = "AILOCALPACK_GITHUB_CLIENT_ID";

    /// <summary>Dev / tests: a fake GitHub at this address (<c>&lt;url&gt;/login/...</c>; the API under <c>&lt;url&gt;/api/</c>).</summary>
    public const string UrlVariable = "AILOCALPACK_GITHUB_URL";

    /// <summary>
    /// <c>public_repo</c>: fork the registry, push a branch to the fork, open the PR, comment and react in its
    /// Discussions (tasks 60–63). It covers every public repository of the player, the narrowest classic OAuth scope
    /// that can push; nothing private is reachable.
    /// </summary>
    public const string Scopes = "public_repo";

    /// <summary>Where the player sees and revokes the app's access.</summary>
    public Uri ConnectionsPage => new(Web, $"settings/connections/applications/{Uri.EscapeDataString(ClientId)}");

    public Uri DeviceCodeEndpoint => new(Web, "login/device/code");

    public Uri TokenEndpoint => new(Web, "login/oauth/access_token");

    public static GitHubOAuthApp Default { get; } = new(DefaultClientId, new Uri("https://github.com/"), new Uri("https://api.github.com/"));

    /// <summary><see cref="Default"/> with <c>AILOCALPACK_GITHUB_CLIENT_ID</c> / <c>AILOCALPACK_GITHUB_URL</c> applied.</summary>
    public static GitHubOAuthApp Resolve(OsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var app = Default;
        if (env.GetVariable(ClientIdVariable) is { } clientId)
        {
            app = app with { ClientId = clientId.Trim() };
        }

        if (env.GetVariable(UrlVariable) is { } url && Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var web))
        {
            app = app with { Web = web, Api = new Uri(web, "api/") };
        }

        return app;
    }

    /// <summary>An HTTP client for GitHub: the product name as User-Agent (GitHub refuses requests without one), compressed responses.</summary>
    public static HttpClient CreateHttpClient(ProductInfoHeaderValue userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.Add(userAgent);
        return http;
    }
}
