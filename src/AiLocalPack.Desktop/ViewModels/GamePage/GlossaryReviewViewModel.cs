using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Desktop.Generation;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using AiLocalPack.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiLocalPack.Desktop.ViewModels.GamePage;

/// <summary>A row of the glossary table: the term, its translation (editable) and the note.</summary>
public sealed partial class GlossaryRowViewModel : ObservableObject
{
    private readonly Action<GlossaryRowViewModel> _changed;
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    private string _translation;

    [ObservableProperty]
    private bool _isEdited;

    internal GlossaryRowViewModel(GlossaryEntry entry, string note, Action<GlossaryRowViewModel> changed)
    {
        _changed = changed;
        Term = entry.Term;
        _translation = entry.Translation;
        _isEdited = entry.Edited;
        Note = note;
    }

    public string Term { get; }

    /// <summary>"place", "keep as is · a brand", "noun, feminine · the heroine".</summary>
    public string Note { get; private set; }

    public bool HasNote => Note.Length > 0;

    /// <summary>An empty translation isn't saved (a term needs one); <c>Continue</c> waits until it is filled.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Translation);

    internal void Apply(GlossaryEntry entry, string note, bool text)
    {
        _loading = true;
        try
        {
            if (text)
            {
                Translation = entry.Translation;
            }

            IsEdited = entry.Edited;
            Note = note;
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(HasNote));
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnTranslationChanged(string value)
    {
        if (!_loading)
        {
            _changed(this);
        }
    }
}

/// <summary>A choice of the review's tone list: as the agent decided, informal, formal.</summary>
public sealed record GlossaryToneOption(FormOfAddress? Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The glossary review on the Generation tab (task 54, design §6c), shown while the job waits for the player: the terms
/// with their translations (editable), the notes, the tone (the agent's words) and the form of address (the agent's
/// choice, informal or formal). Every change is saved to the job at once (<see cref="GenerationService.EditGlossaryAsync"/>,
/// in order, the latest text of a term winning), so it survives a restart; a term the player changed is marked edited and
/// stays when the glossary is drafted again. <c>Continue to translation</c> waits for the saves and for every term to
/// have a translation.
/// </summary>
public sealed partial class GlossaryReviewViewModel : ObservableObject
{
    private readonly GenerationService _jobs;
    private readonly GameId _game;
    private readonly Localizer _localizer;
    private readonly IUiDispatcher _ui;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _pending = new(Glossary.TermComparer);
    private List<GlossaryRowViewModel> _rows = [];
    private Glossary? _glossary;
    private (bool Set, FormOfAddress? Value) _pendingAddress;
    private Task _saving = Task.CompletedTask;
    private bool _looping;
    private bool _choosingTone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsLoaded), nameof(IsFailed))]
    private GlossaryReviewState _state = GlossaryReviewState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private GlossaryToneOption? _selectedTone;

    public GlossaryReviewViewModel(GenerationJob job, GenerationService jobs, Localizer localizer, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(job);
        Job = job;
        _game = job.Game;
        _jobs = jobs;
        _localizer = localizer;
        _ui = ui;
        ToneOptions = BuildToneOptions();
    }

    /// <summary>The job as it was when the review opened (a regenerated glossary is a new review).</summary>
    public GenerationJob Job { get; }

    public bool IsLoading => State == GlossaryReviewState.Loading;

    public bool IsLoaded => State == GlossaryReviewState.Loaded;

    public bool IsFailed => State == GlossaryReviewState.Failed;

    public bool HasError => Error.Length > 0;

    /// <summary>"214 terms".</summary>
    public string CountText => _localizer.Plural(nameof(Strings.Gen_Review_Terms_One), _rows.Count, _rows.Count.ToString("N0", _localizer.Culture));

    /// <summary>The rows matching <see cref="Query"/> (term, translation or note).</summary>
    public IReadOnlyList<GlossaryRowViewModel> Rows => Query.Trim() is { Length: > 0 } q
        ? [.. _rows.Where(r => TextSearch.Matches(r.Term, q) || TextSearch.Matches(r.Translation, q) || TextSearch.Matches(r.Note, q))]
        : _rows;

    public bool HasNoRows => IsLoaded && Rows.Count == 0;

    /// <summary>"No terms match" while searching, else that the agent stored only the tone.</summary>
    public string NoRowsText => _localizer[_rows.Count > 0 ? nameof(Strings.Gen_Review_NoMatch) : nameof(Strings.Gen_Review_Empty)];

    public IReadOnlyList<GlossaryToneOption> ToneOptions { get; private set; }

    /// <summary>"Tone chosen by the agent: informal, playful…"; empty without one.</summary>
    public string AgentTone => _glossary?.Tone is { } tone ? _localizer.Format(nameof(Strings.Gen_Review_AgentTone), tone) : string.Empty;

    public bool HasAgentTone => AgentTone.Length > 0;

    /// <summary>Terms the player changed.</summary>
    public int EditedCount => _rows.Count(r => r.IsEdited);

    /// <summary>A term has no translation: <c>Continue</c> is off until it gets one.</summary>
    public bool HasBlank => _rows.Any(r => r.IsBlank);

    /// <summary>Loaded, saved as far as the player can tell, every term translated.</summary>
    public bool CanContinue => IsLoaded && !HasBlank;

    /// <summary>Reads the job's glossary (off the UI thread) and shows it.</summary>
    public async Task LoadAsync()
    {
        var result = await Task.Run(() => _jobs.ReadGlossary(_game)).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (result.Glossary is not { } glossary)
            {
                Error = _localizer.Format(nameof(Strings.Gen_Review_LoadFailed), result.Detail);
                State = GlossaryReviewState.Failed;
                return;
            }

            _glossary = glossary;
            _rows = [.. glossary.Entries.Select(e => new GlossaryRowViewModel(e, NoteOf(e), OnRowChanged))];
            foreach (var row in _rows)
            {
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(GlossaryRowViewModel.IsBlank))
                    {
                        RaiseState();
                    }
                };
            }

            ShowTone();
            Error = string.Empty;
            State = GlossaryReviewState.Loaded;
            OnPropertyChanged(string.Empty);
        });
    }

    /// <summary>Waits until every change so far is saved; <c>false</c> when a save failed (the error shows).</summary>
    public async Task<bool> FlushAsync()
    {
        Task saving;
        lock (_lock)
        {
            saving = _saving;
        }

        await saving.ConfigureAwait(false);
        return !HasError;
    }

    /// <summary>The UI language changed.</summary>
    public void RefreshTexts()
    {
        ShowTone();
        foreach (var row in _rows)
        {
            if (_glossary?.Entries.FirstOrDefault(e => Glossary.TermComparer.Equals(e.Term, row.Term)) is { } entry)
            {
                row.Apply(entry, NoteOf(entry), text: false);
            }
        }

        OnPropertyChanged(string.Empty);
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(NoRowsText));
    }

    partial void OnSelectedToneChanged(GlossaryToneOption? value)
    {
        if (_choosingTone || value is null || _glossary is null)
        {
            return;
        }

        lock (_lock)
        {
            _pendingAddress = (true, value.Value);
        }

        Save();
    }

    private void OnRowChanged(GlossaryRowViewModel row)
    {
        if (row.IsBlank)
        {
            RaiseState();
            return;
        }

        lock (_lock)
        {
            _pending[row.Term] = row.Translation;
        }

        Save();
    }

    private void Save()
    {
        lock (_lock)
        {
            if (!_looping)
            {
                _looping = true;
                _saving = SaveLoopAsync();
            }
        }
    }

    /// <summary>Saves the pending changes, a batch at a time, until none is left.</summary>
    private async Task SaveLoopAsync()
    {
        await Task.Yield();
        while (true)
        {
            KeyValuePair<string, string>[] texts;
            (bool Set, FormOfAddress? Value) address;
            lock (_lock)
            {
                if (_pending.Count == 0 && !_pendingAddress.Set)
                {
                    _looping = false;
                    return;
                }

                texts = [.. _pending];
                _pending.Clear();
                address = _pendingAddress;
                _pendingAddress = default;
            }

            var result = await _jobs.EditGlossaryAsync(_game, glossary =>
            {
                foreach (var (term, text) in texts)
                {
                    glossary = GlossaryReview.SetTranslation(glossary, term, text);
                }

                return address.Set ? GlossaryReview.SetFormOfAddress(glossary, address.Value) : glossary;
            }).ConfigureAwait(false);
            _ui.Post(() => Saved(result));
        }
    }

    private void Saved(GenerationGlossaryResult result)
    {
        if (result.Glossary is not { } glossary)
        {
            Error = _localizer.Format(nameof(Strings.Gen_Review_SaveFailed), result.Detail);
            return;
        }

        Error = string.Empty;
        _glossary = glossary;
        foreach (var row in _rows)
        {
            if (glossary.Entries.FirstOrDefault(e => Glossary.TermComparer.Equals(e.Term, row.Term)) is { } entry)
            {
                row.Apply(entry, NoteOf(entry), text: false);
            }
        }

        OnPropertyChanged(nameof(EditedCount));
    }

    private void ShowTone()
    {
        _choosingTone = true;
        try
        {
            ToneOptions = BuildToneOptions();
            SelectedTone = ToneOptions.FirstOrDefault(o => o.Value == _glossary?.FormOfAddress) ?? ToneOptions[0];
        }
        finally
        {
            _choosingTone = false;
        }
    }

    private List<GlossaryToneOption> BuildToneOptions()
    {
        var language = Job.Language;
        return
        [
            new(null, _localizer[nameof(Strings.Gen_Review_Tone_Agent)]),
            new(FormOfAddress.Informal, Named(nameof(Strings.Gen_Review_Tone_Informal), FormsOfAddress.Pronoun(language, FormOfAddress.Informal))),
            new(FormOfAddress.Formal, Named(nameof(Strings.Gen_Review_Tone_Formal), FormsOfAddress.Pronoun(language, FormOfAddress.Formal))),
        ];

        string Named(string key, string? pronoun) => pronoun is null
            ? _localizer[key]
            : _localizer.Format(nameof(Strings.Gen_Review_Tone_WithPronoun), _localizer[key], pronoun);
    }

    /// <summary>"keep as is · noun, feminine · the heroine".</summary>
    private string NoteOf(GlossaryEntry entry)
    {
        var parts = new List<string>();
        if (entry.KeepAsIs)
        {
            parts.Add(_localizer[nameof(Strings.Gen_Review_KeepAsIs)]);
        }

        var grammar = string.Join(", ", new[] { entry.PartOfSpeech, entry.Gender }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (grammar.Length > 0)
        {
            parts.Add(grammar);
        }

        if (!string.IsNullOrWhiteSpace(entry.Note))
        {
            parts.Add(entry.Note);
        }

        return string.Join(" · ", parts);
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasBlank));
        OnPropertyChanged(nameof(CanContinue));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether <c>Continue</c> may run changed (a term emptied or filled, loaded).</summary>
    public event EventHandler? StateChanged;

    partial void OnStateChanged(GlossaryReviewState value) => RaiseState();
}

public enum GlossaryReviewState
{
    Loading,
    Loaded,
    Failed,
}
