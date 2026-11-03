using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Games;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Toolkit.Engines;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Library;

/// <summary>
/// One cover card of the library grid (design §1): cover or placeholder, name, a context line (engine for now; pack
/// rating and status come in task 21), the "manual" marker and the blocked look for anti-cheat games.
/// </summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    /// <summary>Placeholder gradients (top, bottom), picked by a stable hash of the game ID so a game keeps its colors.</summary>
    private static readonly (Color Top, Color Bottom)[] Placeholders =
    [
        (Color.Parse("#3b4a8c"), Color.Parse("#1b2140")),
        (Color.Parse("#2f6f73"), Color.Parse("#132c33")),
        (Color.Parse("#7a3f6b"), Color.Parse("#2c1530")),
        (Color.Parse("#7a5a2c"), Color.Parse("#2e2112")),
        (Color.Parse("#3f6b3a"), Color.Parse("#16271a")),
        (Color.Parse("#5b4a8c"), Color.Parse("#221a3c")),
    ];

    private readonly Localizer _localizer;
    private readonly AntiCheatPolicy _policy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Cover), nameof(HasCover), nameof(Icon), nameof(HasIcon))]
    private CoverImage? _image;

    /// <param name="open">Opens the game page (card click, context menu).</param>
    /// <param name="openFolder">Opens the game folder in the file manager (context menu).</param>
    /// <param name="remove">Removes a manual game from the library (context menu); <c>null</c> = no such item.</param>
    public GameCardViewModel(
        Game game,
        Localizer localizer,
        AntiCheatPolicy policy,
        Action<GameCardViewModel> open,
        Action<GameCardViewModel> openFolder,
        Action<GameCardViewModel>? remove = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(openFolder);
        Game = game;
        OpenCommand = new RelayCommand(() => open(this));
        OpenFolderCommand = new RelayCommand(() => openFolder(this), () => IsAvailable);
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this), () => remove is not null && IsManual);
        _localizer = localizer;
        _policy = policy;
        var (top, bottom) = Placeholders[StableHash(game.Id.ToString()) % (uint)Placeholders.Length];
        PlaceholderBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        }.ToImmutable();
    }

    public Game Game { get; private set; }

    /// <summary>On the card itself (not the page) so the context menu, which lives in its own popup, can reach it.</summary>
    public IRelayCommand OpenCommand { get; }

    /// <summary>Disabled while the folder isn't available.</summary>
    public IRelayCommand OpenFolderCommand { get; }

    /// <summary>Removes the game from the library; only manual games (Steam games leave when uninstalled in Steam).</summary>
    public IRelayCommand RemoveCommand { get; }

    public string Name => Game.Name;

    /// <summary>Cover art, cropped to 2:3; <c>null</c> when there is none or the image is an icon.</summary>
    public Bitmap? Cover => Image is { IsIcon: false } image ? image.Bitmap : null;

    public bool HasCover => Cover is not null;

    /// <summary>A manual game's icon, drawn small on the placeholder (the name stays visible).</summary>
    public Bitmap? Icon => Image is { IsIcon: true } image ? image.Bitmap : null;

    public bool HasIcon => Icon is not null;

    /// <summary>Background of the placeholder cover (no art): a gradient with the game name on it.</summary>
    public IBrush PlaceholderBrush { get; }

    public bool IsManual => Game.Source == GameSource.Manual;

    public bool IsAvailable => Game.IsAvailable;

    /// <summary>Anti-cheat blocks install and generation: the card is dimmed, with a lock and an "Anti-cheat" badge.</summary>
    public bool IsBlocked => Verdict.IsBlocked;

    /// <summary>Unavailable (unplugged drive, folder gone) but not blocked: the card is dimmed, without the lock.</summary>
    public bool IsDimmed => !IsBlocked && !IsAvailable;

    public string BadgeText => _localizer[nameof(Strings.Library_Badge_AntiCheat)];

    public string ManualText => _localizer[nameof(Strings.Library_Card_Manual)];

    /// <summary>The "·" between the context line and the "manual" marker.</summary>
    public bool ShowManualSeparator => IsManual && ContextLine.Length > 0;

    /// <summary>Line under the name: the anti-cheat for a blocked game, "folder not available", or the detected engine.</summary>
    public string ContextLine
    {
        get
        {
            if (IsBlocked)
            {
                return Verdict.Name!;
            }

            if (!IsAvailable)
            {
                return _localizer[nameof(Strings.Library_Card_Unavailable)];
            }

            if (!EngineKind.TryParse(Game.Engine, out var engine))
            {
                // Not detected yet (the scan is still running): the line stays empty instead of guessing.
                return string.Empty;
            }

            return engine.Engine == GameEngine.Unknown ? _localizer[nameof(Strings.Library_Card_EngineUnknown)] : engine.DisplayName;
        }
    }

    /// <summary>Screen reader name: the game name, plus the reason when blocked.</summary>
    public string AccessibleName =>
        IsBlocked ? _localizer.Format(nameof(Strings.Library_Card_BlockedName), Name, Verdict.Name) : Name;

    private AntiCheatVerdict Verdict => _policy.Resolve(Game);

    /// <summary>Takes the catalog's newer state of the same game (engine detected, renamed, drive plugged in).</summary>
    public void Update(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Id != Game.Id)
        {
            throw new ArgumentException("A card shows one game; its ID can't change.", nameof(game));
        }

        if (game == Game)
        {
            return;
        }

        Game = game;
        RefreshTexts();
        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsDimmed));
        OpenFolderCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-raises the texts after a UI language switch.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ContextLine));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(ManualText));
        OnPropertyChanged(nameof(ShowManualSeparator));
    }

    /// <summary>Loads the cover; a failure leaves the placeholder.</summary>
    public async Task LoadCoverAsync(ICoverLoader loader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        try
        {
            if (await loader.LoadAsync(Game, cancellationToken) is { } image)
            {
                Image = image;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>FNV-1a: <see cref="string.GetHashCode()"/> differs between runs, the placeholder color must not.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
