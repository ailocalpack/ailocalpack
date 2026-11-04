using System.Net;
using System.Text;

namespace AiLocalPack.TestKit;

/// <summary>A request seen by <see cref="FakeHttpHandler"/>; the body and headers are read before the request is disposed.</summary>
/// <param name="Headers">Request headers (name → values joined with ", "), case-insensitive names.</param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The header's value, or <c>null</c> when it was not sent.</summary>
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>
/// <see cref="HttpMessageHandler"/> that answers from configured routes (method + absolute URL) and never touches
/// the network. A request to an unknown route throws <see cref="InvalidOperationException"/>, so a test fails
/// loudly instead of silently getting a 404.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<(HttpMethod Method, Uri Uri), Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = [];
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _lock = new();

    /// <summary>Every request received, in order (unknown routes included).</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Answers <paramref name="method"/> <paramref name="url"/> with a response built per request.</summary>
    public FakeHttpHandler On(HttpMethod method, string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return OnAsync(method, url, (request, _) => Task.FromResult(respond(request)));
    }

    /// <summary>
    /// Answers <paramref name="method"/> <paramref name="url"/> asynchronously, with the request's cancellation token
    /// (a hanging server: <c>(_, ct) => Task.Delay(Timeout.Infinite, ct)…</c>).
    /// </summary>
    public FakeHttpHandler OnAsync(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        OnAsync(HttpMethod.Get, url, respond);

    public FakeHttpHandler OnAsync(HttpMethod method, string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(respond);
        lock (_lock)
        {
            _routes[(method, new Uri(url, UriKind.Absolute))] = respond;
        }

        return this;
    }

    /// <summary>Answers GET <paramref name="url"/> with a text body.</summary>
    public FakeHttpHandler OnGet(string url, string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
        On(HttpMethod.Get, url, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    /// <summary>Answers GET <paramref name="url"/> with a binary body.</summary>
    public FakeHttpHandler OnGet(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
        On(HttpMethod.Get, url, _ => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });

    /// <summary>Answers GET <paramref name="url"/> with an empty body and the given status.</summary>
    public FakeHttpHandler OnGet(string url, HttpStatusCode status) =>
        On(HttpMethod.Get, url, _ => new HttpResponseMessage(status));

    /// <summary>A client over this handler. The handler stays alive when the client is disposed.</summary>
    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond;
        string known;
        lock (_lock)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            _requests.Add(new RecordedRequest(request.Method, uri, body, headers));
            _routes.TryGetValue((request.Method, uri), out respond);
            known = respond is null ? string.Join(", ", _routes.Keys.Select(k => $"{k.Method} {k.Uri}")) : "";
        }

        if (respond is null)
        {
            throw new InvalidOperationException(
                $"No fake route for {request.Method} {uri}. Known routes: {(known.Length == 0 ? "(none)" : known)}.");
        }

        var response = await respond(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }
}
