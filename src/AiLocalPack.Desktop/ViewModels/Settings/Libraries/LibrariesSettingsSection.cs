using System.Collections.ObjectModel;
using AiLocalPack.Core.Games;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Steam;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Covers;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.Libraries;

/// <summary>
/// Settings → Libraries (design §8): the Steam summary with <c>Rescan</c>, one row per Steam library folder,
/// <c>+ Add Steam library folder</c>; manually added games with <c>Open</c> / <c>Remove</c> and <c>Add game folder</c>;
/// other stores as "coming soon". Each library folder that isn't available gets an alert on top and counts in the badge.
/// Folders added or removed here are kept in <see cref="AppSettings.SteamLibraryFolders"/> /
/// <see cref="AppSettings.IgnoredSteamLibraries"/> and take effect with a rescan right away.
/// </summary>
public sealed class LibrariesSettingsSection : SettingsSection
{
    private readonly GameLibrary _library;
    private readonly SettingsStore _store;
    private readonly IUiDispatcher _ui;
    private readonly ISystemShell _shell;
    private readonly IFilePicker _picker;
    private readonly IDialogService _dialogs;
    private readonly ManualCoverStore _covers;
    private readonly ILogger<LibrariesSettingsSection> _logger;
    private readonly SettingRowServices _services;
    private readonly ObservableCollection<SettingRow> _steamRows = [];
    private readonly ObservableCollection<SettingRow> _manualRows = [];
    private readonly Dictionary<string, LibraryFolderRow> _folders = new(PathNames.Comparer);
    private readonly Dictionary<GameId, ManualGameRow> _games = [];

    public LibrariesSettingsSection(
        GameLibrary library,
        SettingsStore store,
        Localizer localizer,
        IUiDispatcher ui,
        ISystemShell shell,
        IFilePicker picker,
        IDialogService dialogs,
        ManualCoverStore covers,
        ILogger<LibrariesSettingsSection> logger)
        : base(SettingsSectionId.Libraries, "IconFolder", nameof(Strings.Settings_Libraries_Title), nameof(Strings.Settings_Libraries_Description), localizer)
    {
        _library = library;
        _store = store;
        _ui = ui;
        _shell = shell;
        _picker = picker;
        _dialogs = dialogs;
        _covers = covers;
        _logger = logger;
        _services = new SettingRowServices(store, localizer, logger);

        SteamSummary = new SteamSummaryRow(_services, RescanAsync);
        AddSteamFolder = new ActionSettingRow(
            _services,
            nameof(Strings.Settings_AddSteamFolder),
            nameof(Strings.Settings_AddSteamFolder_Help),
            nameof(Strings.Settings_AddSteamFolder_Keywords),
            "IconFolderPlus",
            AddSteamFolderAsync);
        AddGameFolder = new ActionSettingRow(
            _services,
            nameof(Strings.Settings_AddGameFolder),
            null,
            nameof(Strings.Settings_AddGameFolder_Keywords),
            "IconFolderPlus",
            () =>
            {
                _dialogs.Show<AddGameDialogViewModel>();
                return Task.CompletedTask;
            });
        NoManualGames = new TextSettingRow(_services, nameof(Strings.Settings_ManualGames_Empty));
        OtherStores = new ComingSoonSettingRow(
            _services,
            nameof(Strings.Settings_OtherStores_Label),
            nameof(Strings.Settings_OtherStores_Help),
            nameof(Strings.Settings_OtherStores_Keywords));

        Groups =
        [
            new SettingsGroup(_services, nameof(Strings.Settings_Group_Steam), _steamRows),
            new SettingsGroup(_services, nameof(Strings.Settings_Group_ManualGames), _manualRows, _ => nameof(Strings.Settings_ManualGames_Note)),
            new SettingsGroup(_services, nameof(Strings.Settings_Group_OtherStores), [OtherStores]),
        ];

        _library.StateChanged += (_, _) => _ui.Post(Sync);
        _library.Catalog.Changed += (_, _) => _ui.Post(Sync);
        Sync();
    }

    public SteamSummaryRow SteamSummary { get; }

    public ActionSettingRow AddSteamFolder { get; }

    public ActionSettingRow AddGameFolder { get; }

    public TextSettingRow NoManualGames { get; }

    public ComingSoonSettingRow OtherStores { get; }

    /// <summary>Steam library folders of the last scan, in its order.</summary>
    public IReadOnlyList<LibraryFolderRow> Folders => [.. _steamRows.OfType<LibraryFolderRow>()];

    /// <summary>Manually added games, in catalog order (by name).</summary>
    public IReadOnlyList<ManualGameRow> ManualGames => [.. _manualRows.OfType<ManualGameRow>()];

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>Rescans Steam (and the manual folders' availability).</summary>
    public Task RescanAsync() => _library.RefreshAsync();

    /// <summary>
    /// <c>+ Add Steam library folder</c>: the folder picker, then the picked folder (or the library above a picked
    /// <c>steamapps</c>) joins the list and Steam is scanned again. Not a library or already listed → an error on the row.
    /// </summary>
    public async Task AddSteamFolderAsync()
    {
        AddSteamFolder.Error = null;
        var picked = await _picker.PickFolderAsync(Localizer[nameof(Strings.Settings_AddSteamFolder_PickerTitle)]);
        if (picked is null)
        {
            return;
        }

        if (SteamLibraryScanner.FindLibraryFolder(picked) is not { } folder)
        {
            AddSteamFolder.Error = Localizer.Format(nameof(Strings.Settings_AddSteamFolder_NotLibrary), picked);
            return;
        }

        if (_library.LastScan?.Libraries.Any(l => PathNames.Same(l.Path, folder)) == true)
        {
            AddSteamFolder.Error = Localizer.Format(nameof(Strings.Settings_AddSteamFolder_AlreadyListed), folder);
            return;
        }

        // Back in the list even when it was removed before; added by hand in case Steam no longer lists it.
        if (!TrySaveLibraries(s => s with
            {
                SteamLibraryFolders = [.. Without(s.SteamLibraryFolders, folder), folder],
                IgnoredSteamLibraries = Without(s.IgnoredSteamLibraries, folder),
            }))
        {
            AddSteamFolder.Error = Localizer[nameof(Strings.Settings_AddSteamFolder_SaveFailed)];
            return;
        }

        _logger.LogInformation("Steam library folder {Path} added by hand", folder);
        await _library.RefreshAsync();
    }

    /// <summary>
    /// <c>Remove</c> on a folder: asks first; then the folder leaves the list (a folder Steam lists is remembered as
    /// removed), its games leave the catalog, and Steam is scanned again. Files and Steam itself are not touched.
    /// </summary>
    public void RemoveFolder(LibraryFolderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
            Localizer.Format(nameof(Strings.Settings_RemoveFolder_Title), row.Path),
            Localizer[nameof(Strings.Settings_RemoveFolder_Text)],
            Localizer[nameof(Strings.Settings_Remove)],
            () => _ = RemoveFolderConfirmedAsync(row)));
    }

    /// <summary><c>Remove</c> on a manual game: asks first (the folder and installed pack stay on disk), then removes it and its cover.</summary>
    public void RemoveGame(ManualGameRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
            Localizer.Format(nameof(Strings.Settings_RemoveGame_Title), row.Game.Name),
            Localizer[nameof(Strings.Settings_RemoveGame_Text)],
            Localizer[nameof(Strings.Settings_Remove)],
            () =>
            {
                if (_library.Catalog.RemoveManual(row.Game.Id))
                {
                    _covers.Delete(row.Game.Id);
                }
            }));
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        SyncAlerts(_library.LastScan);
    }

    private async Task RemoveFolderConfirmedAsync(LibraryFolderRow row)
    {
        var path = row.Path;
        var steamListsIt = !row.IsAddedByHand;
        if (!TrySaveLibraries(s => s with
            {
                SteamLibraryFolders = Without(s.SteamLibraryFolders, path),
                IgnoredSteamLibraries = steamListsIt ? [.. Without(s.IgnoredSteamLibraries, path), path] : s.IgnoredSteamLibraries,
            }))
        {
            return;
        }

        var removed = _library.Catalog.RemoveSteamLibrary(path);
        _logger.LogInformation("Steam library folder {Path} removed with {Games} games", path, removed);
        await _library.RefreshAsync();
    }

    private bool TrySaveLibraries(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _store.Update(change);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Can't save the Steam library folders");
            return false;
        }
    }

    private static List<string> Without(IReadOnlyList<string>? paths, string path) =>
        [.. (paths ?? []).Where(p => !PathNames.Same(p, path))];

    /// <summary>Brings rows, alerts and the badge in step with the last scan and the catalog (UI thread).</summary>
    private void Sync()
    {
        var scan = _library.LastScan;
        var games = _library.Catalog.Games;
        var steamGames = games.Where(g => g.Source == GameSource.Steam).ToList();
        SteamSummary.Update(new SteamSummaryState(
            _library.IsScanning,
            _library.LastScanAt,
            scan?.Libraries.Count ?? 0,
            steamGames.Count(g => g.IsAvailable)));

        var folderRows = new List<SettingRow> { SteamSummary };
        var listed = new HashSet<string>(PathNames.Comparer);
        foreach (var folder in scan?.Libraries ?? [])
        {
            listed.Add(folder.Path);
            if (!_folders.TryGetValue(folder.Path, out var row))
            {
                row = new LibraryFolderRow(_services, folder.Path, r => _shell.OpenFolder(r.Path), RemoveFolder, SteamSummary.RescanCommand);
                _folders.Add(folder.Path, row);
            }

            row.Update(folder.IsAvailable, folder.IsAddedByHand, steamGames.Count(g => PathNames.Same(g.LibraryPath, folder.Path)));
            folderRows.Add(row);
        }

        foreach (var gone in _folders.Keys.Where(p => !listed.Contains(p)).ToList())
        {
            _folders[gone].Detach();
            _folders.Remove(gone);
        }

        folderRows.Add(AddSteamFolder);
        Replace(_steamRows, folderRows);

        var manual = games.Where(g => g.Source == GameSource.Manual).ToList();
        var ids = manual.Select(g => g.Id).ToHashSet();
        foreach (var gone in _games.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _games[gone].Detach();
            _games.Remove(gone);
        }

        var gameRows = new List<SettingRow>();
        foreach (var game in manual)
        {
            if (_games.TryGetValue(game.Id, out var row))
            {
                row.Update(game);
            }
            else
            {
                row = new ManualGameRow(_services, game, r => _shell.OpenFolder(r.Game.InstallPath), RemoveGame);
                _games.Add(game.Id, row);
            }

            gameRows.Add(row);
        }

        if (gameRows.Count == 0)
        {
            gameRows.Add(NoManualGames);
        }

        gameRows.Add(AddGameFolder);
        Replace(_manualRows, gameRows);
        SyncAlerts(scan);
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(ManualGames));
    }

    /// <summary>One warning per library folder that isn't available; the badge counts them.</summary>
    private void SyncAlerts(SteamScan? scan)
    {
        var games = _library.Catalog.Games;
        Alerts.Clear();
        foreach (var folder in scan?.Libraries.Where(l => !l.IsAvailable) ?? [])
        {
            var hidden = games.Count(g => g.Source == GameSource.Steam && PathNames.Same(g.LibraryPath, folder.Path));
            Alerts.Add(new SettingsAlert(
                Localizer[nameof(Strings.Settings_Alert_LibraryUnavailable_Title)],
                Localizer.Plural(nameof(Strings.Settings_Alert_LibraryUnavailable_Text_One), hidden, folder.Path),
                Localizer[nameof(Strings.Settings_Rescan)],
                SteamSummary.RescanCommand));
        }

        BadgeCount = Alerts.Count;
        BadgeIsError = false;
    }

    /// <summary>Updates <paramref name="target"/> to <paramref name="rows"/> only when they differ, so the view keeps its items (and focus).</summary>
    private static void Replace(ObservableCollection<SettingRow> target, List<SettingRow> rows)
    {
        if (target.SequenceEqual(rows))
        {
            return;
        }

        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }
}
