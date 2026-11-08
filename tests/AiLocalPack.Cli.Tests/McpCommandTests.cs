using System.Text.Json;
using AiLocalPack.Toolkit.Recipes.RpgMaker;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AiLocalPack.Cli.Tests;

public sealed class McpCommandTests
{
    [Fact]
    public async Task Server_over_stdio_extracts_the_fixture_strings()
    {
        // Arrange
        using var dir = new TempDir();
        RpgMakerFixture.Create(dir.Combine("game"));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = dotnet,
            Arguments = [Path.Combine(AppContext.BaseDirectory, "ailocalpack.dll"), "mcp", "--root", dir.Combine("game"), "--job", dir.Combine("job"), "--lang", "ru"],
            EnvironmentVariables = new Dictionary<string, string?> { ["AILOCALPACK_HOME"] = dir.Combine("home"), ["AILOCALPACK_TOOLKIT_ALL_ENGINES"] = "1" },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var result = await client.CallToolAsync("extract_strings", new Dictionary<string, object?> { ["limit"] = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.GetProperty("totalStrings").GetInt32().Should().Be(64);
    }

    [Fact]
    public async Task Missing_working_copy_exits_1_with_the_reason()
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var result = await CliRunner.RunAsync("mcp", "--root", dir.Combine("missing"), "--job", dir.Combine("job"));

        // Assert
        (result.ExitCode, result.StdOut, result.StdErr.Trim()).Should().Be((1, "", $"Folder not found: {dir.Combine("missing")}"));
    }

    [Fact]
    public async Task Unknown_language_exits_1()
    {
        // Arrange
        using var dir = new TempDir().Dir("game");

        // Act
        var result = await CliRunner.RunAsync("mcp", "--root", dir.Combine("game"), "--job", dir.Combine("job"), "--lang", "xx");

        // Assert
        (result.ExitCode, result.StdErr.Trim()).Should().Be((1, "'xx' is not a supported language tag (e.g. en, ru, pt-BR)."));
    }
}
