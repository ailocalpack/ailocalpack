using AiLocalPack.Core.Llm;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Core.Tests.Llm;

public sealed class LlmConnectionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static LlmConnection Ollama(string id = "llm-0a1b2c3d") => new(id, "Qwen", "ollama", "http://localhost:11434/v1", "qwen3:30b");

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    [InlineData("  https://openrouter.ai/api/v1/  ", "https://openrouter.ai/api/v1")]
    [InlineData("http://localhost:1234/v1/chat/completions", "http://localhost:1234/v1")]
    [InlineData("https://api.openai.com/v1/models", "https://api.openai.com/v1")]
    public void A_server_address_is_stored_as_its_base(string typed, string expected)
    {
        // Act
        var url = LlmConnections.NormalizeUrl(typed);

        // Assert
        url.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:11434")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("https://user:pass@example.com/v1")]
    [InlineData("https://example.com/v1?key=1")]
    public void An_address_that_is_not_a_plain_http_url_is_refused(string typed)
    {
        // Act
        var url = LlmConnections.NormalizeUrl(typed);

        // Assert
        url.Should().BeNull();
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    public void A_loopback_server_is_local(string url, bool local)
    {
        // Act
        var isLocal = LlmConnections.IsLocalUrl(url);

        // Assert
        isLocal.Should().Be(local);
    }

    [Fact]
    public void A_new_id_names_a_model_of_the_player()
    {
        // Act
        var id = LlmConnections.NewId();

        // Assert
        (LlmConnections.IsLlmId(id), id.Length).Should().Be((true, 12));
    }

    [Fact]
    public void A_host_without_the_default_port_keeps_its_port()
    {
        // Act
        var host = Ollama().Host;

        // Assert
        host.Should().Be("localhost:11434");
    }

    [Fact]
    public void An_unknown_preset_reads_as_another_server()
    {
        // Act
        var provider = LlmProviders.Of("newcloud");

        // Assert
        provider.Should().Be(LlmProviders.Other);
    }

    [Fact]
    public void Adding_a_model_with_a_known_id_replaces_it_in_place()
    {
        // Arrange
        var settings = AppSettings.Defaults.WithLlm(Ollama("llm-00000001")).WithLlm(Ollama("llm-00000002"));

        // Act
        var changed = settings.WithLlm(Ollama("llm-00000001") with { Model = "llama3.1:8b" });

        // Assert
        changed.Llms.Select(c => (c.Id, c.Model)).Should().Equal(("llm-00000001", "llama3.1:8b"), ("llm-00000002", "qwen3:30b"));
    }

    [Fact]
    public void Removing_the_default_model_clears_the_default_agent()
    {
        // Arrange
        var settings = AppSettings.Defaults.WithLlm(Ollama()) with { DefaultAgent = "llm-0a1b2c3d" };

        // Act
        var changed = settings.WithoutLlm("llm-0a1b2c3d");

        // Assert
        (changed.LlmConnections, changed.DefaultAgent).Should().Be(((IReadOnlyList<LlmConnection>?)null, (string?)null));
    }

    [Fact]
    public void Models_survive_a_restart_without_their_derived_fields()
    {
        // Arrange
        var paths = AppPaths.UnderHome(_dir.Path);
        new SettingsStore(paths, new TestLogger<SettingsStore>(), new FakeClock(DateTimeOffset.UnixEpoch)).Update(s => s.WithLlm(Ollama() with { HasKey = true }));

        // Act
        var loaded = new SettingsStore(paths, new TestLogger<SettingsStore>(), new FakeClock(DateTimeOffset.UnixEpoch)).Load();

        // Assert
        (loaded.Llms.Single(), File.ReadAllText(paths.SettingsFile).Contains("\"host\"", StringComparison.Ordinal))
            .Should().Be((Ollama() with { HasKey = true }, false));
    }
}
