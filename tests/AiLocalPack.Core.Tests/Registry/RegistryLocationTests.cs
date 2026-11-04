using AiLocalPack.Core.Games;
using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Registry;

public sealed class RegistryLocationTests
{
    private static OsEnvironment Env(string? indexUrl = null) =>
        new(OsKind.Linux, "/home/player", name => name == RegistryLocation.IndexUrlVariable ? indexUrl : null);

    private static RegistryLocation Parse(string text, string? baseDirectory = null)
    {
        RegistryLocation.TryParse(text, out var location, out var error, baseDirectory).Should().BeTrue(error);
        return location!;
    }

    [Fact]
    public void Without_overrides_the_public_registry_is_used()
    {
        // Act
        RegistryLocation.TryResolve(Env(), null, out var location, out _);

        // Assert
        location!.IndexUri.Should().Be(new Uri("https://ailocalpack.github.io/registry/index.json"));
    }

    [Fact]
    public void Environment_variable_wins_over_the_setting()
    {
        // Act
        RegistryLocation.TryResolve(Env("https://env.example/index.json"), "https://setting.example/index.json", out var location, out _);

        // Assert
        location!.IndexUri.Host.Should().Be("env.example");
    }

    [Fact]
    public void Setting_is_used_without_the_variable()
    {
        // Act
        RegistryLocation.TryResolve(Env(), "https://setting.example/index.json", out var location, out _);

        // Assert
        location!.IndexUri.Host.Should().Be("setting.example");
    }

    [Fact]
    public void Invalid_variable_is_an_error_naming_it()
    {
        // Act
        var ok = RegistryLocation.TryResolve(Env("ftp://example.com/index.json"), null, out _, out var error);

        // Assert
        (ok, error).Should().Be((false, "AILOCALPACK_INDEX_URL: 'ftp://example.com/index.json' must be an https:// URL or a local folder."));
    }

    [Fact]
    public void Https_folder_url_gets_index_json()
    {
        // Act
        var location = Parse("https://example.com/registry/");

        // Assert
        location.IndexUri.Should().Be(new Uri("https://example.com/registry/index.json"));
    }

    [Fact]
    public void Https_index_url_is_kept()
    {
        // Act
        var location = Parse("https://example.com/r/my-index.json");

        // Assert
        (location.IndexUri.AbsoluteUri, location.IsLocal).Should().Be(("https://example.com/r/my-index.json", false));
    }

    [Fact]
    public void Plain_http_is_refused()
    {
        // Act
        var ok = RegistryLocation.TryParse("http://example.com/index.json", out _, out var error);

        // Assert
        (ok, error).Should().Be((false, "'http://example.com/index.json' must be an https:// URL or a local folder."));
    }

    [Fact]
    public void Empty_text_is_refused()
    {
        // Act
        var ok = RegistryLocation.TryParse("  ", out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void Local_folder_reads_its_index_json()
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var location = Parse(dir.Path);

        // Assert
        (location.IsLocal, location.LocalIndexPath).Should().Be((true, Path.Combine(dir.Path, "index.json")));
    }

    [Fact]
    public void Relative_folder_is_resolved_against_the_base_folder()
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var location = Parse(Path.Combine("tests", "registry"), dir.Path);

        // Assert
        location.LocalIndexPath.Should().Be(Path.Combine(dir.Path, "tests", "registry", "index.json"));
    }

    [Fact]
    public void Local_index_file_is_used_as_is()
    {
        // Arrange
        using var dir = new TempDir();
        dir.File("other.json", "{}");

        // Act
        var location = Parse(dir.Combine("other.json"));

        // Assert
        location.LocalIndexPath.Should().Be(dir.Combine("other.json"));
    }

    [Fact]
    public void File_url_is_a_local_folder()
    {
        // Arrange
        using var dir = new TempDir();

        // Act
        var location = Parse(new Uri(dir.Path).AbsoluteUri);

        // Assert
        location.LocalIndexPath.Should().Be(Path.Combine(dir.Path, "index.json"));
    }

    [Fact]
    public void Manifest_path_resolves_next_to_a_remote_index()
    {
        // Arrange
        var location = Parse("https://example.com/registry/index.json");

        // Act
        var manifest = location.Resolve("games/steam-620/ru/full/manifest.json");

        // Assert
        manifest.Should().Be(new Uri("https://example.com/registry/games/steam-620/ru/full/manifest.json"));
    }

    [Fact]
    public void Manifest_path_resolves_inside_a_local_registry()
    {
        // Arrange
        using var dir = new TempDir();
        var location = Parse(dir.Path);

        // Act
        var manifest = location.Resolve("games/steam-620/ru/full/manifest.json");

        // Assert
        manifest.LocalPath.Should().Be(Path.Combine(dir.Path, "games", "steam-620", "ru", "full", "manifest.json"));
    }

    [Fact]
    public void Game_folder_replaces_the_colon()
    {
        // Act
        var folder = RegistryLayout.GameFolder(GameId.Steam(620));

        // Assert
        folder.Should().Be("steam-620");
    }

    [Fact]
    public void Game_folder_reads_back_as_its_game()
    {
        // Arrange
        var game = GameId.Fingerprint(new string('a', 64));

        // Act
        var read = RegistryLayout.GameOfFolder(RegistryLayout.GameFolder(game));

        // Assert
        read.Should().Be(game);
    }

    [Fact]
    public void Folder_that_is_not_a_game_reads_as_none()
    {
        // Act
        var read = RegistryLayout.GameOfFolder("steam-0620");

        // Assert
        read.Should().BeNull();
    }
}
