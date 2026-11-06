using AiLocalPack.Core.Games;
using AiLocalPack.Core.Library;

namespace AiLocalPack.Desktop.Library;

/// <summary>The pack installed for a game and language, as statuses need it. The install store (task 29) implements it.</summary>
public interface IInstalledPacks
{
    InstalledPackState? Find(GameId game, string language);
}

/// <summary>Until installing exists (task 29): nothing is installed.</summary>
internal sealed class NoInstalledPacks : IInstalledPacks
{
    public InstalledPackState? Find(GameId game, string language) => null;
}
