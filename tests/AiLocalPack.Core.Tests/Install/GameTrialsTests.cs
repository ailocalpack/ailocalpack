using AiLocalPack.Core.Install;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Install;

/// <summary>Test runs on the live game folder are transactions (_Live game folder_): lock, journal, undo, recovery.</summary>
public sealed class GameTrialsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;

    public GameTrialsTests()
    {
        _paths = AppPaths.UnderHome(_dir.Combine("home"));
        _dir.File("game/data/System.json", "original").File("build/data/System.json", "translated");
    }

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private string GameFile => _dir.Combine("game/data/System.json");

    private GameTrials Trials => new(_paths);

    private GameTrial Open() => Trials.TryOpen(Game, [], [])!;

    [Fact]
    public void A_trial_lays_the_build_over_the_game()
    {
        // Arrange
        using var trial = Open();

        // Act
        trial.Lay(_dir.Combine("build"), ["data/System.json"], []);

        // Assert
        File.ReadAllText(GameFile).Should().Be("translated");
    }

    [Fact]
    public void Undoing_a_trial_puts_the_game_back()
    {
        // Arrange
        using var trial = Open();
        trial.Lay(_dir.Combine("build"), ["data/System.json"], ["steam_appid.txt"]);
        _dir.File("game/steam_appid.txt", "620");

        // Act
        trial.Undo();

        // Assert
        new { Text = File.ReadAllText(GameFile), AppId = File.Exists(_dir.Combine("game/steam_appid.txt")) }
            .Should().Be(new { Text = "original", AppId = false });
    }

    [Fact]
    public void An_ended_trial_leaves_no_journal_or_folder()
    {
        // Arrange
        var trial = Open();
        trial.Lay(_dir.Combine("build"), ["data/System.json"], []);

        // Act
        trial.Dispose();

        // Assert
        new { Journal = File.Exists(_paths.TrialJournal), Trials = Directory.EnumerateFileSystemEntries(_paths.Trials).Any(), Text = File.ReadAllText(GameFile) }
            .Should().Be(new { Journal = false, Trials = false, Text = "original" });
    }

    [Fact]
    public void A_trial_waits_for_another_transaction()
    {
        // Arrange
        using var install = InstallLock.TryAcquire(_paths);

        // Act
        var trial = Trials.TryOpen(Game, [], []);

        // Assert
        trial.Should().BeNull();
    }

    [Fact]
    public void A_trial_a_crash_left_is_undone_on_recovery()
    {
        // Arrange
        var trial = Open();
        trial.Lay(_dir.Combine("build"), ["data/System.json"], []);
        _dir.File("game/cache.bin", "written by the game");
        trial.Abandon();

        // Act
        var recovery = Trials.Recover();

        // Assert
        new { recovery.GameFolder, Text = File.ReadAllText(GameFile), Cache = File.Exists(_dir.Combine("game/cache.bin")), Journal = File.Exists(_paths.TrialJournal) }
            .Should().Be(new { GameFolder = (string?)Path.GetFullPath(Game), Text = "original", Cache = false, Journal = false });
    }

    [Fact]
    public void The_install_recovery_at_start_undoes_a_crashed_trial()
    {
        // Arrange
        var trial = Open();
        trial.Lay(_dir.Combine("build"), ["data/System.json"], []);
        trial.Abandon();
        var installer = new PackInstaller(_paths, new InstalledPackStore(_paths), Core.AntiCheat.AntiCheatPolicy.Bundled, new FakeProcesses(), new OsEnvironment(OsKind.Linux, _dir.Combine("user"), _ => null));

        // Act
        installer.Recover(TestContext.Current.CancellationToken);

        // Assert
        File.ReadAllText(GameFile).Should().Be("original");
    }
}
