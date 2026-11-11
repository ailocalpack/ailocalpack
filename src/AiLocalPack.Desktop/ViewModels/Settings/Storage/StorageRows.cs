using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.ViewModels.AddGame;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.ViewModels.Settings.Storage;

/// <summary>
/// The summary on top of Settings → Storage (design §8): "<total> used", a stacked bar and its legend (backups, working
/// copies, cache, each with its size). "Measuring…" until the first measure is in.
/// </summary>
public sealed class StorageSummaryRow(SettingRowServices services) : SettingRow(services, nameof(Strings.Settings_Storage_Summary_Label), nameof(Strings.Settings_Storage_Keywords))
{
    private StorageUsage? _usage;

    public bool IsMeasured => _usage is not null;

    public override string Label => _usage is { } usage
        ? Localizer.Format(nameof(Strings.Settings_Storage_Used), Size(usage.TotalBytes))
        : Localizer[nameof(Strings.Settings_Storage_Measuring)];

    public override string? Help => null;

    /// <summary>Shares of the bar (star widths; all 0 when nothing is used, which hides the segments).</summary>
    public double BackupsShare => Share(_usage?.BackupBytes);

    public double WorkingCopiesShare => Share(_usage?.WorkingCopyBytes);

    public double CacheShare => Share(_usage?.CacheBytes);

    public string BackupsLegend => Legend(nameof(Strings.Settings_Storage_Legend_Backups), _usage?.BackupBytes);

    public string WorkingCopiesLegend => Legend(nameof(Strings.Settings_Storage_Legend_WorkingCopies), _usage?.WorkingCopyBytes);

    public string CacheLegend => Legend(nameof(Strings.Settings_Storage_Legend_Cache), _usage?.CacheBytes);

    public void Update(StorageUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _usage = usage;
        RaiseAll();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        RaiseAll();
    }

    private double Share(long? bytes) => _usage is { TotalBytes: > 0 } usage && bytes is { } b ? (double)b / usage.TotalBytes : 0;

    private string Legend(string key, long? bytes) => Localizer.Format(key, Size(bytes ?? 0));

    private string Size(long bytes) => AddGameText.FormatSize(Localizer, bytes);

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(BackupsShare));
        OnPropertyChanged(nameof(WorkingCopiesShare));
        OnPropertyChanged(nameof(CacheShare));
        OnPropertyChanged(nameof(BackupsLegend));
        OnPropertyChanged(nameof(WorkingCopiesLegend));
        OnPropertyChanged(nameof(CacheLegend));
    }
}

/// <summary>
/// Backups of overwritten game files: size and number of games, the folder, <c>Open</c>, <c>Change location…</c>
/// (moves them; "Moving N %" meanwhile, the reason on a line under it when it didn't work). Never cleared here.
/// </summary>
public sealed class BackupsRow : SettingRow
{
    private string _folder = string.Empty;
    private long? _bytes;
    private int _games;
    private BackupMoveProgressState? _moving;
    private string? _error;
    private string? _note;

    public BackupsRow(SettingRowServices services, Action<BackupsRow> open, Func<Task> change)
        : base(services, nameof(Strings.Settings_Storage_Backups_Label), nameof(Strings.Settings_Storage_Backups_Keywords))
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(change);
        OpenCommand = new RelayCommand(() => open(this), () => Directory.Exists(Folder) && !IsMoving);
        ChangeCommand = new AsyncRelayCommand(change, () => !IsMoving);
    }

    public IRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand ChangeCommand { get; }

    /// <summary>The folder the backups are in.</summary>
    public string Folder => _folder;

    public long? Bytes => _bytes;

    public int Games => _games;

    public bool IsMoving => _moving is not null;

    public override string? Help => _moving is { } moving
        ? Localizer.Format(nameof(Strings.Settings_Storage_Backups_Moving), moving.Total > 0 ? (int)(moving.Done * 100 / moving.Total) : 0)
        : _bytes is not { } bytes
            ? Localizer[nameof(Strings.Settings_Storage_Measuring)]
            : _games == 0
                ? Localizer[nameof(Strings.Settings_Storage_Backups_None)]
                : Localizer.Format(nameof(Strings.Settings_Storage_Backups_Size), AddGameText.FormatSize(Localizer, bytes), Localizer.Plural(nameof(Strings.Library_GameCount_One), _games));

    /// <summary>Why the last move didn't happen; <c>null</c> when it did.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    /// <summary>A quiet line after a move (the old folder left for later).</summary>
    public string? Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool HasNote => Note is not null;

    public string OpenText => Localizer[nameof(Strings.Settings_Open)];

    public string ChangeText => Localizer[nameof(Strings.Settings_Storage_Backups_Change)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Folder];

    public void Update(string folder, long bytes, int games)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        (_folder, _bytes, _games) = (folder, bytes, games);
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(Games));
        OnPropertyChanged(nameof(Help));
        OpenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the move's progress; <c>null</c> = the move ended.</summary>
    public void ShowMoving(BackupMoveProgressState? moving)
    {
        var wasMoving = IsMoving;
        _moving = moving;
        OnPropertyChanged(nameof(Help));
        if (wasMoving != IsMoving)
        {
            OnPropertyChanged(nameof(IsMoving));
            OpenCommand.NotifyCanExecuteChanged();
            ChangeCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(OpenText));
        OnPropertyChanged(nameof(ChangeText));
    }
}

/// <summary>Bytes of the backups moved so far (the row's percentage).</summary>
public sealed record BackupMoveProgressState(long Done, long Total);

/// <summary>
/// One generation job folder: the game, its size and the job's state, <c>Delete</c> (confirmation; an open job's
/// progress is lost). A job generating right now can't be deleted: "Stop generation to delete" instead of the button.
/// </summary>
public sealed class WorkingCopyRow : SettingRow
{
    private WorkingCopyUsage _usage;
    private bool _isDeleting;
    private string? _error;

    public WorkingCopyRow(SettingRowServices services, WorkingCopyUsage usage, Action<WorkingCopyRow> delete)
        : base(services, nameof(Strings.Settings_Storage_WorkingCopy_Label), null)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(delete);
        _usage = usage;
        DeleteCommand = new RelayCommand(() => delete(this), () => CanDelete);
    }

    public WorkingCopyUsage Usage => _usage;

    public string Folder => _usage.Folder;

    public GenerationJob? Job => _usage.Job;

    /// <summary>The job is generating (here or in the CLI): its copy is in use.</summary>
    public bool IsRunning => Job?.Status == GenerationJobStatus.Running;

    /// <summary>An open job (paused, interrupted, failed, waiting for the glossary review): deleting loses its progress.</summary>
    public bool LosesProgress => Job is { IsOpen: true } && !IsRunning;

    public bool CanDelete => !IsRunning && !_isDeleting;

    public bool IsDeleting
    {
        get => _isDeleting;
        set
        {
            if (SetProperty(ref _isDeleting, value))
            {
                OnPropertyChanged(nameof(CanDelete));
                DeleteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IRelayCommand DeleteCommand { get; }

    public override string Label => Job?.GameName ?? _usage.GameFolder;

    public override string? Help => Localizer.Format(nameof(Strings.Settings_Storage_WorkingCopy_Help), AddGameText.FormatSize(Localizer, _usage.Bytes), State);

    /// <summary>The job's state in words ("Paused", "Generating now", "Finished").</summary>
    public string State => Job switch
    {
        null => Localizer[nameof(Strings.Settings_Storage_State_NoJob)],
        { Status: GenerationJobStatus.Running } => Localizer[nameof(Strings.Settings_Storage_State_Running)],
        { Status: GenerationJobStatus.Paused, PauseReason: GenerationPauseReason.Limit } => Localizer[nameof(Strings.Settings_Storage_State_PausedLimit)],
        { Status: GenerationJobStatus.Paused } => Localizer[nameof(Strings.Settings_Storage_State_Paused)],
        { Status: GenerationJobStatus.WaitingForReview } => Localizer[nameof(Strings.Settings_Storage_State_Review)],
        { Status: GenerationJobStatus.Interrupted } => Localizer[nameof(Strings.Settings_Storage_State_Interrupted)],
        { Status: GenerationJobStatus.Failed } => Localizer[nameof(Strings.Settings_Storage_State_Failed)],
        { Status: GenerationJobStatus.Cancelled } => Localizer[nameof(Strings.Settings_Storage_State_Cancelled)],
        _ => Localizer[nameof(Strings.Settings_Storage_State_Finished)],
    };

    /// <summary>Why the last delete didn't happen; <c>null</c> when it did.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public string DeleteText => Localizer[nameof(Strings.Settings_Storage_Delete)];

    public string RunningText => Localizer[nameof(Strings.Settings_Storage_WorkingCopy_StopToDelete)];

    public override IEnumerable<string> SearchTexts => [.. base.SearchTexts, Label];

    public void Update(WorkingCopyUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage == _usage)
        {
            return;
        }

        _usage = usage;
        OnPropertyChanged(nameof(Usage));
        OnPropertyChanged(nameof(Job));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(LosesProgress));
        OnPropertyChanged(nameof(CanDelete));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(RunningText));
    }
}

/// <summary>The cache: registry index and downloaded packs, its size, <c>Clear</c> (what it freed, or why not, on a line under it).</summary>
public sealed class CacheRow : SettingRow
{
    private long? _bytes;
    private bool _isClearing;
    private string? _result;
    private bool _resultIsError;

    public CacheRow(SettingRowServices services, Func<Task> clear)
        : base(services, nameof(Strings.Settings_Storage_Cache_Label), nameof(Strings.Settings_Storage_Cache_Keywords))
    {
        ArgumentNullException.ThrowIfNull(clear);
        ClearCommand = new AsyncRelayCommand(clear, () => !IsClearing && _bytes > 0);
    }

    public IAsyncRelayCommand ClearCommand { get; }

    public long? Bytes => _bytes;

    public bool IsClearing
    {
        get => _isClearing;
        set
        {
            if (SetProperty(ref _isClearing, value))
            {
                OnPropertyChanged(nameof(ClearText));
                ClearCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public override string? Help => _bytes is { } bytes
        ? Localizer.Format(nameof(Strings.Settings_Storage_Cache_Help), AddGameText.FormatSize(Localizer, bytes))
        : Localizer[nameof(Strings.Settings_Storage_Measuring)];

    /// <summary>What the last <c>Clear</c> did; <c>null</c> before any.</summary>
    public string? Result => _result;

    public bool HasResult => _result is not null;

    public bool ResultIsError => _resultIsError;

    public string ClearText => Localizer[IsClearing ? nameof(Strings.Settings_Storage_Cache_Clearing) : nameof(Strings.Settings_Storage_Cache_Clear)];

    public void Update(long bytes)
    {
        _bytes = bytes;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(Help));
        ClearCommand.NotifyCanExecuteChanged();
    }

    public void ShowResult(string? text, bool isError)
    {
        _result = text;
        _resultIsError = isError;
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultIsError));
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        OnPropertyChanged(nameof(ClearText));
    }
}
