using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Steam;

public sealed class SteamLocatorTests : IDisposable
{
    private readonly TempDir _home = new();

    /// <summary>Directory symlinks on Windows need admin rights or developer mode.</summary>
    public static bool HostIsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _home.Dispose();

    private SteamLocator Locator(OsKind os, string? registrySteamPath = null, params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        var env = new OsEnvironment(os, _home.Path, name => map.GetValueOrDefault(name));
        return new SteamLocator(env, new FakeRegistry(registrySteamPath));
    }

    /// <summary>Creates a Steam root (a folder with <c>steamapps/</c>) at <paramref name="relativePath"/> under the fake home.</summary>
    private string SteamAt(string relativePath)
    {
        _home.Dir($"{relativePath}/steamapps");
        return _home.Combine(relativePath);
    }

    [Fact]
    public void MacOS_finds_steam_in_application_support()
    {
        // Arrange
        var steam = SteamAt("Library/Application Support/Steam");

        // Act
        var roots = Locator(OsKind.MacOS).FindRoots();

        // Assert
        roots.Should().Equal(steam);
    }

    [Fact]
    public void Windows_uses_the_steam_path_from_the_registry()
    {
        // Arrange
        var steam = SteamAt("custom/Steam");

        // Act
        var roots = Locator(OsKind.Windows, registrySteamPath: steam).FindRoots();

        // Assert
        roots.Should().Equal(steam);
    }

    [Fact]
    public void Windows_falls_back_to_program_files_x86()
    {
        // Arrange
        var steam = SteamAt("Program Files (x86)/Steam");

        // Act
        var roots = Locator(OsKind.Windows, null, ("ProgramFiles(x86)", _home.Combine("Program Files (x86)"))).FindRoots();

        // Assert
        roots.Should().Equal(steam);
    }

    [Fact]
    public void Windows_lists_the_registry_path_once_when_it_is_also_the_default_folder()
    {
        // Arrange
        var steam = SteamAt("Program Files (x86)/Steam");
        var registryPath = steam.Replace('\\', '/') + "/";

        // Act
        var roots = Locator(OsKind.Windows, registryPath, ("ProgramFiles(x86)", _home.Combine("Program Files (x86)"))).FindRoots();

        // Assert
        roots.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/Steam")]
    public void Windows_ignores_an_empty_or_relative_registry_value(string registryPath)
    {
        // Arrange
        SteamAt("relative/Steam");

        // Act
        var roots = Locator(OsKind.Windows, registryPath, ("ProgramFiles(x86)", _home.Combine("none"))).FindRoots();

        // Assert
        roots.Should().BeEmpty();
    }

    [Theory]
    [InlineData(".local/share/Steam")]
    [InlineData(".steam/steam")]
    [InlineData(".steam/root")]
    [InlineData(".steam/debian-installation")]
    [InlineData(".var/app/com.valvesoftware.Steam/.local/share/Steam")]
    [InlineData("snap/steam/common/.local/share/Steam")]
    public void Linux_finds_steam_in_each_known_location(string relativePath)
    {
        // Arrange
        var steam = SteamAt(relativePath);

        // Act
        var roots = Locator(OsKind.Linux).FindRoots();

        // Assert
        roots.Should().Equal(steam);
    }

    [Fact]
    public void Linux_honours_xdg_data_home()
    {
        // Arrange
        var steam = SteamAt("xdg-data/Steam");

        // Act
        var roots = Locator(OsKind.Linux, null, ("XDG_DATA_HOME", _home.Combine("xdg-data"))).FindRoots();

        // Assert
        roots.Should().Equal(steam);
    }

    [Fact]
    public void Linux_reports_a_native_and_a_flatpak_install_in_priority_order()
    {
        // Arrange
        var flatpak = SteamAt(".var/app/com.valvesoftware.Steam/.local/share/Steam");
        var native = SteamAt(".local/share/Steam");

        // Act
        var roots = Locator(OsKind.Linux).FindRoots();

        // Assert
        roots.Should().Equal(native, flatpak);
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Directory symlinks need admin rights on Windows")]
    public void Linux_symlinks_to_the_same_install_collapse_to_one()
    {
        // Arrange
        var native = SteamAt(".local/share/Steam");
        _home.Dir(".steam");
        Directory.CreateSymbolicLink(_home.Combine(".steam/steam"), native);
        Directory.CreateSymbolicLink(_home.Combine(".steam/root"), _home.Combine(".steam/steam"));

        // Act
        var roots = Locator(OsKind.Linux).FindRoots();

        // Assert
        roots.Should().Equal(native);
    }

    [Fact(SkipWhen = nameof(HostIsWindows), Skip = "Directory symlinks need admin rights on Windows")]
    public void Linux_symlinked_parent_folders_are_resolved_too()
    {
        // Arrange
        var real = SteamAt("disk/Steam");
        _home.Dir(".local");
        Directory.CreateSymbolicLink(_home.Combine(".local/share"), _home.Combine("disk"));
        _home.Dir(".steam");
        Directory.CreateSymbolicLink(_home.Combine(".steam/steam"), real);

        // Act
        var roots = Locator(OsKind.Linux).FindRoots();

        // Assert
        roots.Should().Equal(_home.Combine(".local/share/Steam"));
    }

    [Fact]
    public void A_folder_without_steamapps_is_not_a_steam_root()
    {
        // Arrange
        _home.Dir(".local/share/Steam/ubuntu12_32");

        // Act
        var roots = Locator(OsKind.Linux).FindRoots();

        // Assert
        roots.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OsKind.Windows)]
    [InlineData(OsKind.MacOS)]
    [InlineData(OsKind.Linux)]
    public void Nothing_installed_gives_an_empty_list(OsKind os)
    {
        // Arrange
        var locator = Locator(os, null, ("ProgramFiles(x86)", _home.Combine("Program Files (x86)")));

        // Act
        var roots = locator.FindRoots();

        // Assert
        roots.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_home_folder_gives_an_empty_list()
    {
        // Arrange
        var env = new OsEnvironment(OsKind.Linux, _home.Combine("gone"), _ => null);
        var locator = new SteamLocator(env, new FakeRegistry(null));

        // Act
        var roots = locator.FindRoots();

        // Assert
        roots.Should().BeEmpty();
    }

    [Fact]
    public void The_override_variable_replaces_discovery()
    {
        // Arrange
        SteamAt(".local/share/Steam");
        var fixture = SteamAt("fixture/Steam");

        // Act
        var roots = Locator(OsKind.Linux, null, (SteamLocator.RootVariable, fixture)).FindRoots();

        // Assert
        roots.Should().Equal(fixture);
    }

    [Fact]
    public void An_override_that_is_not_a_steam_root_gives_an_empty_list()
    {
        // Arrange
        SteamAt(".local/share/Steam");

        // Act
        var roots = Locator(OsKind.Linux, null, (SteamLocator.RootVariable, _home.Combine("nothing-here"))).FindRoots();

        // Assert
        roots.Should().BeEmpty();
    }

    [Fact]
    public void The_real_registry_gives_null_for_a_missing_key()
    {
        // Arrange
        var registry = WindowsRegistry.Instance;

        // Act
        var value = registry.GetCurrentUserString(@"Software\ailocalpack-tests\missing", "Value");

        // Assert
        value.Should().BeNull();
    }

    private sealed class FakeRegistry(string? steamPath) : IWindowsRegistry
    {
        public string? GetCurrentUserString(string keyPath, string valueName) =>
            keyPath == @"Software\Valve\Steam" && valueName == "SteamPath" ? steamPath : null;
    }
}
