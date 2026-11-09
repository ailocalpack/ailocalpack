using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalPack.Desktop.Generation;

/// <summary>What the player chose in the cancel dialog.</summary>
public enum GenerationCancelChoice
{
    /// <summary>Back: nothing happens.</summary>
    Back,

    /// <summary>Stops the job (paused by the player); it can be resumed later.</summary>
    KeepProgress,

    /// <summary>Cancels the job and deletes its folder with the working copy.</summary>
    DeleteWorkingCopy,
}

/// <summary>The cancel dialog's question: how far the job is, and what to do with the choice.</summary>
/// <param name="OnChosen">Runs after the dialog closed (Back and Esc included).</param>
public sealed record GenerationCancelRequest(int Percent, Action<GenerationCancelChoice> OnChosen);

/// <summary>
/// "Cancel the generation? N% is done." (design §6b): <c>Keep progress</c> (stops; resume later) / <c>Delete working
/// copy</c> (frees the disk) / <c>Back</c>. Esc is Back.
/// </summary>
public sealed partial class GenerationCancelDialogViewModel(GenerationCancelRequest request, IDialogService dialogs, Localizer localizer) : DialogViewModel
{
    private bool _chosen;

    public string Title => localizer[nameof(Strings.Gen_Cancel_Title)];

    public string Message => localizer.Format(nameof(Strings.Gen_Cancel_Text), Math.Clamp(request.Percent, 0, 100));

    public override void OnClosed() => Choose(GenerationCancelChoice.Back, close: false);

    [RelayCommand]
    private void Keep() => Choose(GenerationCancelChoice.KeepProgress);

    [RelayCommand]
    private void Delete() => Choose(GenerationCancelChoice.DeleteWorkingCopy);

    [RelayCommand]
    private void Back() => Choose(GenerationCancelChoice.Back);

    private void Choose(GenerationCancelChoice choice, bool close = true)
    {
        if (_chosen)
        {
            return;
        }

        _chosen = true;
        if (close)
        {
            dialogs.Close(this);
        }

        request.OnChosen(choice);
    }
}

/// <summary>What the Details dialog of a job shows.</summary>
/// <param name="GameName">The game, for the title.</param>
/// <param name="Reason">The failure in plain words, if the job failed.</param>
/// <param name="Message">The failure's own (English) message, if any.</param>
/// <param name="Log">The end of the job's log.</param>
/// <param name="Folder">The job folder (opened by <c>Open the job folder</c>).</param>
/// <param name="Retry">Retries the failed stage; <c>null</c> = no Retry button.</param>
public sealed record GenerationDetailsRequest(string GameName, string? Reason, string? Message, IReadOnlyList<string> Log, string? Folder, Action? Retry);

/// <summary>A job's Details (design §6b, failed stage): the reason, the failure's message, the log, Retry stage.</summary>
public sealed partial class GenerationDetailsDialogViewModel(GenerationDetailsRequest request, IDialogService dialogs, ISystemShell shell, Localizer localizer) : DialogViewModel
{
    public string Title => localizer.Format(nameof(Strings.Gen_Details_Title), request.GameName);

    public string Reason => request.Reason ?? string.Empty;

    public bool HasReason => request.Reason is not null;

    public string Message => request.Message ?? string.Empty;

    public bool HasMessage => request.Message is not null;

    public string LogText => request.Log.Count == 0 ? localizer[nameof(Strings.Gen_Log_Empty)] : string.Join(Environment.NewLine, request.Log);

    public bool CanRetry => request.Retry is not null;

    public bool CanOpenFolder => request.Folder is not null;

    [RelayCommand]
    private void Close() => dialogs.Close(this);

    [RelayCommand]
    private void Retry()
    {
        dialogs.Close(this);
        request.Retry?.Invoke();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (request.Folder is { } folder)
        {
            shell.OpenFolder(folder);
        }
    }
}
