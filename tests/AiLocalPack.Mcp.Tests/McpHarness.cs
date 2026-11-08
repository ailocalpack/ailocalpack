using System.IO.Pipelines;
using System.Text.Json;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiLocalPack.Mcp.Tests;

/// <summary>
/// The toolkit server and an MCP client talking over in-memory pipes. The working copy is <c>&lt;temp&gt;/game</c>
/// (the RPG Maker MZ fixture unless told otherwise), the job folder <c>&lt;temp&gt;/job</c>; every engine is on.
/// </summary>
internal sealed class McpHarness : IAsyncDisposable
{
    private readonly Pipe clientToServer = new();
    private readonly Pipe serverToClient = new();
    private readonly CancellationTokenSource stop = new();
    private McpServer? server;
    private Task? serverRun;

    private McpHarness(TempDir dir)
    {
        Dir = dir;
    }

    public TempDir Dir { get; }

    public string GameDir => Dir.Combine("game");

    public string JobDir => Dir.Combine("job");

    public McpClient Client { get; private set; } = null!;

    public static async Task<McpHarness> StartAsync(string? language = "ru", Action<TempDir>? game = null)
    {
        var dir = new TempDir();
        if (game is null)
        {
            RpgMakerFixture.Create(dir.Combine("game"));
        }
        else
        {
            dir.Dir("game");
            game(dir);
        }

        var harness = new McpHarness(dir);
        await harness.ConnectAsync(language);
        return harness;
    }

    public async Task<CallToolResult> CallAsync(string tool, object? arguments = null)
    {
        var args = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments, ToolkitMcpServer.SerializerOptions))!;
        return await Client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Calls a tool that must succeed and returns its JSON result.</summary>
    public async Task<JsonElement> ResultAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        result.IsError.Should().NotBe(true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    /// <summary>Calls a tool that must fail and returns its message without the SDK's "An error occurred invoking 'x': " prefix.</summary>
    public async Task<string> ErrorAsync(string tool, object? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        result.IsError.Should().BeTrue(Text(result));
        var prefix = $"An error occurred invoking '{tool}': ";
        var text = Text(result);
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }

    public static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await stop.CancelAsync();
        if (serverRun is not null)
        {
            try
            {
                await serverRun;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }

        stop.Dispose();
        Dir.Dispose();
    }

    private async Task ConnectAsync(string? language)
    {
        var session = new ToolkitSession(new ToolkitServerOptions(GameDir, JobDir, language, RecipeTools.Builtin));
        var transport = new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        server = ToolkitMcpServer.Create(transport, session);
        serverRun = server.RunAsync(stop.Token);
        Client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
