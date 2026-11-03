using AiLocalPack.Core.Games;

namespace AiLocalPack.Desktop.ViewModels;

/// <summary>Game page. Stub: header and tabs come in task 22.</summary>
public sealed class GamePageViewModel(Game game) : PageViewModel
{
    public Game Game { get; } = game;

    public override string Title => Game.Name;

    public override bool IsSamePage(PageViewModel other) => other is GamePageViewModel page && page.Game.Id == Game.Id;
}
