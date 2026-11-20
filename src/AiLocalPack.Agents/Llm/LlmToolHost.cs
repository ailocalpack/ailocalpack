using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiLocalPack.Agents.Llm;

/// <summary>The outcome of one tool call: the text the model reads, and whether the tool failed.</summary>
public sealed record LlmToolOutput(string Text, bool IsError);

/// <summary>
/// The tools of one run of the player's own model: the run's MCP servers started and their tools listed (named
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, as Claude Code names them), calls passed on. Disposing stops the servers.
/// </summary>
public interface ILlmToolHost : IAsyncDisposable
{
    IReadOnlyList<LlmTool> Tools { get; }

    /// <summary>Calls <paramref name="name"/> with the model's JSON arguments. Never throws but for cancellation: a failure is an error output.</summary>
    Task<LlmToolOutput> CallAsync(string name, string arguments, CancellationToken cancellationToken);
}

/// <summary>Starts the tools of a run (behind an interface so the adapter is tested without processes).</summary>
public interface ILlmToolHostFactory
{
    /// <exception cref="LlmToolStartException">A server didn't start or didn't list its tools.</exception>
    Task<ILlmToolHost> StartAsync(IReadOnlyList<AgentMcpServer> servers, string workingDirectory, CancellationToken cancellationToken);
}

public sealed class LlmToolStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The real tools: each MCP server started over stdio with the official SDK's client.</summary>
public sealed class McpToolHostFactory : ILlmToolHostFactory
{
    public static McpToolHostFactory Instance { get; } = new();

    /// <summary>How long a tool call may take when its server names no timeout.</summary>
    public static TimeSpan DefaultToolTimeout { get; } = TimeSpan.FromMinutes(5);

    public async Task<ILlmToolHost> StartAsync(IReadOnlyList<AgentMcpServer> servers, string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var host = new McpToolHost();
        try
        {
            foreach (var server in servers)
            {
                await host.AddAsync(server, workingDirectory, cancellationToken).ConfigureAwait(false);
            }

            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class McpToolHost : ILlmToolHost
    {
        private readonly List<McpClient> _clients = [];
        private readonly List<LlmTool> _tools = [];
        private readonly Dictionary<string, (McpClient Client, string Tool, TimeSpan Timeout)> _routes = new(StringComparer.Ordinal);

        public IReadOnlyList<LlmTool> Tools => _tools;

        public async Task AddAsync(AgentMcpServer server, string workingDirectory, CancellationToken cancellationToken)
        {
            var options = new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = [.. server.Arguments],
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = server.Environment.ToDictionary(e => e.Key, e => (string?)e.Value),
            };
            McpClient client;
            IList<McpClientTool> tools;
            try
            {
                client = await McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new LlmToolStartException($"The tool server \"{server.Name}\" didn't start: {e.Message}", e);
            }

            _clients.Add(client);
            try
            {
                tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new LlmToolStartException($"The tool server \"{server.Name}\" didn't list its tools: {e.Message}", e);
            }

            foreach (var tool in tools)
            {
                var name = $"mcp__{server.Name}__{tool.ProtocolTool.Name}";
                _routes[name] = (client, tool.ProtocolTool.Name, server.ToolTimeout ?? DefaultToolTimeout);
                _tools.Add(new LlmTool(name, tool.ProtocolTool.Description, tool.ProtocolTool.InputSchema));
            }
        }

        public async Task<LlmToolOutput> CallAsync(string name, string arguments, CancellationToken cancellationToken)
        {
            if (!_routes.TryGetValue(name, out var route))
            {
                return new LlmToolOutput($"There is no tool \"{name}\". Use one of: {string.Join(", ", _routes.Keys)}.", true);
            }

            Dictionary<string, JsonElement> args;
            try
            {
                args = ParseArguments(arguments);
            }
            catch (JsonException e)
            {
                return new LlmToolOutput($"The arguments aren't a JSON object: {e.Message}", true);
            }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(route.Timeout);
            try
            {
                var result = await route.Client.CallToolAsync(new CallToolRequestParams { Name = route.Tool, Arguments = args }, timer.Token).ConfigureAwait(false);
                var text = new StringBuilder();
                foreach (var block in result.Content)
                {
                    if (block is TextContentBlock t)
                    {
                        text.Append(t.Text);
                    }
                }

                return new LlmToolOutput(text.Length == 0 && result.StructuredContent is { } structured ? structured.ToString() : text.ToString(), result.IsError == true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new LlmToolOutput($"The tool didn't finish within {route.Timeout.TotalMinutes:0} min.", true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new LlmToolOutput($"The tool failed: {e.Message}", true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
                {
                    // The server is gone either way.
                }
            }

            _clients.Clear();
        }
    }

    /// <summary>The model's arguments as a property map; empty text (a tool without arguments) is an empty map.</summary>
    /// <exception cref="JsonException">Not a JSON object.</exception>
    internal static Dictionary<string, JsonElement> ParseArguments(string arguments)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return map;
        }

        using var doc = JsonDocument.Parse(arguments);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Expected an object, found {doc.RootElement.ValueKind}.");
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }

        return map;
    }
}
