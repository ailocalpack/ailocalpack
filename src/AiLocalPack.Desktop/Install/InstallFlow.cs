using AiLocalPack.Core.Games;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Packs;
using AiLocalPack.Core.Registry;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Registry;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.GamePage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Install;

/// <summary>
/// The player's side of installing (design §4a), shared by the Packs tab, the game page's header and the library card:
/// checks the pack when the caller hasn't, asks only when a risk applies (<see cref="InstallRisks"/>; a verified,
/// compatible pack without dependencies, with nothing installed, starts at once), runs the operation through
/// <see cref="PackInstallService"/>, then shows the done toast (<c>▶ Play</c> · <c>Undo</c>) or the failure toast
/// (<c>Retry</c> · <c>Details</c>). One operation at a time: <see cref="IsBusy"/> covers the check before it too.
/// Uninstall asks first (it is destructive); <c>Undo</c> on the done toast doesn't.
/// </summary>
public sealed class InstallFlow
{
    private readonly PackInstallService _service;
    private readonly PackManifests _manifests;
    private readonly PackIndex _packIndex;
    private readonly InstalledPackStore _store;
    private readonly GameLibrary _library;
    private readonly GameStatuses _statuses;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly GameSessions _sessions;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private GameId? _preparing;

    public InstallFlow(
        PackInstallService service,
        PackManifests manifests,
        PackIndex packIndex,
        InstalledPackStore store,
        GameLibrary library,
        GameStatuses statuses,
        IDialogService dialogs,
        ToastService toasts,
        GameSessions sessions,
        Localizer localizer,
        IUiDispatcher ui,
        ILogger<InstallFlow>? logger = null)
    {
        _service = service;
        _manifests = manifests;
        _packIndex = packIndex;
        _store = store;
        _library = library;
        _statuses = statuses;
        _dialogs = dialogs;
        _toasts = toasts;
        _sessions = sessions;
        _localizer = localizer;
        _ui = ui;
        _logger = logger ?? NullLogger<InstallFlow>.Instance;
        _service.Changed += (_, _) => _ui.Post(RaiseChanged);
    }

    /// <summary>Raised on the UI thread when an operation starts, moves on or ends, or a check before one starts or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>The operation running now (progress block, list row, card badge).</summary>
    public InstallOperation? Current => _service.Current;

    /// <summary>The game whose pack is being checked before an install (the buttons are off meanwhile).</summary>
    public GameId? Preparing => _preparing;

    /// <summary>An operation runs, or a check before one: every <c>Install</c> and the header's action are off.</summary>
    public bool IsBusy => _preparing is not null || _service.Current is not null;

    /// <summary>
    /// <c>Install</c> on a pack: checks it when <paramref name="compatibility"/> isn't given, asks when a risk applies,
    /// then installs. An incompatible or unreadable pack ends in the failure toast.
    /// </summary>
    public async Task InstallAsync(Game game, RegistryPack pack, PackManifest? manifest = null, PackCompatibility? compatibility = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(pack);
        if (IsBusy)
        {
            return;
        }

        if (manifest is null || compatibility is null)
        {
            SetPreparing(game.Id);
            try
            {
                (manifest, compatibility) = await CheckAsync(game, pack);
            }
            finally
            {
                SetPreparing(null);
            }

            if (manifest is null || compatibility is null)
            {
                ShowFailure(new InstallOutcome(InstallOperationKind.Install, game, pack.Id, pack.Title, new InstallError(InstallErrorKind.Fetch, Fetch: PackFetchProblem.InvalidManifest), []));
                return;
            }
        }

        if (!compatibility.CanInstall)
        {
            ShowFailure(new InstallOutcome(InstallOperationKind.Install, game, pack.Id, pack.Title, new InstallError(InstallErrorKind.Incompatible, Compatibility: compatibility), []));
            return;
        }

        var installed = _store.Find(game.Id);
        var risks = InstallRisks.For(pack, manifest, compatibility, installed, _service.TitleOf(game.Id, installed), _packIndex.Current?.Index?.Dependencies);
        if (risks.Count == 0)
        {
            await RunInstallAsync(game, pack);
            return;
        }

        _ui.Post(() => _dialogs.Show<InstallConfirmDialogViewModel>(
            new InstallConfirmRequest(pack.Title, risks, () => _ = RunInstallAsync(game, pack))));
    }

    /// <summary>
    /// The card's and the header's <c>Install</c>: the best compatible pack of the target language
    /// (<see cref="BestPack.PickAsync"/>), checked here; <paramref name="noPack"/> runs when none can be installed (the
    /// Packs tab explains why).
    /// </summary>
    public async Task InstallBestAsync(Game game, Action noPack)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(noPack);
        if (IsBusy)
        {
            return;
        }

        var manifests = new Dictionary<string, PackManifest>(StringComparer.Ordinal);
        PackChoice? choice;
        SetPreparing(game.Id);
        try
        {
            choice = await BestPack.PickAsync(_statuses.Resolve(game).Packs, async (pack, _) =>
            {
                var (manifest, compatibility) = await CheckAsync(game, pack);
                if (manifest is not null)
                {
                    manifests[pack.Id] = manifest;
                }

                return compatibility;
            });
        }
        finally
        {
            SetPreparing(null);
        }

        if (choice is null)
        {
            _ui.Post(noPack);
            return;
        }

        await InstallAsync(game, choice.Pack, manifests[choice.Pack.Id], choice.Compatibility);
    }

    /// <summary>The installed block's <c>Uninstall (restore originals)</c>: asks, then restores the original files.</summary>
    public void Uninstall(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (IsBusy || _store.Find(game.Id) is not { } record)
        {
            return;
        }

        var title = _service.TitleOf(game.Id, record);
        _dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
            _localizer.Format(nameof(Strings.Install_Uninstall_Title), title),
            _localizer[nameof(Strings.Install_Uninstall_Message)],
            _localizer[nameof(Strings.Install_Uninstall_Confirm)],
            () => _ = RunUninstallAsync(game)));
    }

    /// <summary>The installed block's <c>Re-apply</c>.</summary>
    public async Task ReapplyAsync(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (IsBusy)
        {
            return;
        }

        var outcome = await _service.ReapplyAsync(Fresh(game));
        _ui.Post(() =>
        {
            if (outcome.Succeeded)
            {
                Toast(new ToastViewModel(ToastTone.Ok, InstallTexts.Toast(outcome, _localizer), [], ToastKey(game)));
            }
            else
            {
                ShowFailure(outcome, () => _ = ReapplyAsync(game));
            }
        });
    }

    private async Task RunInstallAsync(Game game, RegistryPack pack)
    {
        var outcome = await _service.InstallAsync(Fresh(game), pack);
        _ui.Post(() =>
        {
            if (!outcome.Succeeded)
            {
                // The player already confirmed: Retry runs the same install again without asking.
                ShowFailure(outcome, () => _ = RunInstallAsync(game, pack));
                return;
            }

            Toast(new ToastViewModel(
                ToastTone.Ok,
                InstallTexts.Toast(outcome, _localizer),
                [
                    new ToastAction(_localizer[nameof(Strings.Install_Toast_Play)], () => _ = PlayAsync(game), "IconPlay", IsPrimary: true),
                    new ToastAction(_localizer[nameof(Strings.Install_Toast_Undo)], () => _ = RunUninstallAsync(game), "IconUndo"),
                ],
                ToastKey(game)));
        });
    }

    private async Task RunUninstallAsync(Game game)
    {
        var outcome = await _service.UninstallAsync(Fresh(game));
        _ui.Post(() =>
        {
            if (outcome.Succeeded)
            {
                Toast(new ToastViewModel(ToastTone.Info, InstallTexts.Toast(outcome, _localizer), [], ToastKey(game)));
            }
            else
            {
                ShowFailure(outcome, () => _ = RunUninstallAsync(game));
            }
        });
    }

    private async Task PlayAsync(Game game)
    {
        var result = await _sessions.LaunchAsync(Fresh(game));
        if (!result.Succeeded)
        {
            _ui.Post(() => Toast(new ToastViewModel(ToastTone.Bad, LaunchTexts.Problem(result, _localizer), [], ToastKey(game))));
        }
    }

    /// <summary>The failure toast: <c>Retry</c> (when another try may help) and <c>Details</c>.</summary>
    private void ShowFailure(InstallOutcome outcome, Action? retry = null)
    {
        var actions = new List<ToastAction>();
        if (retry is not null && outcome.Error!.CanRetry)
        {
            actions.Add(new ToastAction(_localizer[nameof(Strings.Install_Toast_Retry)], retry, "IconRefresh"));
        }

        actions.Add(new ToastAction(
            _localizer[nameof(Strings.Install_Toast_Details)],
            () => _dialogs.Show<InstallDetailsDialogViewModel>(new InstallDetailsRequest(outcome, retry))));
        _ui.Post(() => Toast(new ToastViewModel(ToastTone.Bad, InstallTexts.Toast(outcome, _localizer), actions, ToastKey(outcome.Game))));
    }

    /// <summary>Fetches the manifest and checks it against the player's files; <c>(null, null)</c> when it can't be read.</summary>
    private async Task<(PackManifest? Manifest, PackCompatibility? Compatibility)> CheckAsync(Game game, RegistryPack pack)
    {
        if (_packIndex.Current?.Location is not { } location)
        {
            return (null, null);
        }

        try
        {
            var fetch = await _manifests.LoadAsync(location, pack);
            if (fetch.Manifest is not { } manifest)
            {
                return (null, null);
            }

            return (manifest, await _manifests.CheckAsync(manifest, Fresh(game)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Checking {Pack} for {Game} failed", pack.Id, game.Id);
            return (null, null);
        }
    }

    /// <summary>The catalog's current copy of the game (a version or folder may have changed since the page read it).</summary>
    private Game Fresh(Game game) => _library.Catalog.Find(game.Id) ?? game;

    private void Toast(ToastViewModel toast) => _toasts.Show(toast);

    private void SetPreparing(GameId? game)
    {
        _preparing = game;
        _ui.Post(RaiseChanged);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string ToastKey(Game game) => $"install:{game.Id}";
}
