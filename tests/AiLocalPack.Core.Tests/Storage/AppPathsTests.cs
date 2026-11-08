using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Storage;

public sealed class AppPathsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "user-home");

    private static OsEnvironment Env(OsKind os, params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return new OsEnvironment(os, Home, name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void Windows_uses_roaming_appdata_for_config_and_local_appdata_for_data_and_cache()
    {
        // Arrange
        var roaming = Path.Combine(Home, "Roaming");
        var local = Path.Combine(Home, "Local");
        var env = Env(OsKind.Windows, ("APPDATA", roaming), ("LOCALAPPDATA", local));

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(roaming, "ailocalpack"));
        paths.Data.Should().Be(Path.Combine(local, "ailocalpack"));
        paths.Cache.Should().Be(Path.Combine(local, "ailocalpack", "cache"));
    }

    [Fact]
    public void Windows_falls_back_to_the_profile_folders_when_appdata_variables_are_missing()
    {
        // Arrange
        var env = Env(OsKind.Windows);

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(Home, "AppData", "Roaming", "ailocalpack"));
        paths.Data.Should().Be(Path.Combine(Home, "AppData", "Local", "ailocalpack"));
    }

    [Fact]
    public void MacOS_uses_application_support_and_caches()
    {
        // Arrange
        var env = Env(OsKind.MacOS, ("XDG_CONFIG_HOME", Path.Combine(Home, "xdg")));

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        var support = Path.Combine(Home, "Library", "Application Support", "ailocalpack");
        paths.Config.Should().Be(support);
        paths.Data.Should().Be(support);
        paths.Cache.Should().Be(Path.Combine(Home, "Library", "Caches", "ailocalpack"));
    }

    [Fact]
    public void Linux_uses_xdg_variables_when_set()
    {
        // Arrange
        var config = Path.Combine(Home, "cfg");
        var data = Path.Combine(Home, "dat");
        var cache = Path.Combine(Home, "cch");
        var env = Env(OsKind.Linux, ("XDG_CONFIG_HOME", config), ("XDG_DATA_HOME", data), ("XDG_CACHE_HOME", cache));

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(config, "ailocalpack"));
        paths.Data.Should().Be(Path.Combine(data, "ailocalpack"));
        paths.Cache.Should().Be(Path.Combine(cache, "ailocalpack"));
    }

    [Fact]
    public void Linux_uses_xdg_defaults_when_variables_are_missing()
    {
        // Arrange
        var env = Env(OsKind.Linux);

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(Home, ".config", "ailocalpack"));
        paths.Data.Should().Be(Path.Combine(Home, ".local", "share", "ailocalpack"));
        paths.Cache.Should().Be(Path.Combine(Home, ".cache", "ailocalpack"));
    }

    [Theory]
    [InlineData("relative/config")]
    [InlineData("   ")]
    public void Linux_ignores_relative_or_blank_xdg_values(string value)
    {
        // Arrange
        var env = Env(OsKind.Linux, ("XDG_CONFIG_HOME", value));

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(Home, ".config", "ailocalpack"));
    }

    [Theory]
    [InlineData(OsKind.Windows)]
    [InlineData(OsKind.MacOS)]
    [InlineData(OsKind.Linux)]
    public void Ailocalpack_home_overrides_the_os_folders(OsKind os)
    {
        // Arrange
        var home = Path.Combine(Home, "portable");
        var env = Env(os, (AppPaths.HomeVariable, home), ("APPDATA", Path.Combine(Home, "a")), ("XDG_DATA_HOME", Path.Combine(Home, "x")));

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(home, "config"));
        paths.Data.Should().Be(Path.Combine(home, "data"));
        paths.Cache.Should().Be(Path.Combine(home, "cache"));
    }

    [Fact]
    public void A_relative_ailocalpack_home_is_made_absolute()
    {
        // Arrange
        var env = Env(OsKind.Linux, (AppPaths.HomeVariable, "dev-home"));

        // Act
        var paths = AppPaths.Resolve(env);

        // Assert
        paths.Config.Should().Be(Path.Combine(Environment.CurrentDirectory, "dev-home", "config"));
    }

    [Fact]
    public void Settings_logs_backups_and_working_copies_live_under_their_roots()
    {
        // Arrange
        var home = Path.Combine(Home, "portable");

        // Act
        var paths = AppPaths.UnderHome(home);

        // Assert
        paths.SettingsFile.Should().Be(Path.Combine(home, "config", "settings.json"));
        paths.Logs.Should().Be(Path.Combine(home, "data", "logs"));
        paths.Backups.Should().Be(Path.Combine(home, "data", "backups"));
        paths.WorkingCopies.Should().Be(Path.Combine(home, "data", "work"));
    }

    [Fact]
    public void Agents_the_app_installs_live_in_data()
    {
        // Arrange
        var home = Path.Combine(Home, "portable");

        // Act
        var paths = AppPaths.UnderHome(home);

        // Assert
        paths.Agents.Should().Be(Path.Combine(home, "data", "agents"));
    }

    [Fact]
    public void Resolving_creates_nothing_on_disk()
    {
        // Arrange
        using var dir = new TempDir();
        var env = Env(OsKind.Linux, (AppPaths.HomeVariable, dir.Combine("home")));

        // Act
        AppPaths.Resolve(env);

        // Assert
        Directory.Exists(dir.Combine("home")).Should().BeFalse();
    }
}
