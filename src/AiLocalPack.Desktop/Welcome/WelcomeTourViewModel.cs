using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Welcome;

/// <summary>The slides of the welcome tour, in order.</summary>
public enum WelcomeSlide
{
    Welcome,
    Install,
    CreateWithAi,
    Community,
}

/// <summary>One point of a slide: an icon, a bold lead and the rest of the sentence.</summary>
public sealed record WelcomePoint(string IconKey, string Lead, string Text);

/// <summary>A step dot under a slide: the current one is long, the ones seen are tinted.</summary>
public sealed record WelcomeDot(bool IsCurrent, bool IsDone);

/// <summary>
/// The welcome tour (design §10): one dialog of four slides over the library: what the app does and the translation
/// language, installing (undoable, survives updates, anti-cheat safe), creating with the player's own AI agent,
/// sharing and rating. Nothing in it is required: Skip, Esc and ✕ close it like the last slide's button. Closing it
/// any way records <see cref="AppSettings.WelcomeTourShown"/>, so it opens by itself only once.
/// </summary>
public sealed partial class WelcomeTourViewModel : DialogViewModel
{
    public const int SlideCount = 4;

    private readonly IDialogService _dialogs;
    private readonly SettingsStore _settings;
    private readonly Localizer _localizer;
    private readonly GameLibrary _library;
    private readonly IUiDispatcher _ui;
    private readonly EventHandler _libraryChanged;
    private readonly EventHandler _cultureChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Slide), nameof(IsWelcome), nameof(IsInstall), nameof(IsCreateWithAi), nameof(IsCommunity))]
    [NotifyPropertyChangedFor(nameof(IsFirst), nameof(IsLast), nameof(StepText), nameof(Title), nameof(Body), nameof(Points))]
    [NotifyPropertyChangedFor(nameof(NextText), nameof(Dots))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private int _index;

    public WelcomeTourViewModel(
        IDialogService dialogs,
        SettingsStore settings,
        Localizer localizer,
        GameLibrary library,
        IUiDispatcher ui,
        ILogger<WelcomeTourViewModel> logger)
    {
        _dialogs = dialogs;
        _settings = settings;
        _localizer = localizer;
        _library = library;
        _ui = ui;
        GameLanguage = new DropdownSettingRow(
            new SettingRowServices(settings, localizer, logger),
            nameof(Strings.Settings_GameLanguage_Label),
            nameof(Strings.Welcome_LanguageHelp),
            nameof(Strings.Settings_GameLanguage_Keywords),
            LanguageOptions.ForGames(localizer, UiLanguages.Os),
            s => s.GameLanguage,
            (s, v) => s with { GameLanguage = (string?)v });
        _libraryChanged = (_, _) => _ui.Post(() => OnPropertyChanged(nameof(GamesFound)));
        _cultureChanged = (_, _) => RefreshTexts();
        _library.StateChanged += _libraryChanged;
        _localizer.CultureChanged += _cultureChanged;
    }

    public WelcomeSlide Slide => (WelcomeSlide)Index;

    public bool IsWelcome => Slide == WelcomeSlide.Welcome;

    public bool IsInstall => Slide == WelcomeSlide.Install;

    public bool IsCreateWithAi => Slide == WelcomeSlide.CreateWithAi;

    public bool IsCommunity => Slide == WelcomeSlide.Community;

    public bool IsFirst => Index == 0;

    public bool IsLast => Index == SlideCount - 1;

    /// <summary>"1 / 4 · Welcome".</summary>
    public string StepText => _localizer.Format(nameof(Strings.Welcome_Step), Index + 1, SlideCount, _localizer[Slide switch
    {
        WelcomeSlide.Welcome => nameof(Strings.Welcome_Step_Welcome),
        WelcomeSlide.Install => nameof(Strings.Welcome_Step_Install),
        WelcomeSlide.CreateWithAi => nameof(Strings.Welcome_Step_CreateWithAi),
        _ => nameof(Strings.Welcome_Step_Community),
    }]);

    public string Title => _localizer[Slide switch
    {
        WelcomeSlide.Welcome => nameof(Strings.Welcome_Title_Welcome),
        WelcomeSlide.Install => nameof(Strings.Welcome_Title_Install),
        WelcomeSlide.CreateWithAi => nameof(Strings.Welcome_Title_CreateWithAi),
        _ => nameof(Strings.Welcome_Title_Community),
    }];

    public string Body => _localizer[Slide switch
    {
        WelcomeSlide.Welcome => nameof(Strings.Welcome_Body_Welcome),
        WelcomeSlide.Install => nameof(Strings.Welcome_Body_Install),
        WelcomeSlide.CreateWithAi => nameof(Strings.Welcome_Body_CreateWithAi),
        _ => nameof(Strings.Welcome_Body_Community),
    }];

    /// <summary>"We found 18 games in your library." once the library knows any; <c>null</c> before.</summary>
    public string? GamesFound => _library.Catalog.Games.Count is var count and > 0
        ? _localizer.Plural(nameof(Strings.Welcome_GamesFound_Other), count)
        : null;

    /// <summary>The first slide's choice: the language games are translated into (the same setting as Settings → General).</summary>
    public DropdownSettingRow GameLanguage { get; }

    public IReadOnlyList<WelcomePoint> Points => Slide switch
    {
        WelcomeSlide.Install =>
        [
            Point("IconUndo", nameof(Strings.Welcome_Install_Undo_Lead), nameof(Strings.Welcome_Install_Undo)),
            Point("IconRefresh", nameof(Strings.Welcome_Install_Updates_Lead), nameof(Strings.Welcome_Install_Updates)),
            Point("IconShield", nameof(Strings.Welcome_Install_Safe_Lead), nameof(Strings.Welcome_Install_Safe)),
        ],
        WelcomeSlide.CreateWithAi =>
        [
            Point("IconSparkles", nameof(Strings.Welcome_Ai_Setup_Lead), nameof(Strings.Welcome_Ai_Setup)),
            Point("IconPause", nameof(Strings.Welcome_Ai_Resume_Lead), nameof(Strings.Welcome_Ai_Resume)),
        ],
        WelcomeSlide.Community =>
        [
            Point("IconCheckCircle", nameof(Strings.Welcome_Community_Verified_Lead), nameof(Strings.Welcome_Community_Verified)),
            Point("IconGitHub", nameof(Strings.Welcome_Community_GitHub_Lead), nameof(Strings.Welcome_Community_GitHub)),
        ],
        _ => [],
    };

    public IReadOnlyList<WelcomeDot> Dots => [.. Enumerable.Range(0, SlideCount).Select(i => new WelcomeDot(i == Index, i < Index))];

    /// <summary>"Let's go" on the first slide, "Open my library" on the last, "Next" between.</summary>
    public string NextText => _localizer[IsFirst ? nameof(Strings.Welcome_Start) : IsLast ? nameof(Strings.Welcome_Finish) : nameof(Strings.Welcome_Next)];

    /// <summary>The agents named on the AI slide's illustration.</summary>
    public IReadOnlyList<AgentInfo> Agents { get; } = [AgentInfo.Claude, AgentInfo.Codex];

    /// <summary>Illustration of the install slide: "Installed" on the cover, Play below.</summary>
    public string InstalledText => _localizer[nameof(Strings.Library_Status_Installed)];

    public string AvailableText => _localizer[nameof(Strings.Library_Status_PackAvailable)];

    public string NoTranslationText => _localizer[nameof(Strings.Library_Status_NoPack)];

    /// <summary>The installed game's line on the welcome slide, in the player's language: "Hello, traveller!".</summary>
    public string GreetingText => _localizer[nameof(Strings.Welcome_Ill_Greeting)];

    public string PlayText => _localizer[nameof(Strings.Game_Action_Play)];

    /// <summary>Illustration of the AI slide: "Translating… 42 %".</summary>
    public string TranslatingText => _localizer.Format(nameof(Strings.Welcome_Ill_Translating), 42);

    /// <summary>Illustration of the community slide: "3 of 3 players confirmed it works", "Verified".</summary>
    public string ConfirmedText => _localizer.Format(nameof(Strings.Welcome_Ill_Confirmed), 3, 3);

    public string VerifiedText => _localizer[nameof(Strings.Welcome_Ill_Verified)];

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            _dialogs.Close(this);
        }
        else
        {
            Index++;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Index--;

    private bool CanGoBack() => Index > 0;

    /// <summary>Skip, ✕: the tour closes from any slide.</summary>
    [RelayCommand]
    private void Skip() => _dialogs.Close(this);

    public override void OnClosed()
    {
        _library.StateChanged -= _libraryChanged;
        _localizer.CultureChanged -= _cultureChanged;
        if (!_settings.Current.WelcomeTourShown)
        {
            _settings.Update(s => s with { WelcomeTourShown = true });
        }
    }

    private WelcomePoint Point(string icon, string leadKey, string textKey) => new(icon, _localizer[leadKey], _localizer[textKey]);

    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(GamesFound));
        OnPropertyChanged(nameof(InstalledText));
        OnPropertyChanged(nameof(AvailableText));
        OnPropertyChanged(nameof(NoTranslationText));
        OnPropertyChanged(nameof(GreetingText));
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(TranslatingText));
        OnPropertyChanged(nameof(ConfirmedText));
        OnPropertyChanged(nameof(VerifiedText));
    }
}
