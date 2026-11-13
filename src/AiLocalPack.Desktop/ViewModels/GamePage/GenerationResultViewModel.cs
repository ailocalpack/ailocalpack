using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Publishing;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.AddGame;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>What the result card's buttons do; the Generation tab runs them.</summary>
public sealed record GenerationResultActions(
    Func<LocalPack, Task> Play,
    Action<LocalPack> Discard,
    Action<LocalPack> ShowInPacks,
    Action NewTranslation,
    Action<LocalPack> NotNow);

/// <summary>
/// The Generation tab's result (task 55, design §6d): "Translation ready", where the pack is saved and its size; four
/// tiles (coverage, test run, fonts, validator) with the warnings list behind <c>Show the N warnings</c>; the in-game
/// language note. Before the player tried the pack in game: <c>Install &amp; play</c> (installs with backup, launches the
/// game; <c>Play</c> once installed) and <c>Discard</c> (only while not installed). After the player played with it
/// installed and closed the game: the "Looks good? Share it" strip (<c>Not now</c>, <c>Publish</c>: task 61) and the
/// actions <c>Play again</c> and <c>Show in Packs</c>; once sent, the same place shows the publish state
/// (<see cref="PackPublishViewModel"/>: sending, CI running, failed, published). <c>New translation</c> keeps the pack in Packs → Mine and
/// returns to the start form.
/// </summary>
public sealed partial class GenerationResultViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly GenerationResultActions _actions;
    private bool _installed;
    private bool _busy;
    private bool _acting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningsToggleText), nameof(ShowWarnings))]
    private bool _isWarningsOpen;

    public GenerationResultViewModel(GenerationResult result, bool installed, bool busy, Localizer localizer, GenerationResultActions actions, PackPublishViewModel publish)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(publish);
        Result = result;
        Publish = publish;
        _installed = installed;
        _busy = busy;
        _localizer = localizer;
        _actions = actions;
        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        DiscardCommand = new RelayCommand(() => _actions.Discard(Pack), () => ShowDiscard && !_busy);
        ShowInPacksCommand = new RelayCommand(() => _actions.ShowInPacks(Pack));
        NewTranslationCommand = new RelayCommand(_actions.NewTranslation, () => !_busy);
        NotNowCommand = new RelayCommand(() => _actions.NotNow(Pack));
        ToggleWarningsCommand = new RelayCommand(() => IsWarningsOpen = !IsWarningsOpen);
    }

    public GenerationResult Result { get; private set; }

    public LocalPack Pack => Result.Pack;

    /// <summary>The pack's publish state and <c>Publish</c> (task 61).</summary>
    public PackPublishViewModel Publish { get; }

    // ---------- Header ----------

    /// <summary>"Saved as a local pack in Packs → Mine · 2.1 MB".</summary>
    public string Subtitle => _localizer.Format(nameof(Strings.Gen_Result_Saved), AddGameText.FormatSize(_localizer, Pack.Report.SizeBytes));

    /// <summary>"«Claude Code translation» into Русский".</summary>
    public string PackLine => Quality is { } quality
        ? _localizer.Format(nameof(Strings.Gen_Result_PackLineQuality), Pack.Manifest.Title, LanguageNames.Native(Pack.Manifest.Language), GenerationQualityTexts.Name(quality, _localizer))
        : _localizer.Format(nameof(Strings.Gen_Result_PackLine), Pack.Manifest.Title, LanguageNames.Native(Pack.Manifest.Language));

    /// <summary>The quality the pack was made at (the manifest's <c>agent.quality</c>), when the app knows it.</summary>
    private GenerationQuality? Quality => Pack.Manifest.Agent?.Quality is { } text && Enum.TryParse<GenerationQuality>(text, ignoreCase: true, out var q) && Enum.IsDefined(q)
        ? q
        : null;

    /// <summary>"Revision 4, an update of rev 3 · New: 12 · Changed: 3 · Removed: 2" for an update job's pack (task 56).</summary>
    public string UpdateLine => Pack.Report.Update is { } u
        ? _localizer.Format(
            nameof(Strings.Gen_Result_Update),
            Pack.Manifest.Revision,
            u.BaseRevision,
            u.Added.ToString("N0", _localizer.Culture),
            u.Changed.ToString("N0", _localizer.Culture),
            u.Removed.ToString("N0", _localizer.Culture))
        : string.Empty;

    public bool HasUpdateLine => UpdateLine.Length > 0;

    // ---------- Tiles and warnings ----------

    public IReadOnlyList<ResultTile> Tiles => LocalPackTexts.Tiles(Pack, _localizer);

    public int WarningCount => Pack.Report.Warnings.Count;

    public bool HasWarnings => WarningCount > 0;

    /// <summary>"Show the 3 warnings" / "Hide the warnings".</summary>
    public string WarningsToggleText => IsWarningsOpen
        ? _localizer[nameof(Strings.Gen_Result_HideWarnings)]
        : _localizer.Plural(nameof(Strings.Gen_Result_ShowWarnings_One), WarningCount, WarningCount.ToString("N0", _localizer.Culture));

    public IReadOnlyList<GenerationWarningRow> Warnings => LocalPackTexts.Warnings(Pack.Report, _localizer, GenerationTabViewModel.MaxWarningRows);

    public bool ShowWarnings => IsWarningsOpen && HasWarnings;

    /// <summary>"In the game, choose English as the language." when the game has no slot for the pack's language.</summary>
    public string InGameNote => Pack.Report.InGameLanguage is { } language
        ? _localizer.Format(nameof(Strings.Gen_Result_InGame), LanguageNames.Native(language))
        : string.Empty;

    public bool HasInGameNote => InGameNote.Length > 0;

    // ---------- State and actions ----------

    /// <summary>The pack is the one installed into the game.</summary>
    public bool IsInstalled => _installed;

    /// <summary>The player played the game with it installed and closed it.</summary>
    public bool IsTried => Pack.Report.IsTried;

    /// <summary>"Looks good? Share it with other players." once tried, until <c>Not now</c> or the pack was sent.</summary>
    public bool ShowShare => IsTried && !Pack.Report.ShareDismissed && Publish.Phase == PublishPhase.NotPublished;

    /// <summary>"Install &amp; play" (not installed), "Play" (installed, not tried yet), "Play again" (tried); "Installing…" meanwhile.</summary>
    public string PlayText => _localizer[_busy && _acting ? nameof(Strings.Packs_Installing)
        : IsTried ? nameof(Strings.Gen_Result_PlayAgain)
        : IsInstalled ? nameof(Strings.Game_Action_Play)
        : nameof(Strings.Gen_Result_InstallPlay)];

    public string PlayIcon => _busy && _acting ? "IconLoader" : "IconPlay";

    /// <summary><c>Install &amp; play</c> is the card's primary button until the pack was tried; <c>Play again</c> is a ghost one.</summary>
    public bool IsPlayPrimary => !IsTried;

    public bool CanPlay => !_busy;

    /// <summary><c>Discard</c> (the pack and its working copy) before the pack was tried and while it isn't installed.</summary>
    public bool ShowDiscard => !IsTried && !IsInstalled;

    /// <summary><c>Show in Packs</c> once the pack is installed or tried.</summary>
    public bool ShowInPacksAction => IsTried || IsInstalled;

    /// <summary>"Installing backs up the original files; you can uninstall any time."</summary>
    public bool ShowCopyNote => !IsInstalled && !IsTried;

    public IAsyncRelayCommand PlayCommand { get; }

    public IRelayCommand DiscardCommand { get; }

    public IRelayCommand ShowInPacksCommand { get; }

    public IRelayCommand NewTranslationCommand { get; }

    public IRelayCommand NotNowCommand { get; }

    public IRelayCommand ToggleWarningsCommand { get; }

    /// <summary>The result read again (tried, "Not now"), the installed pack or the install state changed.</summary>
    internal void Update(GenerationResult result, bool installed, bool busy)
    {
        Result = result;
        _installed = installed;
        _busy = busy;
        Publish.Update(result.Pack);
        if (!busy)
        {
            _acting = false;
        }

        RefreshTexts();
    }

    internal void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        Publish.RefreshTexts();
        PlayCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        NewTranslationCommand.NotifyCanExecuteChanged();
    }

    private async Task PlayAsync()
    {
        _acting = !IsInstalled;
        await _actions.Play(Pack);
    }
}
