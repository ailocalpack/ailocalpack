using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AiLocalPack.Cli.Tests;

/// <summary>
/// A local fake github.com for the CLI run as a child process (<c>AILOCALPACK_GITHUB_URL</c>): the device-code and token
/// endpoints (pending once, then a token, polled every second) and <c>GET /api/user</c>, which answers 401 when
/// <see cref="Revoked"/>; every other <c>/api/</c> call goes to <see cref="Api"/> (the publish client's REST calls,
/// task 60).
/// </summary>
internal sealed class FakeGitHubServer : IDisposable
{
    public const string Token = "gho_fake_token";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private int _polls;

    public FakeGitHubServer()
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        Api = new FakeGitHubApi(new Uri(Url + "api/"));
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Url { get; }

    /// <summary>The REST API behind <c>/api/</c> (except <c>/api/user</c>).</summary>
    public FakeGitHubApi Api { get; }

    /// <summary>Answers the device-code request with <c>device_flow_disabled</c>.</summary>
    public bool DeviceFlowDisabled { get; set; }

    /// <summary><c>GET /api/user</c> answers 401 (the token was revoked on GitHub).</summary>
    public bool Revoked { get; set; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
        Api.Dispose();
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (context.Request.Url!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) && context.Request.Url.AbsolutePath != "/api/user")
            {
                await ForwardAsync(context);
                continue;
            }

            var (status, body) = Answer(context.Request);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            if (context.Request.Url!.AbsolutePath == "/api/user")
            {
                context.Response.Headers.Add("X-OAuth-Scopes", "public_repo");
            }

            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        var incoming = context.Request;
        using var request = new HttpRequestMessage(new HttpMethod(incoming.HttpMethod), incoming.Url);
        if (incoming.HasEntityBody)
        {
            using var buffer = new MemoryStream();
            await incoming.InputStream.CopyToAsync(buffer);
            request.Content = new ByteArrayContent(buffer.ToArray());
        }

        foreach (var name in new[] { "Authorization", "Accept" })
        {
            if (incoming.Headers[name] is { } value)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await Api.HandleAsync(request, _stop.Token);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        foreach (var header in response.Headers)
        {
            context.Response.Headers.Add(header.Key, string.Join(",", header.Value));
        }

        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private (HttpStatusCode, string) Answer(HttpListenerRequest request) => request.Url!.AbsolutePath switch
    {
        "/login/device/code" when DeviceFlowDisabled => (HttpStatusCode.BadRequest, """{"error":"device_flow_disabled","error_description":"Device Flow must be explicitly enabled for this App"}"""),
        "/login/device/code" => (HttpStatusCode.OK, """{"device_code":"dev-1","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}"""),
        "/login/oauth/access_token" => Interlocked.Increment(ref _polls) == 1
            ? (HttpStatusCode.OK, """{"error":"authorization_pending"}""")
            : (HttpStatusCode.OK, $$"""{"access_token":"{{Token}}","token_type":"bearer","scope":"public_repo"}"""),
        "/api/user" when Revoked || request.Headers["Authorization"] != $"Bearer {Token}" => (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""),
        "/api/user" => (HttpStatusCode.OK, """{"login":"octo","name":"Octo Cat","avatar_url":"https://avatars.githubusercontent.com/u/1","html_url":"https://github.com/octo"}"""),
        _ => (HttpStatusCode.NotFound, """{"message":"Not Found"}"""),
    };
}
