using AiLocalPack.Core.Background;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Packs;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Mcp.Tests;

public sealed class ToolkitServerLaunchTests
{
    [Fact]
    public void Command_runs_the_app_in_its_hidden_server_mode()
    {
        // Arrange
        var app = new AppLaunch("/usr/bin/dotnet", ["/app/AiLocalPack.Desktop.dll"]);

        // Act
        var (executable, arguments) = ToolkitServerLaunch.Command(app, "/work/game", "/work/job", "ru");

        // Assert
        new[] { executable }.Concat(arguments).Should().Equal(
            "/usr/bin/dotnet", "/app/AiLocalPack.Desktop.dll", "--mcp-server", "--root", Path.GetFullPath("/work/game"), "--job", Path.GetFullPath("/work/job"), "--lang", "ru");
    }

    [Fact]
    public void Is_requested_only_by_the_first_argument()
    {
        // Arrange
        string[] args = ["--background", "--mcp-server"];

        // Act
        var requested = ToolkitServerLaunch.IsRequested(args);

        // Assert
        requested.Should().BeFalse();
    }

    [Fact]
    public void Parse_reads_the_options_after_the_mode_argument()
    {
        // Arrange
        string[] args = ["--mcp-server", "--root", "/g", "--job", "/j", "--lang", "ru"];

        // Act
        var (options, _) = ToolkitServerLaunch.Parse(args, RecipeTools.Builtin);

        // Assert
        options.Should().Be(new ToolkitServerOptions("/g", "/j", "ru", RecipeTools.Builtin));
    }

    [Fact]
    public void Parse_requires_root_and_job()
    {
        // Arrange
        string[] args = ["--mcp-server", "--root", "/g"];

        // Act
        var (_, error) = ToolkitServerLaunch.Parse(args, RecipeTools.Builtin);

        // Assert
        error.Should().Be("--root <working copy> and --job <job folder> are required.");
    }

    [Fact]
    public void Parse_refuses_an_unknown_language()
    {
        // Arrange
        string[] args = ["--root", "/g", "--job", "/j", "--lang", "xx"];

        // Act
        var (_, error) = ToolkitServerLaunch.Parse(args, RecipeTools.Builtin);

        // Assert
        error.Should().Be("'xx' is not a supported language tag (e.g. en, ru, pt-BR).");
    }

    [Fact]
    public void Parse_refuses_an_unknown_option()
    {
        // Arrange
        string[] args = ["--root", "/g", "--job", "/j", "--net", "on"];

        // Act
        var (_, error) = ToolkitServerLaunch.Parse(args, RecipeTools.Builtin);

        // Assert
        error.Should().Be("Unknown option '--net'.");
    }

    [Fact]
    public async Task Run_refuses_a_missing_working_copy_with_exit_code_1()
    {
        // Arrange
        using var dir = new TempDir();
        var error = new StringWriter();

        // Act
        var code = await ToolkitServerLaunch.RunAsync(
            new ToolkitServerOptions(dir.Combine("missing"), dir.Combine("job"), "ru", RecipeTools.Builtin), error, TestContext.Current.CancellationToken);

        // Assert
        (code, error.ToString().Trim()).Should().Be((1, $"Folder not found: {dir.Combine("missing")}"));
    }

    [Fact]
    public void Session_refuses_a_job_folder_inside_the_working_copy()
    {
        // Arrange
        using var dir = new TempDir().Dir("game");

        // Act
        var act = () => new ToolkitSession(new ToolkitServerOptions(dir.Combine("game"), dir.Combine("game/job"), "ru", RecipeTools.Builtin));

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("The job folder must not be the game folder or inside it.*");
    }

    [Fact]
    public void Session_refuses_a_language_other_than_the_stored_strings()
    {
        // Arrange
        using var dir = new TempDir().Dir("game");
        new GenerationFiles(dir.Combine("job")).WriteStrings(new StringTable("de", new Dictionary<string, string> { ["k"] = "v" }));

        // Act
        var act = () => new ToolkitSession(new ToolkitServerOptions(dir.Combine("game"), dir.Combine("job"), "ru", RecipeTools.Builtin));

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("The job's strings.json is in 'de', not 'ru'.*");
    }
}
