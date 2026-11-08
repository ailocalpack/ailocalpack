namespace AiLocalPack.Mcp.Tests;

public sealed class PathEscapeTests
{
    public static TheoryData<string> EscapingPaths => new()
    {
        "../job",
        "data/../../job",
        "data\\..\\..\\job",
        "/etc/hosts",
        "\\\\server\\share",
        "C:\\Windows",
        "C:relative",
    };

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task Read_file_snippet_refuses_a_path_out_of_the_working_copy(string path)
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var error = await mcp.ErrorAsync("read_file_snippet", new { path });

        // Assert
        error.Should().StartWith($"Path '{path}' refused");
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task List_files_refuses_a_path_out_of_the_working_copy(string path)
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync();

        // Act
        var error = await mcp.ErrorAsync("list_files", new { path });

        // Assert
        error.Should().StartWith($"Path '{path}' refused");
    }

    [Fact]
    public async Task Read_file_snippet_refuses_a_file_symlink_leading_out()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("secret.txt", "secret");
            File.CreateSymbolicLink(dir.Combine("game/link.txt"), dir.Combine("secret.txt"));
        });

        // Act
        var error = await mcp.ErrorAsync("read_file_snippet", new { path = "link.txt" });

        // Assert
        error.Should().Be("Path 'link.txt' refused: it leads out of the game folder through a link.");
    }

    [Fact]
    public async Task Read_file_snippet_refuses_a_path_through_a_folder_symlink_leading_out()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("outside/secret.txt", "secret");
            Directory.CreateSymbolicLink(dir.Combine("game/inner"), dir.Combine("outside"));
        });

        // Act
        var error = await mcp.ErrorAsync("read_file_snippet", new { path = "inner/secret.txt" });

        // Assert
        error.Should().Be("Path 'inner/secret.txt' refused: it leads out of the game folder through a link.");
    }

    [Fact]
    public async Task Read_file_snippet_refuses_a_dangling_symlink()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
            File.CreateSymbolicLink(dir.Combine("game/gone.txt"), dir.Combine("missing.txt")));

        // Act
        var error = await mcp.ErrorAsync("read_file_snippet", new { path = "gone.txt" });

        // Assert
        error.Should().Be("Path 'gone.txt' refused: it passes through a link whose target is missing.");
    }

    [Fact]
    public async Task Read_file_snippet_follows_a_symlink_staying_inside()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("game/real.txt", "inside");
            File.CreateSymbolicLink(dir.Combine("game/alias.txt"), dir.Combine("game/real.txt"));
        });

        // Act
        var result = await mcp.ResultAsync("read_file_snippet", new { path = "alias.txt" });

        // Assert
        result.GetProperty("content").GetString().Should().Be("1\tinside\n");
    }

    [Fact]
    public async Task List_files_lists_a_folder_symlink_without_descending_into_it()
    {
        // Arrange
        await using var mcp = await McpHarness.StartAsync(game: dir =>
        {
            dir.File("outside/secret.txt", "secret");
            Directory.CreateSymbolicLink(dir.Combine("game/inner"), dir.Combine("outside"));
        });

        // Act
        var result = await mcp.ResultAsync("list_files", new { recursive = true });

        // Assert
        result.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("path").GetString(), e.GetProperty("kind").GetString()))
            .Should().Equal(("inner", "link"));
    }
}
