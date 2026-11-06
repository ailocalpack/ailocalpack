using AiLocalPack.Core.Games;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Launch;

public sealed class GameLaunchPlannerTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "games", "Lantern");

    private static Game Steam(bool available = true) => new(
        GameId.Steam(1942280), "Brotato", GameSource.Steam, Folder, "/steam", null, null, null, null, available);

    private static Game Manual(bool available = true, string folder = "") => new(
        GameId.Fingerprint(new string('a', 64)), "Lantern", GameSource.Manual, folder.Length > 0 ? folder : Folder, null, null, null, null, null, available);

    private static string At(string relative) => Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private static LaunchCandidate Exe(string path, ExecutableFormat format) => new(path, format);

    [Theory]
    [InlineData(OsKind.Windows)]
    [InlineData(OsKind.MacOS)]
    [InlineData(OsKind.Linux)]
    public void Steam_game_starts_through_steam(OsKind os)
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Steam(), os, []);

        // Assert
        plan.Should().Be(new OpenUrlLaunch(new Uri("steam://rungameid/1942280")));
    }

    [Fact]
    public void Unavailable_steam_game_is_not_started()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Steam(available: false), OsKind.Windows, []);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.GameUnavailable));
    }

    [Fact]
    public void Unavailable_manual_game_is_not_started()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(available: false), OsKind.Windows, [Exe("Lantern.exe", ExecutableFormat.WindowsPe)]);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.GameUnavailable));
    }

    [Fact]
    public void Windows_exe_starts_in_its_folder_on_windows()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Windows, [Exe("bin/x64/Lantern.exe", ExecutableFormat.WindowsPe)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch(At("bin/x64/Lantern.exe"), [], At("bin/x64")));
    }

    [Fact]
    public void Linux_binary_starts_in_its_folder_on_linux()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Linux, [Exe("Lantern.x86_64", ExecutableFormat.LinuxElf)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch(At("Lantern.x86_64"), [], Folder));
    }

    [Theory]
    [InlineData(OsKind.Linux)]
    [InlineData(OsKind.MacOS)]
    public void Script_runs_through_sh(OsKind os)
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), os, [Exe("start.sh", ExecutableFormat.Script)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch("/bin/sh", [At("start.sh")], Folder));
    }

    [Fact]
    public void Bundle_opens_through_open_on_macos()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.MacOS, [Exe("Lantern.app", ExecutableFormat.MacBundle)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch("/usr/bin/open", [At("Lantern.app")], Folder));
    }

    [Fact]
    public void Binary_inside_a_bundle_opens_the_bundle_on_macos()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.MacOS, [Exe("Lantern.app/Contents/MacOS/Lantern", ExecutableFormat.MacMachO)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch("/usr/bin/open", [At("Lantern.app")], Folder));
    }

    [Fact]
    public void Game_folder_that_is_a_bundle_opens_itself_on_macos()
    {
        // Arrange
        var bundle = Path.Combine(Path.GetTempPath(), "games", "Lantern.app");

        // Act
        var plan = GameLaunchPlanner.Plan(Manual(folder: bundle), OsKind.MacOS, [Exe("Contents/MacOS/Lantern", ExecutableFormat.MacMachO)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch("/usr/bin/open", [bundle], bundle));
    }

    [Fact]
    public void Bare_binary_starts_directly_on_macos()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.MacOS, [Exe("Contents/MacOS/Lantern", ExecutableFormat.MacMachO)]);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch(At("Contents/MacOS/Lantern"), [], At("Contents/MacOS")));
    }

    [Fact]
    public void Main_executable_of_another_os_gives_way_to_one_this_os_runs()
    {
        // Arrange
        LaunchCandidate[] candidates = [Exe("Lantern.exe", ExecutableFormat.WindowsPe), Exe("Lantern.x86_64", ExecutableFormat.LinuxElf)];

        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Linux, candidates);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch(At("Lantern.x86_64"), [], Folder));
    }

    [Fact]
    public void Windows_only_build_on_linux_names_the_exe()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Linux, [Exe("Lantern.exe", ExecutableFormat.WindowsPe)]);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.WindowsOnly, "Lantern.exe"));
    }

    [Fact]
    public void Windows_only_build_on_macos_is_windows_only()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.MacOS, [Exe("Lantern.exe", ExecutableFormat.WindowsPe)]);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.WindowsOnly, "Lantern.exe"));
    }

    [Fact]
    public void Linux_only_build_on_windows_is_linux_only()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Windows, [Exe("start.sh", ExecutableFormat.Script), Exe("Lantern", ExecutableFormat.LinuxElf)]);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.LinuxOnly, "start.sh"));
    }

    [Fact]
    public void Mac_only_build_on_linux_is_mac_only()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Linux, [Exe("Lantern.app", ExecutableFormat.MacBundle)]);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.MacOnly, "Lantern.app"));
    }

    [Fact]
    public void Folder_without_executables_has_nothing_to_start()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Windows, []);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.NoExecutable));
    }

    [Fact]
    public void Unreadable_executables_are_skipped()
    {
        // Arrange
        LaunchCandidate[] candidates = [Exe("broken.exe", ExecutableFormat.Unknown), Exe("Lantern.exe", ExecutableFormat.WindowsPe)];

        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Windows, candidates);

        // Assert
        plan.Should().BeEquivalentTo(new ProcessLaunch(At("Lantern.exe"), [], Folder));
    }

    [Fact]
    public void Only_unreadable_executables_leave_nothing_to_start()
    {
        // Act
        var plan = GameLaunchPlanner.Plan(Manual(), OsKind.Windows, [Exe("broken.exe", ExecutableFormat.Unknown)]);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.NoExecutable));
    }
}
