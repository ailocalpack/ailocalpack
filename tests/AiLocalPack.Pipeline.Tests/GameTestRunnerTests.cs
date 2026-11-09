using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Pipeline.Tests;

/// <summary>The Build stage's test run (task 51): outcomes, the rebuilt files laid over the copy and taken off again.</summary>
public sealed class GameTestRunnerTests : IDisposable
{
    private static readonly TestRunOptions Short = new(TimeSpan.FromMilliseconds(100));

    private static readonly Game Manual = new(
        GameId.Fingerprint(new string('a', 64)), "Lantern", GameSource.Manual, "/games/lantern", null, null, null, "none", null, true);

    private static readonly Game Steam = Manual with { Id = GameId.Steam(620), Source = GameSource.Steam };

    private static readonly string[] BuildFiles = ["data/a.json", "fonts/new.ttf"];

    private readonly TempDir _dir = new();

    public GameTestRunnerTests()
    {
        _dir.File("job/game/Game.sh", "#!/bin/sh\nexec ./game\n")
            .File("job/game/data/a.json", "original")
            .File("job/build/data/a.json", "translated")
            .File("job/build/fonts/new.ttf", "font");
    }

    public void Dispose() => _dir.Dispose();

    private string Job => _dir.Combine("job");

    private string Copy => _dir.Combine("job/game");

    private string CopyFile(string relative) => Path.Combine(Copy, relative.Replace('/', Path.DirectorySeparatorChar));

    private Task<TestRunResult> RunAsync(FakeGameProcesses processes, Game? game = null, OsKind os = OsKind.Linux, TestRunOptions? options = null) =>
        new GameTestRunner(processes, os, options ?? Short).RunAsync(
            new TestRunRequest(game ?? Manual, Copy, _dir.Combine("job/build"), BuildFiles, Job, _ => { }),
            TestContext.Current.CancellationToken);

    private static byte[] SteamStubPe()
    {
        var data = new byte[0x80 + 24 + 0xF0 + 40];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(data, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(data, 0x80);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 0x80 + 6);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(data, 0x80 + 20);
        ".bind"u8.ToArray().CopyTo(data, 0x80 + 24 + 0xF0);
        return data;
    }

    [Fact]
    public async Task A_game_still_running_when_the_watch_ends_passes()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning);

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, Starts = processes.Started.Count }.Should().Be(new { Outcome = TestRunOutcome.Passed, Starts = 1 });
    }

    [Fact]
    public async Task The_translated_run_sees_the_rebuilt_files()
    {
        // Arrange
        string? seen = null;
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning) { OnStart = _ => seen = File.ReadAllText(CopyFile("data/a.json")) };

        // Act
        await RunAsync(processes);

        // Assert
        seen.Should().Be("translated");
    }

    [Fact]
    public async Task Closing_early_while_the_untranslated_copy_keeps_running_is_a_crash()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.Closes(3, "TypeError: font"), FakeGame.KeepsRunning);

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, result.ExitCode, NamesError = result.Detail!.Contains("TypeError: font", StringComparison.Ordinal) }
            .Should().Be(new { Outcome = TestRunOutcome.Crashed, ExitCode = (int?)3, NamesError = true });
    }

    [Fact]
    public async Task The_untranslated_run_sees_the_original_files()
    {
        // Arrange
        var seen = new List<string>();
        var processes = new FakeGameProcesses(FakeGame.Closes(1), FakeGame.KeepsRunning) { OnStart = _ => seen.Add(File.ReadAllText(CopyFile("data/a.json"))) };

        // Act
        await RunAsync(processes);

        // Assert
        seen.Should().Equal("translated", "original");
    }

    [Fact]
    public async Task A_game_that_closes_untranslated_too_is_skipped()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.Closes(1), FakeGame.Closes(1));

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, result.Skip }.Should().Be(new { Outcome = TestRunOutcome.Skipped, Skip = (TestRunSkip?)TestRunSkip.ClosesUntranslated });
    }

    [Fact]
    public async Task After_the_run_the_copy_is_as_before()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.Closes(3), FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes);

        // Assert
        new
        {
            Data = File.ReadAllText(CopyFile("data/a.json")),
            AddedFont = File.Exists(CopyFile("fonts/new.ttf")),
            Overlay = Directory.Exists(Path.Combine(Job, "test-run")),
        }.Should().Be(new { Data = "original", AddedFont = false, Overlay = false });
    }

    [Fact]
    public async Task The_games_error_output_goes_to_the_test_run_log()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.Closes(3, "TypeError: font"), FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes);

        // Assert
        (await File.ReadAllTextAsync(Path.Combine(Job, "logs", "test-run.log"), TestContext.Current.CancellationToken)).Should().Contain("[err] TypeError: font");
    }

    [Fact]
    public async Task A_program_the_os_refuses_is_skipped()
    {
        // Arrange
        var processes = new FakeGameProcesses { FailToStart = true };

        // Act
        var result = await RunAsync(processes);

        // Assert
        new { result.Outcome, result.Skip, Data = File.ReadAllText(CopyFile("data/a.json")) }
            .Should().Be(new { Outcome = TestRunOutcome.Skipped, Skip = (TestRunSkip?)TestRunSkip.StartFailed, Data = "original" });
    }

    [Fact]
    public async Task A_windows_program_on_macos_is_skipped_without_starting()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        _dir.File("job/game/Game.exe", "MZ stub");
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, os: OsKind.MacOS);

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.OtherPlatform, Starts = 0 });
    }

    [Fact]
    public async Task A_copy_without_a_program_is_skipped()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes);

        // Assert
        result.Skip.Should().Be(TestRunSkip.NoExecutable);
    }

    [Fact]
    public async Task A_program_wrapped_in_steam_drm_is_skipped_without_starting()
    {
        // Arrange
        File.Delete(CopyFile("Game.sh"));
        _dir.File("job/game/Game.exe", SteamStubPe());
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, os: OsKind.Windows);

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.SteamDrm, Starts = 0 });
    }

    [Fact]
    public async Task Test_runs_turned_off_are_skipped()
    {
        // Arrange
        var processes = new FakeGameProcesses();

        // Act
        var result = await RunAsync(processes, options: Short with { Enabled = false });

        // Assert
        new { result.Skip, Starts = processes.Started.Count }.Should().Be(new { Skip = (TestRunSkip?)TestRunSkip.TurnedOff, Starts = 0 });
    }

    [Fact]
    public async Task A_steam_game_runs_with_its_app_id_so_steam_doesnt_start_the_installed_game()
    {
        // Arrange
        string? appIdFile = null;
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning) { OnStart = _ => appIdFile = File.ReadAllText(CopyFile("steam_appid.txt")) };

        // Act
        await RunAsync(processes, Steam);

        // Assert
        new { AppIdFile = appIdFile, Variable = processes.Started[0].Environment["SteamAppId"] }.Should().Be(new { AppIdFile = (string?)"620", Variable = (string?)"620" });
    }

    [Fact]
    public async Task The_app_id_file_is_removed_after_the_run()
    {
        // Arrange
        var processes = new FakeGameProcesses(FakeGame.KeepsRunning);

        // Act
        await RunAsync(processes, Steam);

        // Assert
        File.Exists(CopyFile("steam_appid.txt")).Should().BeFalse();
    }

    [Fact]
    public void An_overlay_left_by_a_crash_is_undone()
    {
        // Arrange
        TestRunOverlay.Apply(Job, Copy, _dir.Combine("job/build"), BuildFiles, ["steam_appid.txt"]);
        File.WriteAllText(CopyFile("steam_appid.txt"), "620");

        // Act
        TestRunOverlay.Recover(Job, Copy);

        // Assert
        Directory.EnumerateFiles(Copy, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Copy, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .Should().Equal("Game.sh", "data/a.json");
    }
}
