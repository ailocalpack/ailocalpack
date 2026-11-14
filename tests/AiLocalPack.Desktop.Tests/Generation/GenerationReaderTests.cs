using AiLocalPack.Core.Games;
using AiLocalPack.Core.Generation;
using AiLocalPack.Core.Library;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Navigation;
using AiLocalPack.Desktop.Tests.Settings;
using AiLocalPack.Desktop.ViewModels;
using AiLocalPack.Desktop.ViewModels.GamePage;
using AiLocalPack.Toolkit.Engines;
using AiLocalPack.Toolkit.Generation;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Generation;

/// <summary>
/// The start form's reader (decided 2026-11-14): "… into Русский [with Unity toolkit ▾]", the detected engine's toolkit
/// by default, any other engine's or the AI's own code on request.
/// </summary>
public sealed class GenerationReaderTests
{
    /// <summary>Steam game 10 "Alpha"; the fake estimate finds a Unity game the toolkit reads.</summary>
    private static ServiceProvider Build()
    {
        var services = TestServices.Build(steam: s => s.AddGame(10, "Alpha"));
        services.GetRequiredService<SettingsStore>().Update(s => s with { GameLanguage = "ru" });
        services.GetRequiredService<FakeAgentHost>().Probes["claude"] = FakeAgentHost.SignedIn("claude");
        return services;
    }

    private static GenerationTabViewModel Open(ServiceProvider services) =>
        services.GetRequiredService<IPageFactory>().Create<GamePageViewModel>(
            services.GetRequiredService<GameLibrary>().Catalog.Games.Single(g => g.Id == GameId.Steam(10)),
            GamePageTab.Generation).Generation;

    private static GenerationReaderOption Option(GenerationTabViewModel tab, string id) => tab.ReaderOptions.Single(o => o.Id == id);

    [Fact]
    public void The_detected_engines_toolkit_is_chosen_by_default()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        (tab.ReaderName, tab.ReaderOptions.Single(o => o.IsSelected).Id, tab.ReaderBrandKey).Should().Be(("Unity toolkit", "unity", "BrandUnity"));
    }

    [Fact]
    public void The_detected_engine_is_marked_in_the_list()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        tab.ReaderOptions.Where(o => o.IsDetected).Select(o => o.Id).Should().Equal("unity");
    }

    [Fact]
    public void The_list_has_every_toolkit_engine_and_the_ais_own_code_last()
    {
        // Arrange
        using var services = Build();

        // Act
        var tab = Open(services);

        // Assert
        tab.ReaderOptions.Select(o => o.Id).Should().BeEquivalentTo("godot", "rpgmaker", "renpy", "unity", "unreal", GenerationOptions.AgentReader)
            .And.EndWith(GenerationOptions.AgentReader);
    }

    [Fact]
    public void Picking_the_ais_own_code_estimates_again_that_way()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        var estimates = services.GetRequiredService<FakeEstimates>();
        var calls = estimates.Calls;

        // Act
        Option(tab, GenerationOptions.AgentReader).SelectCommand.Execute(null);

        // Assert
        (estimates.Calls - calls, estimates.LastReader, tab.ReaderName).Should().Be((1, GenerationOptions.AgentReader, "AI's own code"));
    }

    [Fact]
    public async Task Start_passes_the_chosen_engine_to_the_job()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        Option(tab, "godot").SelectCommand.Execute(null);

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        services.GetRequiredService<FakeGenerationStages>().Jobs.Single().Options.Reader.Should().Be("godot");
    }

    [Fact]
    public async Task Start_with_the_detected_engine_leaves_the_reader_to_detection()
    {
        // Arrange
        using var services = Build();
        var tab = Open(services);
        Option(tab, "godot").SelectCommand.Execute(null);
        Option(tab, "unity").SelectCommand.Execute(null);

        // Act
        await tab.StartCommand.ExecuteAsync(null);

        // Assert
        services.GetRequiredService<FakeGenerationStages>().Jobs.Single().Options.Reader.Should().BeNull();
    }

    [Fact]
    public void An_unknown_engine_chooses_the_ais_own_code()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeEstimates>().Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(source: TextSizeSource.UnknownEngine, engine: EngineKind.Unknown));

        // Act
        var tab = Open(services);

        // Assert
        (tab.ReaderName, tab.ReaderOptions.Any(o => o.IsDetected)).Should().Be(("AI's own code", false));
    }

    [Fact]
    public void The_ais_own_code_is_a_warning_in_the_checks()
    {
        // Arrange
        using var services = Build();
        services.GetRequiredService<FakeEstimates>().Estimate = (_, _) => Task.FromResult(FakeEstimates.Of(source: TextSizeSource.AgentChosen));

        // Act
        var tab = Open(services);

        // Assert
        tab.Checks.Should().Contain(c => c.IsWarning && c.Text.StartsWith("You chose the AI's own code", StringComparison.Ordinal));
    }
}
