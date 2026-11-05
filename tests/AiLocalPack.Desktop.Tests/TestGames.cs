using AiLocalPack.Core.Games;
using AiLocalPack.TestKit;

namespace AiLocalPack.Desktop.Tests;

/// <summary>Catalog games for view-model tests.</summary>
internal static class TestGames
{
    public static Game Steam(
        uint appId,
        string name,
        string? engine = null,
        string? antiCheat = null,
        DateTimeOffset? lastPlayed = null,
        bool available = true) =>
        new(
            GameId.Steam(appId),
            name,
            GameSource.Steam,
            $"/steam/steamapps/common/{name}",
            "/steam",
            GameVersion.FromBuildId("1000"),
            engine,
            antiCheat,
            lastPlayed,
            available);

    public static Game Manual(string name, string? engine = null) =>
        new(
            GameId.Fingerprint(new string('a', GameId.FingerprintLength)),
            name,
            GameSource.Manual,
            $"/games/{name}",
            null,
            null,
            engine,
            null,
            null,
            true);

    /// <summary>Adds Steam game 30 "Shooter" with an <c>EasyAntiCheat/</c> folder (blocked once the library is refreshed).</summary>
    public static SteamFixture AddShooter(SteamFixture steam)
    {
        steam.AddGame(30, "Shooter");
        Directory.CreateDirectory(Path.Combine(steam.Root, "steamapps", "common", "Shooter", "EasyAntiCheat"));
        return steam;
    }
}
