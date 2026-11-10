using AiLocalPack.Core.Background;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Background;

/// <summary>Start with the system (task 39): the entry per OS, written and removed as the setting says.</summary>
public sealed class AutostartTests
{
    private static readonly AutostartCommand Command = new("/Apps/ai local pack/AiLocalPack", [AutostartCommand.BackgroundArgument]);

    private static OsEnvironment Env(OsKind os, string home, string? xdgConfig = null) =>
        new(os, home, name => name == "XDG_CONFIG_HOME" ? xdgConfig : null);

    [Fact]
    public void Windows_entry_is_a_run_value_with_the_quoted_executable()
    {
        // Arrange
        var registry = new FakeRegistry();
        var autostart = Autostart.For(Env(OsKind.Windows, @"C:\Users\p"), registry);

        // Act
        Autostart.Sync(autostart, enabled: true, new AutostartCommand(@"C:\Program Files\ailocalpack\AiLocalPack.exe", ["--background"]));

        // Assert
        registry.Values.Should().Equal(new Dictionary<string, string>
        {
            [@"Software\Microsoft\Windows\CurrentVersion\Run\ailocalpack"] = "\"C:\\Program Files\\ailocalpack\\AiLocalPack.exe\" --background",
        });
    }

    [Fact]
    public void Windows_arguments_with_spaces_and_quotes_are_quoted_as_argv_splits_them()
    {
        // Arrange
        var autostart = new WindowsAutostart(new FakeRegistry());

        // Act
        var line = autostart.Render(new AutostartCommand(@"C:\dotnet\dotnet.exe", [@"C:\My Apps\app.dll", "say \"hi\"", @"C:\dir with space\"]));

        // Assert
        line.Should().Be("\"C:\\dotnet\\dotnet.exe\" \"C:\\My Apps\\app.dll\" \"say \\\"hi\\\"\" \"C:\\dir with space\\\\\"");
    }

    [Fact]
    public void Windows_entry_is_removed_when_the_setting_is_off()
    {
        // Arrange
        var registry = new FakeRegistry();
        var autostart = new WindowsAutostart(registry);
        Autostart.Sync(autostart, enabled: true, Command);

        // Act
        var change = Autostart.Sync(autostart, enabled: false, Command);

        // Assert
        (change, registry.Values.Count).Should().Be((AutostartChange.Removed, 0));
    }

    [Fact]
    public void Windows_registry_refusing_the_write_throws()
    {
        // Arrange
        var autostart = new WindowsAutostart(new FakeRegistry { Refuse = true });

        // Act
        var act = () => Autostart.Sync(autostart, enabled: true, Command);

        // Assert
        act.Should().Throw<IOException>().WithMessage("*Run\\ailocalpack*");
    }

    [Fact]
    public void Mac_entry_is_a_launch_agent_run_at_load()
    {
        // Arrange
        using var home = new TempDir();
        var autostart = Autostart.For(Env(OsKind.MacOS, home.Path), new FakeRegistry());

        // Act
        Autostart.Sync(autostart, enabled: true, new AutostartCommand("/Applications/a&b.app/Contents/MacOS/AiLocalPack", ["--background"]));

        // Assert
        File.ReadAllText(home.Combine("Library/LaunchAgents/io.github.ailocalpack.plist")).Should().Be(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>io.github.ailocalpack</string>
            	<key>ProgramArguments</key>
            	<array>
            		<string>/Applications/a&amp;b.app/Contents/MacOS/AiLocalPack</string>
            		<string>--background</string>
            	</array>
            	<key>RunAtLoad</key>
            	<true/>
            	<key>ProcessType</key>
            	<string>Interactive</string>
            </dict>
            </plist>

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Mac_entry_is_deleted_when_the_setting_is_off()
    {
        // Arrange
        using var home = new TempDir();
        var autostart = new MacAutostart(Env(OsKind.MacOS, home.Path));
        Autostart.Sync(autostart, enabled: true, Command);

        // Act
        Autostart.Sync(autostart, enabled: false, Command);

        // Assert
        File.Exists(autostart.Location).Should().BeFalse();
    }

    [Fact]
    public void Linux_entry_is_an_xdg_autostart_desktop_file()
    {
        // Arrange
        using var home = new TempDir();
        var autostart = Autostart.For(Env(OsKind.Linux, home.Path), new FakeRegistry());

        // Act
        Autostart.Sync(autostart, enabled: true, new AutostartCommand("/home/deck/Apps/ailocalpack.AppImage", ["--background"]));

        // Assert
        File.ReadAllText(home.Combine(".config/autostart/ailocalpack.desktop")).Should().Be(
            "[Desktop Entry]\nType=Application\nName=AI Local Pack\nComment=Keeps game translations in place after game updates\n"
            + "Exec=/home/deck/Apps/ailocalpack.AppImage --background\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
    }

    [Fact]
    public void Linux_entry_follows_an_absolute_xdg_config_home()
    {
        // Arrange
        using var home = new TempDir();
        var config = home.Combine("cfg");

        // Act
        var autostart = new LinuxAutostart(Env(OsKind.Linux, home.Path, config));

        // Assert
        autostart.Location.Should().Be(Path.Combine(config, "autostart", "ailocalpack.desktop"));
    }

    [Fact]
    public void Linux_entry_ignores_a_relative_xdg_config_home()
    {
        // Arrange
        using var home = new TempDir();

        // Act
        var autostart = new LinuxAutostart(Env(OsKind.Linux, home.Path, "relative/cfg"));

        // Assert
        autostart.Location.Should().Be(Path.Combine(home.Path, ".config", "autostart", "ailocalpack.desktop"));
    }

    [Fact]
    public void Linux_exec_quotes_reserved_characters_and_escapes_as_the_spec_says()
    {
        // Arrange
        var autostart = new LinuxAutostart(Env(OsKind.Linux, "/home/p"));

        // Act
        var entry = autostart.Render(new AutostartCommand("/home/p/My Apps/ai$pack", ["100%", "a\\b"]));

        // Assert
        entry.Should().Contain("Exec=\"/home/p/My Apps/ai\\\\$pack\" 100%% \"a\\\\\\\\b\"\n");
    }

    [Fact]
    public void Sync_leaves_an_identical_entry_alone()
    {
        // Arrange
        using var home = new TempDir();
        var autostart = new LinuxAutostart(Env(OsKind.Linux, home.Path));
        Autostart.Sync(autostart, enabled: true, Command);

        // Act
        var change = Autostart.Sync(autostart, enabled: true, Command);

        // Assert
        change.Should().Be(AutostartChange.None);
    }

    [Fact]
    public void Sync_rewrites_an_entry_pointing_at_a_moved_app()
    {
        // Arrange
        using var home = new TempDir();
        var autostart = new LinuxAutostart(Env(OsKind.Linux, home.Path));
        Autostart.Sync(autostart, enabled: true, new AutostartCommand("/old/ailocalpack", ["--background"]));

        // Act
        var change = Autostart.Sync(autostart, enabled: true, Command);

        // Assert
        (change, autostart.Read()).Should().Be((AutostartChange.Written, autostart.Render(Command)));
    }

    [Fact]
    public void Sync_with_the_setting_off_and_no_entry_does_nothing()
    {
        // Arrange
        using var home = new TempDir();
        var autostart = new LinuxAutostart(Env(OsKind.Linux, home.Path));

        // Act
        var change = Autostart.Sync(autostart, enabled: false, Command);

        // Assert
        change.Should().Be(AutostartChange.None);
    }

    private sealed class FakeRegistry : IWindowsRegistryWriter
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool Refuse { get; init; }

        public string? GetCurrentUserString(string keyPath, string valueName) => Values.GetValueOrDefault(keyPath + "\\" + valueName);

        public bool SetCurrentUserString(string keyPath, string valueName, string value)
        {
            if (Refuse)
            {
                return false;
            }

            Values[keyPath + "\\" + valueName] = value;
            return true;
        }

        public bool DeleteCurrentUserValue(string keyPath, string valueName) => !Refuse && (Values.Remove(keyPath + "\\" + valueName) || true);
    }
}
