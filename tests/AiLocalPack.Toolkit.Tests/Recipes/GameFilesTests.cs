using System.Text;
using AiLocalPack.Toolkit.Recipes;
using AiLocalPack.Toolkit.Recipes.RpgMaker;

namespace AiLocalPack.Toolkit.Tests.Recipes;

public sealed class GameFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Game => _dir.Combine("game");

    private IGameFiles Folder => GameFiles.Folder(Game);

    /// <summary>The game with a pack that replaced <c>data/b.json</c> (original in the backup) and added <c>data/added.json</c> and <c>tl/</c>.</summary>
    private PristineGameFiles Pristine()
    {
        _dir.File("game/data/a.json", "a")
            .File("game/data/b.json", "translated")
            .File("game/data/added.json", "added")
            .File("game/tl/ru/strings.json", "added")
            .File("backup/data/b.json", "original");
        return new PristineGameFiles(
            Folder,
            new Dictionary<string, string> { ["data/b.json"] = _dir.Combine("backup/data/b.json") },
            ["data/added.json", "tl/ru/strings.json"],
            ["tl", "tl/ru"]);
    }

    [Fact]
    public void A_folder_lists_its_files_by_relative_slash_paths_in_ordinal_order()
    {
        // Arrange
        _dir.File("game/data/b.json").File("game/data/a.json").File("game/data/sub/c.json");

        // Act
        var files = Folder.Files("data");

        // Assert
        files.Should().Equal("data/a.json", "data/b.json");
    }

    [Fact]
    public void A_recursive_listing_includes_the_files_of_subfolders()
    {
        // Arrange
        _dir.File("game/data/a.json").File("game/data/sub/c.json").File("game/top.txt");

        // Act
        var files = Folder.Files(string.Empty, recursive: true);

        // Assert
        files.Should().Equal("data/a.json", "data/sub/c.json", "top.txt");
    }

    [Fact]
    public void A_folder_lists_its_child_folders()
    {
        // Arrange
        _dir.File("game/www/data/a.json").File("game/www/js/main.js").File("game/www/index.html");

        // Act
        var folders = Folder.Directories("www");

        // Assert
        folders.Should().Equal("www/data", "www/js");
    }

    [Fact]
    public void Listing_a_missing_folder_throws()
    {
        // Arrange
        _dir.Dir("game");

        // Act
        var act = () => Folder.Files("missing");

        // Assert
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void A_file_is_read_by_its_relative_path()
    {
        // Arrange
        _dir.File("game/data/a.json", "content");

        // Act
        var bytes = Folder.ReadAllBytes("data/a.json");

        // Assert
        Encoding.UTF8.GetString(bytes).Should().Be("content");
    }

    [Fact]
    public void A_path_leaving_the_game_folder_is_refused()
    {
        // Arrange
        _dir.File("secret.txt").Dir("game");

        // Act
        var act = () => Folder.ReadAllBytes("../secret.txt");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_child_folder_is_found_whatever_its_case()
    {
        // Arrange
        _dir.File("game/www/Data/a.json");

        // Act
        var found = Folder.FindDirectory("www", "data");

        // Assert
        found.Should().Be("www/Data");
    }

    [Fact]
    public void The_pristine_game_reads_a_replaced_file_from_its_original()
    {
        // Arrange
        var game = Pristine();

        // Act
        var bytes = game.ReadAllBytes("data/b.json");

        // Assert
        Encoding.UTF8.GetString(bytes).Should().Be("original");
    }

    [Fact]
    public void The_pristine_game_reads_other_files_from_the_game_folder()
    {
        // Arrange
        var game = Pristine();

        // Act
        var bytes = game.ReadAllBytes("data/a.json");

        // Assert
        Encoding.UTF8.GetString(bytes).Should().Be("a");
    }

    [Fact]
    public void The_pristine_game_has_no_file_the_pack_added()
    {
        // Arrange
        var game = Pristine();

        // Act
        var exists = game.FileExists("data/added.json");

        // Assert
        exists.Should().BeFalse();
    }

    [Fact]
    public void The_pristine_game_lists_replaced_files_and_hides_added_ones()
    {
        // Arrange
        var game = Pristine();

        // Act
        var files = game.Files(string.Empty, recursive: true);

        // Assert
        files.Should().Equal("data/a.json", "data/b.json");
    }

    [Fact]
    public void The_pristine_game_has_no_folder_the_pack_created()
    {
        // Arrange
        var game = Pristine();

        // Act
        var folders = game.Directories(string.Empty);

        // Assert
        folders.Should().Equal("data");
    }

    [Fact]
    public void The_pristine_game_lists_an_original_whose_game_file_is_gone()
    {
        // Arrange
        var game = Pristine();
        File.Delete(_dir.Combine("game/data/b.json"));

        // Act
        var files = game.Files("data");

        // Assert
        files.Should().Equal("data/a.json", "data/b.json");
    }

    [Fact]
    public void A_recipe_extracts_the_originals_of_a_game_with_a_pack_installed()
    {
        // Arrange
        _dir.RpgMakerGame("game");
        var system = _dir.Combine("game/data/System.json");
        _dir.Dir("backup/data");
        File.Copy(system, _dir.Combine("backup/data/System.json"));
        File.WriteAllText(system, File.ReadAllText(system).Replace("Fixture Quest", "Квест", StringComparison.Ordinal));
        var game = new PristineGameFiles(Folder, new Dictionary<string, string> { ["data/System.json"] = _dir.Combine("backup/data/System.json") }, []);

        // Act
        var title = new RpgMakerRecipe().Extract(game, Ct).Strings.Single(s => s.Key == "data/System.json:/gameTitle").Original;

        // Assert
        title.Should().Be("Fixture Quest");
    }
}
