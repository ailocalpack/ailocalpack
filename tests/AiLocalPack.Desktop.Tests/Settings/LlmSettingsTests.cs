using AiLocalPack.Agents.Llm;
using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Agents;
using AiLocalPack.Desktop.Dialogs;
using AiLocalPack.Desktop.ViewModels.Settings.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Settings;

/// <summary>Settings → AI agents → Your models, and the Add model dialog.</summary>
public sealed class LlmSettingsTests : IDisposable
{
    private readonly ServiceProvider _services = TestServices.Build();

    public void Dispose() => _services.Dispose();

    private FakeLlmHost Llms => _services.GetRequiredService<FakeLlmHost>();

    private SettingsStore Store => _services.GetRequiredService<SettingsStore>();

    private IDialogService Dialogs => _services.GetRequiredService<IDialogService>();

    private AgentsSettingsSection Section() => _services.GetRequiredService<AgentsSettingsSection>();

    private static LlmConnection Qwen(string provider = "ollama", bool hasKey = false) =>
        new("llm-0a1b2c3d", "Qwen 3", provider, provider == "ollama" ? "http://localhost:11434/v1" : "https://openrouter.ai/api/v1", "qwen3:30b", hasKey);

    private LlmDialogViewModel OpenAdd()
    {
        Section().AddModelCommand.Execute(null);
        return (LlmDialogViewModel)Dialogs.Current!;
    }

    [Fact]
    public void Without_models_the_group_offers_to_add_one()
    {
        // Act
        var rows = Section().ModelRows;

        // Assert
        rows.Select(r => r.GetType().Name).Should().Equal(nameof(LlmEmptyRow));
    }

    [Fact]
    public void A_saved_model_is_a_row_followed_by_add()
    {
        // Arrange
        Store.Update(s => s.WithLlm(Qwen()));

        // Act
        var rows = Section().ModelRows;

        // Assert
        rows.Select(r => r.GetType().Name).Should().Equal(nameof(LlmRow), nameof(LlmAddRow));
    }

    [Fact]
    public void A_model_the_server_lists_is_ready_and_says_where_it_runs()
    {
        // Arrange
        Store.Update(s => s.WithLlm(Qwen()));

        // Act
        var row = Section().Models.Single();

        // Assert
        (row.StatusText, row.SourceText, row.PrivacyText).Should().Be(("Ready", "qwen3:30b · localhost:11434", "Runs on this computer"));
    }

    [Fact]
    public void An_ollama_server_that_does_not_answer_says_to_start_ollama()
    {
        // Arrange
        Llms.Probe = new LlmCheck(LlmCheckState.Unreachable, [], "Connection refused");
        Store.Update(s => s.WithLlm(Qwen()));

        // Act
        var row = Section().Models.Single();

        // Assert
        (row.StatusText, row.Help, row.CanCheckAgain).Should().Be(("Not reachable", "Start Ollama, then check again.", true));
    }

    [Fact]
    public void A_cloud_model_without_a_key_is_a_key_problem()
    {
        // Arrange
        Store.Update(s => s.WithLlm(Qwen("openrouter")));

        // Act
        var row = Section().Models.Single();

        // Assert
        (row.State, row.PrivacyText).Should().Be((LlmRowState.KeyProblem, "Text is sent to openrouter.ai"));
    }

    [Fact]
    public void A_ready_model_can_be_the_default_agent()
    {
        // Arrange
        _services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");
        Store.Update(s => s.WithLlm(Qwen()));
        var row = Section().DefaultAgent;

        // Act
        row.SelectedChoice = row.Choices.Single(c => c.AgentId == "llm-0a1b2c3d");

        // Assert
        Store.Current.DefaultAgent.Should().Be("llm-0a1b2c3d");
    }

    [Fact]
    public void Removing_a_model_asks_first()
    {
        // Arrange
        Store.Update(s => s.WithLlm(Qwen()));

        // Act
        Section().Models.Single().RemoveCommand.Execute(null);

        // Assert
        (Dialogs.Current as ConfirmDialogViewModel)?.Title.Should().Be("Remove Qwen 3?");
    }

    [Fact]
    public async Task A_confirmed_remove_drops_the_model_and_its_key()
    {
        // Arrange
        Store.Update(s => s.WithLlm(Qwen("openrouter", hasKey: true)));
        await Llms.SaveKeyAsync("llm-0a1b2c3d", "sk-test", TestContext.Current.CancellationToken);
        Section().Models.Single().RemoveCommand.Execute(null);

        // Act
        ((ConfirmDialogViewModel)Dialogs.Current!).ConfirmCommand.Execute(null);

        // Assert
        (Store.Current.Llms.Count, Llms.Keys.Secrets.Count, Section().ModelRows.Single().GetType().Name).Should().Be((0, 0, nameof(LlmEmptyRow)));
    }

    [Fact]
    public void Add_starts_with_ollama_and_its_models()
    {
        // Act
        var dialog = OpenAdd();

        // Assert
        (dialog.Title, dialog.SelectedProvider.Name, dialog.BaseUrl, dialog.ModelsText, dialog.KeyLabel)
            .Should().Be(("Add a model", "Ollama", "http://localhost:11434/v1", "2 models on the server", "API key (optional)"));
    }

    [Fact]
    public void Picking_a_cloud_preset_fills_its_address_and_asks_for_a_key()
    {
        // Arrange
        var dialog = OpenAdd();

        // Act
        dialog.PickProviderCommand.Execute(dialog.Providers.Single(p => p.Name == "OpenRouter"));

        // Assert
        (dialog.BaseUrl, dialog.KeyLabel, dialog.HasKeysUrl, dialog.PrivacyText)
            .Should().Be(("https://openrouter.ai/api/v1", "API key", true, "Your game's text is sent to openrouter.ai."));
    }

    [Fact]
    public void The_name_follows_the_model_until_the_player_types_one()
    {
        // Arrange
        var dialog = OpenAdd();
        dialog.Model = "qwen/qwen3-30b";
        dialog.Name = "My Qwen";

        // Act
        dialog.Model = "llama3.1:8b";

        // Assert
        dialog.Name.Should().Be("My Qwen");
    }

    [Fact]
    public void A_picked_model_names_itself_without_its_vendor()
    {
        // Arrange
        var dialog = OpenAdd();

        // Act
        dialog.Model = "qwen/qwen3-30b";

        // Assert
        dialog.Name.Should().Be("qwen3-30b");
    }

    [Fact]
    public async Task Save_stores_the_model_and_puts_the_key_in_the_keychain()
    {
        // Arrange
        var dialog = OpenAdd();
        dialog.PickProviderCommand.Execute(dialog.Providers.Single(p => p.Name == "OpenRouter"));
        dialog.ApiKey = "sk-or-test";
        dialog.Model = "qwen/qwen3-30b";

        // Act
        await dialog.SaveCommand.ExecuteAsync(null);

        // Assert
        var saved = Store.Current.Llms.Single();
        (saved.Provider, saved.BaseUrl, saved.Model, saved.HasKey, Llms.Keys.Secrets[LlmConnections.SecretName(saved.Id)], Dialogs.Current)
            .Should().Be(("openrouter", "https://openrouter.ai/api/v1", "qwen/qwen3-30b", true, "sk-or-test", (DialogViewModel?)null));
    }

    [Fact]
    public async Task Save_with_a_broken_address_shows_why_and_saves_nothing()
    {
        // Arrange
        var dialog = OpenAdd();
        dialog.BaseUrl = "localhost:11434";
        dialog.Model = "qwen3:30b";

        // Act
        await dialog.SaveCommand.ExecuteAsync(null);

        // Assert
        (dialog.UrlError, Store.Current.Llms.Count).Should().Be(("Enter a full address starting with http:// or https://, e.g. http://localhost:11434/v1.", 0));
    }

    [Fact]
    public async Task A_keychain_that_refuses_the_key_keeps_the_dialog_open()
    {
        // Arrange
        Llms.Keys.FailWith = Core.Secrets.SecretResult.Unavailable("no keyring");
        var dialog = OpenAdd();
        dialog.ApiKey = "sk-test";
        dialog.Model = "qwen3:30b";

        // Act
        await dialog.SaveCommand.ExecuteAsync(null);

        // Assert
        (dialog.KeyError is not null, Store.Current.Llms.Count, Dialogs.Current).Should().Be((true, 0, (DialogViewModel?)dialog));
    }

    [Fact]
    public async Task Check_says_when_the_model_does_not_call_tools()
    {
        // Arrange
        Llms.Test = new LlmCheck(LlmCheckState.NoToolCalls, ["gemma:2b"]);
        var dialog = OpenAdd();
        dialog.Model = "gemma:2b";

        // Act
        await dialog.CheckCommand.ExecuteAsync(null);

        // Assert
        (dialog.IsCheckWarn, dialog.CheckText).Should().Be((true,
            "Connected, but the model answered without calling a tool. Translating needs tool calling: pick a model that supports it."));
    }

    [Fact]
    public async Task Editing_keeps_the_saved_key_unless_it_is_removed()
    {
        // Arrange
        Store.Update(s => s.WithLlm(Qwen("openrouter", hasKey: true)));
        await Llms.SaveKeyAsync("llm-0a1b2c3d", "sk-old", TestContext.Current.CancellationToken);
        Section().Models.Single().EditCommand.Execute(null);
        var dialog = (LlmDialogViewModel)Dialogs.Current!;
        dialog.Name = "Qwen remote";

        // Act
        await dialog.SaveCommand.ExecuteAsync(null);

        // Assert
        (Store.Current.Llms.Single().Name, Store.Current.Llms.Single().HasKey, Llms.Keys.Secrets.Values.Single()).Should().Be(("Qwen remote", true, "sk-old"));
    }
}
