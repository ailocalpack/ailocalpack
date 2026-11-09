using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Launch;

/// <summary>Starting a working copy's program for the Build stage's test run (task 51): bundle binaries, Steam DRM.</summary>
public sealed class TestRunLaunchTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Pe(params string[] sections)
    {
        const int pe = 0x80;
        const int optional = 0xF0;
        var data = new byte[pe + 24 + optional + sections.Length * 40 + 16];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(pe).CopyTo(data, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(data, pe);
        BitConverter.GetBytes((ushort)sections.Length).CopyTo(data, pe + 6);
        BitConverter.GetBytes((ushort)optional).CopyTo(data, pe + 20);
        for (var i = 0; i < sections.Length; i++)
        {
            System.Text.Encoding.ASCII.GetBytes(sections[i]).CopyTo(data, pe + 24 + optional + i * 40);
        }

        return data;
    }

    [Fact]
    public void A_watchable_bundle_starts_its_own_binary()
    {
        // Arrange
        _dir.File("Lantern.app/Contents/Info.plist", "<plist><dict><key>CFBundleExecutable</key><string>Lantern</string></dict></plist>")
            .File("Lantern.app/Contents/MacOS/Lantern", "macho")
            .File("Lantern.app/Contents/MacOS/helper", "macho");

        // Act
        var plan = GameLaunchPlanner.PlanProgram(_dir.Path, OsKind.MacOS, [new LaunchCandidate("Lantern.app", ExecutableFormat.MacBundle)], watchable: true);

        // Assert
        ((ProcessLaunch)plan).FileName.Should().Be(_dir.Combine("Lantern.app/Contents/MacOS/Lantern"));
    }

    [Fact]
    public void A_bundle_without_a_binary_cant_be_watched()
    {
        // Arrange
        _dir.Dir("Lantern.app/Contents/MacOS");

        // Act
        var plan = GameLaunchPlanner.PlanProgram(_dir.Path, OsKind.MacOS, [new LaunchCandidate("Lantern.app", ExecutableFormat.MacBundle)], watchable: true);

        // Assert
        plan.Should().BeOfType<CannotLaunch>().Which.Problem.Should().Be(LaunchProblem.NoExecutable);
    }

    [Fact]
    public void A_bundle_opens_through_open_when_not_watched()
    {
        // Arrange
        _dir.File("Lantern.app/Contents/MacOS/Lantern", "macho");

        // Act
        var plan = GameLaunchPlanner.PlanProgram(_dir.Path, OsKind.MacOS, [new LaunchCandidate("Lantern.app", ExecutableFormat.MacBundle)]);

        // Assert
        ((ProcessLaunch)plan).FileName.Should().Be(GameLaunchPlanner.MacOpen);
    }

    [Fact]
    public void The_only_binary_of_a_bundle_without_info_plist_is_its_executable()
    {
        // Arrange
        _dir.File("Lantern.app/Contents/MacOS/nwjs", "macho");

        // Act
        var binary = MacBundles.Executable(_dir.Combine("Lantern.app"));

        // Assert
        binary.Should().Be(_dir.Combine("Lantern.app/Contents/MacOS/nwjs"));
    }

    [Fact]
    public void A_windows_program_doesnt_run_on_linux()
    {
        // Act
        var plan = GameLaunchPlanner.PlanProgram(_dir.Path, OsKind.Linux, [new LaunchCandidate("Game.exe", ExecutableFormat.WindowsPe)], watchable: true);

        // Assert
        plan.Should().Be(new CannotLaunch(LaunchProblem.WindowsOnly, "Game.exe"));
    }

    [Fact]
    public void A_program_with_a_bind_section_is_wrapped_in_steam_drm()
    {
        // Arrange
        _dir.File("Game.exe", Pe(".text", ".rdata", ".bind"));

        // Act
        var wrapped = SteamStub.IsWrapped(_dir.Combine("Game.exe"));

        // Assert
        wrapped.Should().BeTrue();
    }

    [Fact]
    public void A_plain_program_isnt_wrapped()
    {
        // Arrange
        _dir.File("Game.exe", Pe(".text", ".rdata", ".data"));

        // Act
        var wrapped = SteamStub.IsWrapped(_dir.Combine("Game.exe"));

        // Assert
        wrapped.Should().BeFalse();
    }

    [Fact]
    public void A_stub_that_isnt_a_pe_file_isnt_wrapped()
    {
        // Arrange
        _dir.File("Game.exe", "MZ stub: not a real executable");

        // Act
        var wrapped = SteamStub.IsWrapped(_dir.Combine("Game.exe"));

        // Assert
        wrapped.Should().BeFalse();
    }
}
