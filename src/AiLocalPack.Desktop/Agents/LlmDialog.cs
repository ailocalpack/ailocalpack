using System.Collections.ObjectModel;
using AiLocalPack.Agents.Llm;
using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiLocalPack.Desktop.Agents;

/// <summary>Which model the dialog edits: <c>null</c> adds one; <paramref name="Saved"/> runs after a save (the new connection).</summary>
public sealed record LlmDialogRequest(LlmConnection? Existing, Action<LlmConnection>? Saved = null);

/// <summary>One server preset tile of the dialog.</summary>
public sealed partial class LlmProviderOption(LlmProvider provider, string name) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public LlmProvider Provider { get; } = provider;

    public string Name { get; } = name;

    public AgentInfo Info { get; } = AgentInfo.ForLlm(new LlmConnection("llm-preview", name, provider.Id, provider.BaseUrl, string.Empty));
}

/// <summary>How the last check went, for the result line's colour and icon.</summary>
public enum LlmCheckTone
{
    None,
    Busy,
    Ok,
    Warn,
    Bad,
}

/// <summary>
/// <em>Add model</em> / <em>Edit model</em> (Settings → AI agents → <em>Your models</em>, the start form's add tile):
/// <list type="number">
/// <item>Where it runs: a tile per preset (Ollama, LM Studio, OpenRouter, OpenAI, Other) that fills the address.</item>
/// <item>Server address (checked when it loses focus and on save), API key (masked with a show / hide toggle; "optional" for a
/// local or other server; on edit a saved key stays unless typed over or removed), Model (typed, or picked from the list
/// the server gives: loaded when the dialog opens and on <c>Load</c>), Name (follows the model until the player types one).</item>
/// <item><c>Check connection</c>: lists the models and asks the model to call a test tool; the result in plain words
/// (connected and calls tools / doesn't call tools / not reachable / key refused / model missing).</item>
/// </list>
/// <c>Save</c> needs an address, a model and a name, not a passed check (a server may be off right now); the key goes to
/// the keychain first and a refusal there keeps the dialog open with the reason.
/// </summary>
public sealed partial class LlmDialogViewModel : DialogViewModel
{
    private readonly LlmDialogRequest _request;
    private readonly ILlmHost _host;
    private readonly SettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private readonly ILogger<LlmDialogViewModel> _logger;
    private CancellationTokenSource? _busy;
    private bool _nameEdited;
    private bool _settingName;

    public LlmDialogViewModel(LlmDialogRequest request, ILlmHost host, SettingsStore settings, IDialogService dialogs, Localizer localizer, ILogger<LlmDialogViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _host = host;
        _settings = settings;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;
        Models.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasModels));
        Providers = [.. LlmProviders.All.Select(p => new LlmProviderOption(p, p.Name.Length > 0 ? p.Name : localizer[nameof(Strings.Llm_Provider_Custom)]))];
        if (request.Existing is { } existing)
        {
            _baseUrl = existing.BaseUrl;
            _model = existing.Model;
            _name = existing.Name;
            _nameEdited = true;
            Select(Providers.FirstOrDefault(p => p.Provider.Id == LlmProviders.Of(existing.Provider).Id) ?? Providers[^1], fillUrl: false);
        }
        else
        {
            Select(Providers[0], fillUrl: true);
        }

        _ = LoadModelsAsync();
    }

    public bool IsEdit => _request.Existing is not null;

    public string Title => _localizer[IsEdit ? nameof(Strings.Llm_Dialog_EditTitle) : nameof(Strings.Llm_Dialog_AddTitle)];

    public IReadOnlyList<LlmProviderOption> Providers { get; }

    public LlmProviderOption SelectedProvider { get; private set; } = null!;

    // ---------- Address ----------

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    /// <summary>The address lost focus once: its error shows from then on.</summary>
    [ObservableProperty]
    private bool _urlTouched;

    public string? UrlError => (UrlTouched || _saveTried) && LlmConnections.NormalizeUrl(BaseUrl) is null
        ? _localizer[string.IsNullOrWhiteSpace(BaseUrl) ? nameof(Strings.Llm_Dialog_Url_Missing) : nameof(Strings.Llm_Dialog_Url_Invalid)]
        : null;

    public bool HasUrlError => UrlError is not null;

    /// <summary>"Ollama listens here once it runs." / "Any server with the OpenAI API, up to /v1.".</summary>
    public string UrlHint => SelectedProvider.Provider.Id switch
    {
        "ollama" => _localizer[nameof(Strings.Llm_Dialog_Url_Hint_Ollama)],
        "lmstudio" => _localizer[nameof(Strings.Llm_Dialog_Url_Hint_LmStudio)],
        LlmProviders.Custom => _localizer[nameof(Strings.Llm_Dialog_Url_Hint_Custom)],
        _ => _localizer[nameof(Strings.Llm_Dialog_Url_Hint_Cloud)],
    };

    // ---------- Key ----------

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _isKeyVisible;

    /// <summary>The player removed the saved key (edit).</summary>
    [ObservableProperty]
    private bool _keyRemoved;

    [ObservableProperty]
    private string? _keyError;

    public bool NeedsKey => SelectedProvider.Provider.NeedsKey;

    public string KeyLabel => _localizer[NeedsKey ? nameof(Strings.Llm_Dialog_Key) : nameof(Strings.Llm_Dialog_KeyOptional)];

    public bool HasSavedKey => _request.Existing?.HasKey == true && !KeyRemoved;

    public string KeyPlaceholder => HasSavedKey ? _localizer[nameof(Strings.Llm_Dialog_Key_Saved)] : NeedsKey ? "sk-…" : string.Empty;

    public string KeyHint => _localizer[nameof(Strings.Llm_Dialog_Key_Hint)];

    public bool CanRemoveKey => HasSavedKey && string.IsNullOrEmpty(ApiKey);

    public string? KeysUrl => SelectedProvider.Provider.KeysUrl;

    public bool HasKeysUrl => KeysUrl is not null;

    public string KeyToggleName => _localizer[IsKeyVisible ? nameof(Strings.Llm_Dialog_Key_Hide) : nameof(Strings.Llm_Dialog_Key_Show)];

    // ---------- Model ----------

    [ObservableProperty]
    private string _model = string.Empty;

    public ObservableCollection<string> Models { get; } = [];

    /// <summary>The server gave a list to pick from.</summary>
    public bool HasModels => Models.Count > 0;

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private string? _modelsText;

    [ObservableProperty]
    private bool _modelsFailed;

    public bool HasModelsText => ModelsText is not null;

    public string? ModelError => _saveTried && string.IsNullOrWhiteSpace(Model) ? _localizer[nameof(Strings.Llm_Dialog_Model_Missing)] : null;

    public bool HasModelError => ModelError is not null;

    // ---------- Name ----------

    [ObservableProperty]
    private string _name = string.Empty;

    public string? NameError => _saveTried && string.IsNullOrWhiteSpace(Name) ? _localizer[nameof(Strings.Llm_Dialog_Name_Missing)] : null;

    public bool HasNameError => NameError is not null;

    public int MaxNameLength => LlmConnections.MaxNameLength;

    // ---------- Check ----------

    [ObservableProperty]
    private LlmCheckTone _checkTone;

    [ObservableProperty]
    private string? _checkText;

    public bool HasCheck => CheckTone != LlmCheckTone.None;

    public bool IsChecking => CheckTone == LlmCheckTone.Busy;

    public bool IsCheckOk => CheckTone == LlmCheckTone.Ok;

    public bool IsCheckWarn => CheckTone == LlmCheckTone.Warn;

    public bool IsCheckBad => CheckTone == LlmCheckTone.Bad;

    /// <summary>"Your game's text stays on this computer." / "Your game's text is sent to openrouter.ai.".</summary>
    public string PrivacyText => LlmConnections.NormalizeUrl(BaseUrl) is { } url
        ? LlmConnections.IsLocalUrl(url)
            ? _localizer[nameof(Strings.Llm_Dialog_Privacy_Local)]
            : _localizer.Format(nameof(Strings.Llm_Dialog_Privacy_Remote), new LlmConnection("x", "x", "x", url, "x").Host)
        : _localizer[nameof(Strings.Llm_Dialog_Privacy_Unknown)];

    public bool IsLocal => LlmConnections.IsLocalUrl(LlmConnections.NormalizeUrl(BaseUrl));

    [ObservableProperty]
    private bool _isSaving;

    private bool _saveTried;

    public override bool TryHandleEscape()
    {
        Cancel();
        return true;
    }

    public override void OnClosed()
    {
        _busy?.Cancel();
        base.OnClosed();
    }

    [RelayCommand]
    private void PickProvider(LlmProviderOption option)
    {
        if (option == SelectedProvider)
        {
            return;
        }

        Select(option, fillUrl: true);
        _ = LoadModelsAsync();
    }

    [RelayCommand]
    private void ToggleKey() => IsKeyVisible = !IsKeyVisible;

    [RelayCommand]
    private void RemoveKey()
    {
        KeyRemoved = true;
        ApiKey = string.Empty;
    }

    [RelayCommand]
    private Task LoadModels() => LoadModelsAsync();

    /// <summary>The address lost focus: its error may show, and the list follows the new address.</summary>
    public void OnUrlLeft()
    {
        UrlTouched = true;
        _ = LoadModelsAsync();
    }

    [RelayCommand]
    private async Task Check()
    {
        if (LlmConnections.NormalizeUrl(BaseUrl) is not { } url)
        {
            UrlTouched = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            SetCheck(LlmCheckTone.Bad, _localizer[nameof(Strings.Llm_Dialog_Model_Missing)]);
            return;
        }

        var token = Restart();
        SetCheck(LlmCheckTone.Busy, _localizer[nameof(Strings.Llm_Dialog_Check_Busy)]);
        LlmCheck check;
        try
        {
            check = await _host.TestAsync(url, TypedKey, KeyRemoved ? null : _request.Existing, Model.Trim(), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        ShowModels(check);
        var (tone, text) = check.State switch
        {
            LlmCheckState.Ready => (LlmCheckTone.Ok, _localizer[nameof(Strings.Llm_Dialog_Check_Ok)]),
            LlmCheckState.NoToolCalls => (LlmCheckTone.Warn, _localizer[nameof(Strings.Llm_Dialog_Check_NoTools)]),
            LlmCheckState.ModelMissing => (LlmCheckTone.Bad, _localizer.Format(nameof(Strings.Llm_Dialog_Check_ModelMissing), Model.Trim())),
            LlmCheckState.Unreachable => (LlmCheckTone.Bad, _localizer.Format(nameof(Strings.Llm_Dialog_Check_Unreachable), url)),
            LlmCheckState.KeyRefused => (LlmCheckTone.Bad, _localizer[nameof(Strings.Llm_Dialog_Check_KeyRefused)]),
            _ => (LlmCheckTone.Bad, _localizer.Format(nameof(Strings.Llm_Dialog_Check_Failed), check.Detail ?? string.Empty)),
        };
        SetCheck(tone, text);
    }

    [RelayCommand]
    private async Task Save()
    {
        _saveTried = true;
        RaiseErrors();
        if (LlmConnections.NormalizeUrl(BaseUrl) is not { } url || string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(Name) || IsSaving)
        {
            return;
        }

        IsSaving = true;
        KeyError = null;
        try
        {
            var id = _request.Existing?.Id ?? LlmConnections.NewId();
            var hasKey = HasSavedKey;
            if (TypedKey is { } key)
            {
                var saved = await _host.SaveKeyAsync(id, key);
                if (!saved.IsOk)
                {
                    _logger.LogWarning("Model {Id}: the API key can't be saved: {Problem}", id, saved);
                    KeyError = _localizer[nameof(Strings.Llm_Dialog_Key_SaveFailed)];
                    return;
                }

                hasKey = true;
            }
            else if (KeyRemoved && _request.Existing?.HasKey == true)
            {
                await _host.DeleteKeyAsync(id);
                hasKey = false;
            }

            var connection = new LlmConnection(id, Name.Trim(), SelectedProvider.Provider.Id, url, Model.Trim(), hasKey);
            _settings.Update(s => s.WithLlm(connection));
            AgentInfo.Remember([connection]);
            _logger.LogInformation("Model {Id} saved: {Model} at {Host}", id, connection.Model, connection.Host);
            _dialogs.Close(this);
            _request.Saved?.Invoke(connection);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "The model can't be saved");
            KeyError = e.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _dialogs.Close(this);

    private string? TypedKey => string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();

    private void Select(LlmProviderOption option, bool fillUrl)
    {
        foreach (var p in Providers)
        {
            p.IsSelected = p == option;
        }

        SelectedProvider = option;
        if (fillUrl && (option.Provider.BaseUrl.Length > 0 || Providers.Any(p => p.Provider.BaseUrl == BaseUrl)))
        {
            BaseUrl = option.Provider.BaseUrl;
            UrlTouched = false;
        }

        Models.Clear();
        ModelsText = null;
        SetCheck(LlmCheckTone.None, null);
        foreach (var name in (string[])[nameof(SelectedProvider), nameof(UrlHint), nameof(NeedsKey), nameof(KeyLabel), nameof(KeyPlaceholder), nameof(KeysUrl), nameof(HasKeysUrl)])
        {
            OnPropertyChanged(name);
        }
    }

    private async Task LoadModelsAsync()
    {
        if (LlmConnections.NormalizeUrl(BaseUrl) is not { } url || (NeedsKey && TypedKey is null && !HasSavedKey))
        {
            return;
        }

        var token = Restart();
        IsLoadingModels = true;
        ModelsText = null;
        try
        {
            var check = await _host.ListModelsAsync(url, TypedKey, KeyRemoved ? null : _request.Existing, token);
            if (!token.IsCancellationRequested)
            {
                ShowModels(check);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer load or check took over.
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingModels = false;
            }
        }
    }

    private void ShowModels(LlmCheck check)
    {
        IsLoadingModels = false;
        if (check.State is LlmCheckState.Unreachable or LlmCheckState.KeyRefused or LlmCheckState.Failed && check.Models.Count == 0)
        {
            ModelsFailed = true;
            ModelsText = check.State switch
            {
                LlmCheckState.Unreachable => _localizer[nameof(Strings.Llm_Dialog_Models_Unreachable)],
                LlmCheckState.KeyRefused => _localizer[nameof(Strings.Llm_Dialog_Check_KeyRefused)],
                _ => _localizer[nameof(Strings.Llm_Dialog_Models_Failed)],
            };
            return;
        }

        ModelsFailed = false;
        Models.Clear();
        foreach (var m in check.Models)
        {
            Models.Add(m);
        }

        ModelsText = check.Models.Count == 0
            ? _localizer[nameof(Strings.Llm_Dialog_Models_None)]
            : _localizer.Plural(nameof(Strings.Llm_Dialog_Models_One), check.Models.Count);
        if (string.IsNullOrWhiteSpace(Model) && check.Models.Count == 1)
        {
            Model = check.Models[0];
        }
    }

    private CancellationToken Restart()
    {
        _busy?.Cancel();
        _busy = new CancellationTokenSource();
        return _busy.Token;
    }

    private void SetCheck(LlmCheckTone tone, string? text)
    {
        CheckTone = tone;
        CheckText = text;
    }

    private void RaiseErrors()
    {
        foreach (var name in (string[])[nameof(UrlError), nameof(HasUrlError), nameof(ModelError), nameof(HasModelError), nameof(NameError), nameof(HasNameError)])
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnBaseUrlChanged(string value)
    {
        SetCheck(LlmCheckTone.None, null);
        RaiseErrors();
        OnPropertyChanged(nameof(PrivacyText));
        OnPropertyChanged(nameof(IsLocal));
    }

    partial void OnUrlTouchedChanged(bool value) => RaiseErrors();

    partial void OnApiKeyChanged(string value)
    {
        SetCheck(LlmCheckTone.None, null);
        OnPropertyChanged(nameof(CanRemoveKey));
    }

    partial void OnKeyRemovedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSavedKey));
        OnPropertyChanged(nameof(KeyPlaceholder));
        OnPropertyChanged(nameof(CanRemoveKey));
    }

    partial void OnIsKeyVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyToggleName));
    }

    partial void OnModelChanged(string value)
    {
        SetCheck(LlmCheckTone.None, null);
        RaiseErrors();
        if (!_nameEdited)
        {
            _settingName = true;
            var trimmed = value.Trim();
            Name = trimmed.Length == 0 ? string.Empty : trimmed[(trimmed.LastIndexOf('/') + 1)..];
            _settingName = false;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_settingName)
        {
            _nameEdited = value.Length > 0;
        }

        RaiseErrors();
    }

    partial void OnCheckToneChanged(LlmCheckTone value)
    {
        foreach (var name in (string[])[nameof(HasCheck), nameof(IsChecking), nameof(IsCheckOk), nameof(IsCheckWarn), nameof(IsCheckBad)])
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnModelsTextChanged(string? value) => OnPropertyChanged(nameof(HasModelsText));
}
