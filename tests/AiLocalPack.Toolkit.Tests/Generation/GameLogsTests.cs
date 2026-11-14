using AiLocalPack.Core.Storage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;

namespace AiLocalPack.Toolkit.Tests;

/// <summary>Finding the log a game wrote during a session (task 75).</summary>
public sealed class GameLogsTests : IDisposable
{
    private static readonly DateTimeOffset Since = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Game => _dir.Combine("game");

    private OsEnvironment Env(OsKind os = OsKind.Linux) => new(os, _dir.Combine("home"), _ => null);

    private void Log(string path, string text, DateTimeOffset written)
    {
        _dir.File(path, text);
        File.SetLastWriteTimeUtc(_dir.Combine(path), written.UtcDateTime);
    }

    [Fact]
    public void The_log_written_during_the_session_is_found_in_the_game_folder()
    {
        // Arrange
        Log("game/log.txt", "start\nFATAL: lang line 1342\n", Since.AddSeconds(4));

        // Act
        var log = GameLogs.Find(Game, GameEngine.Unknown, Env(), Since);

        // Assert
        (log!.Name, string.Join('|', log.Lines)).Should().Be(("log.txt", "start|FATAL: lang line 1342"));
    }

    [Fact]
    public void A_log_older_than_the_session_is_left_out()
    {
        // Arrange
        Log("game/log.txt", "old", Since.AddHours(-2));

        // Act
        var log = GameLogs.Find(Game, GameEngine.Unknown, Env(), Since);

        // Assert
        log.Should().BeNull();
    }

    [Fact]
    public void The_newest_of_several_logs_wins()
    {
        // Arrange
        Log("game/logs/engine.log", "engine", Since.AddSeconds(2));
        Log("game/traceback.txt", "traceback", Since.AddSeconds(5));

        // Act
        var log = GameLogs.Find(Game, GameEngine.Unknown, Env(), Since);

        // Assert
        log!.Name.Should().Be("traceback.txt");
    }

    [Fact]
    public void Unitys_player_log_is_found_under_the_company_and_product_of_app_info()
    {
        // Arrange
        _dir.File("game/Neon_Data/app.info", "Studio\nNeon Courier\n");
        Log("home/.config/unity3d/Studio/Neon Courier/Player.log", "NullReferenceException", Since.AddSeconds(3));

        // Act
        var log = GameLogs.Find(Game, GameEngine.Unity, Env(OsKind.Linux), Since);

        // Assert
        log!.Path.Should().Be(_dir.Combine("home/.config/unity3d/Studio/Neon Courier/Player.log"));
    }

    [Fact]
    public void Only_the_last_lines_of_a_long_log_are_kept()
    {
        // Arrange
        Log("game/log.txt", string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}")) + "\n", Since.AddSeconds(1));

        // Act
        var log = GameLogs.Find(Game, GameEngine.Unknown, Env(), Since);

        // Assert
        (log!.Lines.Count, log.Lines[^1]).Should().Be((GameLogs.MaxLines, "line 500"));
    }

    [Theory]
    [InlineData("game.log", true)]
    [InlineData("LOG.TXT", true)]
    [InlineData("traceback.txt", true)]
    [InlineData("crash-2026.txt", true)]
    [InlineData("readme.txt", false)]
    [InlineData("data.json", false)]
    public void Log_like_names_are_recognized(string name, bool isLog)
    {
        // Act
        var result = GameLogs.IsLogName(name);

        // Assert
        result.Should().Be(isLog);
    }
}
