using System.Net;
using System.Text;
using AiLocalPack.Core.GitHub;

namespace AiLocalPack.Core.Tests.GitHub;

/// <summary>A fake github.com for the GitHub tests: answers in order, recorded delays that move the clock.</summary>
internal static class GitHubFakes
{
    public static readonly GitHubOAuthApp App = new("test-client", new Uri("https://github.example/"), new Uri("https://api.github.example/"));

    public const string DeviceCodeUrl = "https://github.example/login/device/code";
    public const string TokenUrl = "https://github.example/login/oauth/access_token";
    public const string UserUrl = "https://api.github.example/user";

    public const string DeviceCodeJson =
        """{"device_code":"dev-123","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";

    public const string UserJson =
        """{"login":"octo","name":"Octo Cat","avatar_url":"https://avatars.example/u/1","html_url":"https://github.com/octo"}""";

    public static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string Error(string error, int? interval = null) =>
        interval is { } seconds ? $$"""{"error":"{{error}}","interval":{{seconds}}}""" : $$"""{"error":"{{error}}"}""";

    public static string Token(string token = "gho_secret", string scope = "public_repo") =>
        $$"""{"access_token":"{{token}}","token_type":"bearer","scope":"{{scope}}"}""";

    /// <summary>Answers <paramref name="url"/> (POST) with <paramref name="replies"/> one by one; the last repeats.</summary>
    public static FakeHttpHandler OnPostSequence(this FakeHttpHandler http, string url, params Func<HttpResponseMessage>[] replies)
    {
        var next = 0;
        return http.On(HttpMethod.Post, url, _ => replies[Math.Min(next++, replies.Length - 1)]());
    }

    public static HttpResponseMessage UserResponse(string body = UserJson, string scopes = "public_repo")
    {
        var response = Reply(body);
        response.Headers.Add("X-OAuth-Scopes", scopes);
        return response;
    }

    /// <summary>Delays that move <paramref name="clock"/> instead of waiting, and remember each wait.</summary>
    public sealed class RecordedDelays(FakeClock clock)
    {
        private readonly List<TimeSpan> _waits = [];

        public IReadOnlyList<TimeSpan> Waits => _waits;

        public Task Delay(TimeSpan span, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _waits.Add(span);
            clock.Advance(span);
            return Task.CompletedTask;
        }
    }
}
