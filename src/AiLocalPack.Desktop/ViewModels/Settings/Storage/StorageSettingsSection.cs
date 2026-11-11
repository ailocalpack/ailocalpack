using System.Collections.ObjectModel;
using AiLocalPack.Core.Install;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using AiLocalPack.Desktop.ViewModels.AddGame;
using AiLocalPack.Desktop.ViewModels.Settings.Libraries;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.ViewModels.Settings.Storage;

/// <summary>
/// Settings → Storage (design §8, task 66): the summary (total, stacked bar, legend), <em>Backups</em> (size, games,
/// folder, <c>Open</c>, <c>Change location…</c> moving them through <see cref="BackupLocation"/>; never cleared here),
/// <em>Generation working copies</em> (one row per job folder with its size and state, <c>Delete</c> through
/// <see cref="GenerationService.DeleteJobFolderAsync"/>), <em>Cache</em> (size, <c>Clear</c> through
/// <see cref="AppCache"/>). The section list shows the total as its hint. Sizes are measured off the UI thread when the
/// section is built (preloaded at start-up), each time it opens, after its own actions and when a job changes.
/// </summary>
public sealed class StorageSettingsSection : SettingsSection
{
    private readonly AppPaths _paths;
    private readonly BackupLocation _backups;
    private readonly AppCache _cache;
    private readonly GenerationService _generation;
    private readonly IFilePicker _picker;
    private readonly ISystemShell _shell;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<StorageSettingsSection> _logger;
    private readonly SettingRowServices _services;
    private readonly ObservableCollection<SettingRow> _copyRows = [];
    private readonly Dictionary<string, WorkingCopyRow> _copies = new(PathNames.Comparer);
    private int _version;
    private StorageUsage? _usage;

    public StorageSettingsSection(
        AppPaths paths,
        BackupLocation backups,
        AppCache cache,
        GenerationService generation,
        IFilePicker picker,
        ISystemShell shell,
        IDialogService dialogs,
        IUiDispatcher ui,
        SettingsStore store,
        Localizer localizer,
        ILogger<StorageSettingsSection> logger)
        : base(SettingsSectionId.Storage, "IconHardDrive", nameof(Strings.Settings_Storage_Title), nameof(Strings.Settings_Storage_Description), localizer)
    {
        _paths = paths;
        _backups = backups;
        _cache = cache;
        _generation = generation;
        _picker = picker;
        _shell = shell;
        _dialogs = dialogs;
        _ui = ui;
        _logger = logger;
        _services = new SettingRowServices(store, localizer, logger);

        Summary = new StorageSummaryRow(_services);
        Backups = new BackupsRow(_services, r => _shell.OpenFolder(r.Folder), ChangeBackupsLocationAsync);
        Cache = new CacheRow(_services, ClearCacheAsync);
        NoWorkingCopies = new TextSettingRow(_services, nameof(Strings.Settings_Storage_WorkingCopies_Empty));
        _copyRows.Add(NoWorkingCopies);
        Groups =
        [
            new SettingsGroup(_services, nameof(Strings.Settings_Group_DiskSpace), [Summary]),
            new SettingsGroup(_services, nameof(Strings.Settings_Group_Backups), [Backups], _ => nameof(Strings.Settings_Storage_Backups_Note)),
            new SettingsGroup(_services, nameof(Strings.Settings_Group_WorkingCopies), _copyRows),
            new SettingsGroup(_services, nameof(Strings.Settings_Group_Cache), [Cache]),
        ];

        _generation.Changed += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    public StorageSummaryRow Summary { get; }

    public BackupsRow Backups { get; }

    public CacheRow Cache { get; }

    public TextSettingRow NoWorkingCopies { get; }

    /// <summary>One row per job folder, newest first.</summary>
    public IReadOnlyList<WorkingCopyRow> WorkingCopies => [.. _copyRows.OfType<WorkingCopyRow>()];

    /// <summary>The last measure; <c>null</c> until the first is in.</summary>
    public StorageUsage? Usage => _usage;

    public override IReadOnlyList<SettingsGroup> Groups { get; }

    public override void OnShown() => _ = RefreshAsync();

    /// <summary>Measures the disk use again (off the UI thread); a newer measure started meanwhile wins. Never throws.</summary>
    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _version);
        StorageUsage usage;
        try
        {
            usage = await Task.Run(() => StorageUsage.Measure(_paths)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Measuring the disk use failed");
            return;
        }

        if (version == Volatile.Read(ref _version))
        {
            _ui.Post(() => Apply(usage));
        }
    }

    /// <summary>
    /// <c>Change location…</c>: the folder picker, then a confirmation naming the folder the backups go into (an
    /// <c>ailocalpack-backups</c> folder inside the picked one), then <see cref="MoveBackupsAsync"/>.
    /// </summary>
    public async Task ChangeBackupsLocationAsync()
    {
        Backups.Error = null;
        Backups.Note = null;
        var picked = await _picker.PickFolderAsync(Localizer[nameof(Strings.Settings_Storage_Backups_PickerTitle)]);
        if (picked is null)
        {
            return;
        }

        var target = BackupLocation.TargetFor(picked);
        _dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
            Localizer[nameof(Strings.Settings_Storage_Move_Title)],
            Localizer.Format(nameof(Strings.Settings_Storage_Move_Text), target, AddGameText.FormatSize(Localizer, Backups.Bytes ?? 0)),
            Localizer[nameof(Strings.Settings_Storage_Move_Confirm)],
            () => _ = MoveBackupsAsync(picked),
            IsDestructive: false));
    }

    /// <summary>Moves the backups into the folder for <paramref name="picked"/>, with the progress on the row; the reason under it when it didn't work.</summary>
    public async Task MoveBackupsAsync(string picked)
    {
        Backups.Error = null;
        Backups.Note = null;
        Backups.ShowMoving(new BackupMoveProgressState(0, Backups.Bytes ?? 0));
        BackupMoveResult result;
        try
        {
            var progress = new UiProgress<BackupMoveProgress>(_ui, p => Backups.ShowMoving(Backups.IsMoving ? new BackupMoveProgressState(p.BytesDone, p.BytesTotal) : null));
            result = await Task.Run(() => _backups.Move(picked, progress)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Moving the backups failed");
            result = new BackupMoveResult(_backups.Root, BackupMoveProblem.IoError, e.Message);
        }

        _ui.Post(() =>
        {
            Backups.ShowMoving(null);
            Backups.Error = result.Succeeded ? null : MoveProblemText(result, BackupLocation.TargetFor(picked));
            Backups.Note = result.LeftBehind is { } old ? Localizer.Format(nameof(Strings.Settings_Storage_Move_LeftBehind), old) : null;
        });
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// <c>Delete</c> on a working copy: asks first (an open job's progress is lost; packs stay), then deletes the folder
    /// (an open job is cancelled first). Nothing for a job generating right now.
    /// </summary>
    public void DeleteWorkingCopy(WorkingCopyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanDelete)
        {
            return;
        }

        row.Error = null;
        var size = AddGameText.FormatSize(Localizer, row.Usage.Bytes);
        _dialogs.Show<ConfirmDialogViewModel>(new ConfirmRequest(
            Localizer.Format(nameof(Strings.Settings_Storage_DeleteCopy_Title), row.Label),
            row.LosesProgress
                ? Localizer.Format(nameof(Strings.Settings_Storage_DeleteCopy_TextOpen), size)
                : Localizer.Format(nameof(Strings.Settings_Storage_DeleteCopy_Text), size),
            Localizer[nameof(Strings.Settings_Storage_Delete)],
            () => _ = DeleteConfirmedAsync(row)));
    }

    /// <summary><c>Clear</c> on the cache: deletes the downloads (refused while an install runs); what it freed under the row.</summary>
    public async Task ClearCacheAsync()
    {
        Cache.IsClearing = true;
        Cache.ShowResult(null, false);
        CacheClearResult result;
        try
        {
            result = await Task.Run(_cache.Clear).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Clearing the cache failed");
            result = new CacheClearResult(true, 0, 0);
        }

        _ui.Post(() =>
        {
            Cache.IsClearing = false;
            if (result.Busy)
            {
                Cache.ShowResult(Localizer[nameof(Strings.Settings_Storage_Cache_Busy)], isError: true);
            }
            else
            {
                var freed = AddGameText.FormatSize(Localizer, result.FreedBytes);
                Cache.ShowResult(
                    result.LeftBytes > 0
                        ? Localizer.Format(nameof(Strings.Settings_Storage_Cache_ClearedSome), freed, AddGameText.FormatSize(Localizer, result.LeftBytes))
                        : Localizer.Format(nameof(Strings.Settings_Storage_Cache_Cleared), freed),
                    isError: false);
            }
        });
        await RefreshAsync().ConfigureAwait(false);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        UpdateHint();
    }

    /// <summary>Deletes the working copy once the player confirmed (an open job is cancelled first); the reason on the row when it failed.</summary>
    public async Task DeleteConfirmedAsync(WorkingCopyRow row)
    {
        row.IsDeleting = true;
        var result = await _generation.DeleteJobFolderAsync(row.Folder).ConfigureAwait(false);
        _ui.Post(() =>
        {
            row.IsDeleting = false;
            row.Error = result.Problem switch
            {
                null => null,
                GenerationActionProblem.Busy => Localizer[nameof(Strings.Settings_Storage_DeleteCopy_Busy)],
                _ => Localizer.Format(nameof(Strings.Settings_Storage_DeleteCopy_Failed), result.Detail ?? string.Empty),
            };
        });
        await RefreshAsync().ConfigureAwait(false);
    }

    private string MoveProblemText(BackupMoveResult result, string target) => result.Problem switch
    {
        BackupMoveProblem.Busy => Localizer[nameof(Strings.Settings_Storage_Move_Busy)],
        BackupMoveProblem.Interrupted => Localizer[nameof(Strings.Settings_Storage_Move_Interrupted)],
        BackupMoveProblem.SameFolder => Localizer[nameof(Strings.Settings_Storage_Move_SameFolder)],
        BackupMoveProblem.Nested => Localizer[nameof(Strings.Settings_Storage_Move_Nested)],
        BackupMoveProblem.NotEmpty => Localizer.Format(nameof(Strings.Settings_Storage_Move_NotEmpty), target),
        BackupMoveProblem.DiskFull => Localizer[nameof(Strings.Settings_Storage_Move_DiskFull)],
        BackupMoveProblem.Denied => Localizer[nameof(Strings.Settings_Storage_Move_Denied)],
        BackupMoveProblem.VerifyFailed => Localizer[nameof(Strings.Settings_Storage_Move_VerifyFailed)],
        BackupMoveProblem.NewerFormat => Localizer[nameof(Strings.Settings_Storage_Move_NewerFormat)],
        _ => Localizer.Format(nameof(Strings.Settings_Storage_Move_Failed), result.Detail ?? string.Empty),
    };

    /// <summary>Brings the rows and the hint in step with <paramref name="usage"/> (UI thread).</summary>
    private void Apply(StorageUsage usage)
    {
        _usage = usage;
        Summary.Update(usage);
        Backups.Update(usage.BackupsFolder, usage.BackupBytes, usage.BackupGames);
        Cache.Update(usage.CacheBytes);

        var rows = new List<SettingRow>();
        var listed = new HashSet<string>(PathNames.Comparer);
        foreach (var copy in usage.WorkingCopies)
        {
            listed.Add(copy.Folder);
            if (_copies.TryGetValue(copy.Folder, out var row))
            {
                row.Update(copy);
            }
            else
            {
                row = new WorkingCopyRow(_services, copy, DeleteWorkingCopy);
                _copies.Add(copy.Folder, row);
            }

            rows.Add(row);
        }

        foreach (var gone in _copies.Keys.Where(f => !listed.Contains(f)).ToList())
        {
            _copies[gone].Detach();
            _copies.Remove(gone);
        }

        if (rows.Count == 0)
        {
            rows.Add(NoWorkingCopies);
        }

        if (!_copyRows.SequenceEqual(rows))
        {
            _copyRows.Clear();
            foreach (var row in rows)
            {
                _copyRows.Add(row);
            }
        }

        UpdateHint();
        OnPropertyChanged(nameof(Usage));
        OnPropertyChanged(nameof(WorkingCopies));
    }

    private void UpdateHint() => Hint = _usage is { } usage ? AddGameText.FormatSize(Localizer, usage.TotalBytes) : null;

    /// <summary>Progress reports handed to the UI thread through <see cref="IUiDispatcher"/>.</summary>
    private sealed class UiProgress<T>(IUiDispatcher ui, Action<T> report) : IProgress<T>
    {
        public void Report(T value) => ui.Post(() => report(value));
    }
}
