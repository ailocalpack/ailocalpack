using AiLocalPack.Core.Install;

namespace AiLocalPack.Core.Tests.Install;

public sealed class GameSavesTests
{
    [Theory]
    [InlineData("www/save/file1.rpgsave")]
    [InlineData("game/saves/1-1-LT1.save")]
    [InlineData("~unreal-quest/SaveGames/slot0.sav")]
    [InlineData("~unity/save_01.json")]
    [InlineData("~unity/prefs")]
    [InlineData("game/saves/persistent")]
    [InlineData("profile.es3")]
    public void A_save_is_kept(string key)
    {
        // Act
        var save = GameSaves.IsSave(key);

        // Assert
        save.Should().BeTrue();
    }

    [Theory]
    [InlineData("books/compiled_books.json")]
    [InlineData("settings.ini")]
    [InlineData("~unity/Player.log")]
    [InlineData("game/cache/bytecode.rpyb")]
    public void A_cache_setting_or_log_is_not_a_save(string key)
    {
        // Act
        var save = GameSaves.IsSave(key);

        // Assert
        save.Should().BeFalse();
    }
}
