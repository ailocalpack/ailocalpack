using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Languages;
using AiLocalPack.Core.Packs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.AddGame;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>What a Mine row's buttons do; the Packs tab runs them.</summary>
public sealed record MinePackActions(
    Action<MinePackRowViewModel> Select,
    Action OpenGeneration,
    Action<LocalPack> Install,
    Action Uninstall,
    Action<LocalPack> Delete);

/// <summary>
/// One row of the Packs tab's Mine section (task 55, design §4): the game's generation job (<c>creating 42%</c>, opens
/// the Generation tab) or a pack generated on this machine (<c>Local · not published</c>, <c>tested in game</c> once the
/// player played with it). A local pack's details: the "Tested in game" box, made with, coverage, test run, fonts,
/// validator warnings, size, the note that publishing compares it with the registry's packs first, and the actions
/// <c>Install</c> / <c>Installed ✓</c> + <c>Uninstall</c>, <c>Publish</c> (wired in task 61) and <c>Delete</c>
/// (not while installed).
/// </summary>
public sealed partial class MinePackRowViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly MinePackActions _actions;
    private string _tabLanguage;
    private bool _installed;
    private bool _busy;
    private bool _installing;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>A local pack's row.</summary>
    public MinePackRowViewModel(LocalPack pack, string tabLanguage, bool installed, Localizer localizer, MinePackActions actions)
        : this(localizer, actions, tabLanguage)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        _installed = installed;
    }

    /// <summary>The game's open generation job's row.</summary>
    public MinePackRowViewModel(GenerationJob job, string tabLanguage, Localizer localizer, MinePackActions actions)
        : this(localizer, actions, tabLanguage)
    {
        ArgumentNullException.ThrowIfNull(job);
        Job = job;
    }

    private MinePackRowViewModel(Localizer localizer, MinePackActions actions, string tabLanguage)
    {
        _localizer = localizer;
        _actions = actions;
        _tabLanguage = tabLanguage;
        SelectCommand = new RelayCommand(() =>
        {
            if (IsCreating)
            {
                _actions.OpenGeneration();
            }
            else
            {
                _actions.Select(this);
            }
        });
        InstallCommand = new RelayCommand(() => _actions.Install(Pack!), () => CanInstall);
        UninstallCommand = new RelayCommand(_actions.Uninstall, () => _installed && !_busy);
        DeleteCommand = new RelayCommand(() => _actions.Delete(Pack!), () => CanDelete);
        PublishCommand = new RelayCommand(() => { }, () => false);
    }

    /// <summary>The local pack (a local row), else <c>null</c>.</summary>
    public LocalPack? Pack { get; private set; }

    /// <summary>The generation job (the <c>creating</c> row), else <c>null</c>.</summary>
    public GenerationJob? Job { get; private set; }

    public bool IsCreating => Job is not null;

    public bool IsLocal => Pack is not null;

    /// <summary>A stable key: the job's ID, or the pack's language and ID.</summary>
    public string Key => Job is { } job ? $"job/{job.Id}" : $"{Pack!.Manifest.Language}/{Pack.Manifest.Id}";

    public IRelayCommand SelectCommand { get; }

    public IRelayCommand InstallCommand { get; }

    public IRelayCommand UninstallCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    /// <summary>Off until publishing comes (task 61).</summary>
    public IRelayCommand PublishCommand { get; }

    // ---------- Row ----------

    /// <summary>The pack's title, or "Translation into Русский" for the job.</summary>
    public string Title => Pack is { } pack ? pack.Manifest.Title
        : _localizer.Format(nameof(Strings.Packs_Mine_JobTitle), LanguageNames.Native(Job!.Language));

    /// <summary>"local · not published" (· the language when it isn't the tab's), or "open the Generation tab".</summary>
    public string Meta
    {
        get
        {
            if (Job is not null)
            {
                return _localizer[nameof(Strings.Packs_Mine_JobMeta)];
            }

            var meta = _localizer[nameof(Strings.Packs_Mine_LocalMeta)];
            return Pack!.Manifest.Language == _tabLanguage ? meta : $"{meta} · {LanguageNames.Native(Pack.Manifest.Language)}";
        }
    }

    /// <summary>"creating 42%", "tested in game", "not tried yet".</summary>
    public string Tag => Job is { } job ? _localizer.Format(nameof(Strings.Packs_Mine_Creating), GenerationProgress.Of(job).Percent)
        : _localizer[IsTried ? nameof(Strings.Packs_Mine_Tested) : nameof(Strings.Packs_Mine_NotTried)];

    public string TagIcon => IsCreating ? "IconLoader" : IsTried ? "IconCheck" : "IconCircleDashed";

    /// <summary>The tag is green once tried, teal while creating, muted otherwise.</summary>
    public bool IsTagOk => IsTried;

    public bool IsTagGen => IsCreating;

    /// <summary>The installed pack has the green <c>Installed</c> tag too.</summary>
    public bool IsInstalled => _installed;

    public bool IsTried => Pack?.Report.IsTried == true;

    // ---------- Details ----------

    /// <summary>"local · created 09.11.2026 14:20".</summary>
    public string Created => Pack is { } pack
        ? _localizer.Format(nameof(Strings.Packs_Mine_Created), pack.Report.Created.ToLocalTime().ToString("g", _localizer.Culture))
        : string.Empty;

    /// <summary>"Tested in game" / "Not tried in game yet".</summary>
    public string TriedTitle => _localizer[IsTried ? nameof(Strings.Packs_Mine_TestedTitle) : nameof(Strings.Packs_Mine_NotTriedTitle)];

    public string TriedText => _localizer[IsTried ? nameof(Strings.Packs_Mine_TestedText) : nameof(Strings.Packs_Mine_NotTriedText)];

    public string AgentText => Pack is { } pack ? LocalPackTexts.Agent(pack.Manifest, _localizer) : string.Empty;

    public double CoveragePercent => (Pack?.Manifest.Coverage?.Ratio ?? 0) * 100;

    public string CoverageText => Pack is { } pack ? LocalPackTexts.CoverageText(pack.Manifest, _localizer) : string.Empty;

    /// <summary>"Passed: …" / "Skipped: …".</summary>
    public string TestRunText => Pack is { } pack ? Line(LocalPackTexts.TestRun(pack.Report.TestRun, _localizer)) : string.Empty;

    public string FontsText => Pack is { } pack ? Line(LocalPackTexts.Fonts(pack.Report.Fonts, _localizer)) : string.Empty;

    public string ValidatorText => Pack is { } pack ? Line(LocalPackTexts.Validator(pack.Report, _localizer)) : string.Empty;

    public string SizeText => Pack is { } pack ? AddGameText.FormatSize(_localizer, pack.Report.SizeBytes) : string.Empty;

    /// <summary>"Install", "Installing…", "Installed".</summary>
    public string InstallText => _localizer[_installing ? nameof(Strings.Packs_Installing) : _installed ? nameof(Strings.Packs_Installed) : nameof(Strings.Packs_Install)];

    public string InstallIcon => _installing ? "IconLoader" : _installed ? "IconCheck" : "IconDownload";

    public bool CanInstall => IsLocal && !_installed && !_busy;

    public bool CanDelete => IsLocal && !_installed && !_busy;

    /// <summary>Why Delete or Publish is off.</summary>
    public string ActionHint => _installed ? _localizer[nameof(Strings.Packs_Mine_DeleteInstalled)]
        : IsTried ? _localizer[nameof(Strings.Gen_Result_PublishSoon)]
        : _localizer[nameof(Strings.Packs_Mine_PublishAfterTry)];

    internal void Update(LocalPack pack, string tabLanguage, bool installed)
    {
        Pack = pack;
        _tabLanguage = tabLanguage;
        _installed = installed;
        RefreshTexts();
    }

    internal void Update(GenerationJob job, string tabLanguage)
    {
        Job = job;
        _tabLanguage = tabLanguage;
        RefreshTexts();
    }

    /// <summary>An operation runs (any game), and whether it installs this pack.</summary>
    internal void SetInstallState(bool busy, bool installing)
    {
        _busy = busy;
        _installing = installing;
        RefreshTexts();
    }

    internal void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private string Line(ResultTile tile) => tile.Detail.Length > 0
        ? _localizer.Format(nameof(Strings.Packs_Mine_ValueDetail), tile.Value, tile.Detail)
        : tile.Value;
}
