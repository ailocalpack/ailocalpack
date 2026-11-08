using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiLocalPack.Mcp;

/// <summary>
/// The toolkit as an MCP server (<c>ailocalpack mcp --root &lt;working copy&gt; --job &lt;job folder&gt;</c>, stdio):
/// the tools of <see cref="ToolkitTools"/>, confined to one working copy of the game.
/// </summary>
public static class ToolkitMcpServer
{
    public const string ServerName = "ailocalpack-toolkit";

    /// <summary>What the agent is told about the server when it connects.</summary>
    public const string Instructions =
        "Tools of ailocalpack's toolkit for translating one game. Every path is relative to the game folder (a working copy; "
        + "the tools can't leave it). Start with detect_engine, then extract_strings / get_strings to read the strings (originals, "
        + "context, length limits, placeholders to keep exactly), put_translations to store translations (each is validated; "
        + "refused ones come back with the reason), put_glossary / get_glossary for names, terms and tone, validate_strings to "
        + "check, rebuild to write the game files from the stored translations into the job's build folder. Results are paged: "
        + "call again with offset = nextOffset until it is null.";

    /// <summary>Results and arguments: camelCase, nulls left out, non-ASCII unescaped.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(McpJsonUtilities.DefaultOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(McpJsonUtilities.DefaultOptions.TypeInfoResolver, new DefaultJsonTypeInfoResolver()),
    };

    /// <summary>The server's tools bound to <paramref name="session"/>.</summary>
    public static McpServerPrimitiveCollection<McpServerTool> CreateTools(ToolkitSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var target = new ToolkitTools(session);
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(ToolkitTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            tools.Add(McpServerTool.Create(method, target, new McpServerToolCreateOptions { SerializerOptions = SerializerOptions }));
        }

        return tools;
    }

    public static McpServer Create(ITransport transport, ToolkitSession session, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = typeof(ToolkitMcpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
            ServerInstructions = Instructions,
            ToolCollection = CreateTools(session),
        };
        return McpServer.Create(transport, options, loggerFactory);
    }

    /// <summary>Serves over standard input / output until the client disconnects or <paramref name="cancellationToken"/> fires.</summary>
    public static async Task RunStdioAsync(ToolkitSession session, CancellationToken cancellationToken)
    {
        await using var transport = new StdioServerTransport(ServerName);
        await using var server = Create(transport, session);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
