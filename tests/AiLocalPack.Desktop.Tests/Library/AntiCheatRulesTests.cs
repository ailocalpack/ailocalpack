using AiLocalPack.Core.AntiCheat;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Library;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Library;

/// <summary>The anti-cheat policy the app uses: bundled + registry, and which rules the catalog was checked with (task 65).</summary>
public sealed class AntiCheatRulesTests : IDisposable
{
    private readonly TestRegistry _registry = new();

    public void Dispose() => _registry.Dispose();

    private async Task<ServiceProvider> BuildAsync()
    {
        _registry.Write();
        var services = TestServices.Build(registry: _registry.Path);
        await services.GetRequiredService<PackIndex>().RefreshAsync();
        return services;
    }

    [Fact]
    public async Task Current_policy_has_the_registry_rules_on_top()
    {
        // Arrange
        _registry.AntiCheatRules.Add(new AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard/"]));
        using var services = await BuildAsync();

        // Act
        var policy = services.GetRequiredService<AntiCheatRules>().Current;

        // Assert
        policy.Find("acme-guard")!.Name.Should().Be("Acme Guard");
    }

    [Fact]
    public void Before_a_pack_list_the_bundled_policy_is_used()
    {
        // Arrange
        using var services = TestServices.Build(registry: _registry.Path);

        // Act
        var policy = services.GetRequiredService<AntiCheatRules>().Current;

        // Assert
        policy.Should().BeSameAs(AntiCheatPolicy.Bundled);
    }

    [Fact]
    public async Task Library_refresh_remembers_the_rules_it_checked_with()
    {
        // Arrange
        using var services = await BuildAsync();
        var rules = services.GetRequiredService<AntiCheatRules>();

        // Act
        await services.GetRequiredService<GameLibrary>().RefreshAsync();

        // Assert
        rules.IsChecked(rules.Current.RulesStamp).Should().BeTrue();
    }

    [Fact]
    public async Task Checked_rules_survive_a_restart()
    {
        // Arrange
        using var services = await BuildAsync();
        services.GetRequiredService<AntiCheatRules>().MarkChecked("0123456789abcdef");
        var rules = new AntiCheatRules(AntiCheatPolicy.Bundled, services.GetRequiredService<PackIndex>(), services.GetRequiredService<AppPaths>(), new TestLogger<AntiCheatRules>());

        // Act
        var isChecked = rules.IsChecked("0123456789abcdef");

        // Assert
        isChecked.Should().BeTrue();
    }

    [Fact]
    public async Task Damaged_file_means_not_checked()
    {
        // Arrange
        using var services = await BuildAsync();
        var paths = services.GetRequiredService<AppPaths>();
        Directory.CreateDirectory(paths.Cache);
        File.WriteAllText(paths.AntiCheatRulesFile, "{ not json");
        var rules = new AntiCheatRules(AntiCheatPolicy.Bundled, services.GetRequiredService<PackIndex>(), paths, new TestLogger<AntiCheatRules>());

        // Act
        var isChecked = rules.IsChecked(AntiCheatPolicy.Bundled.RulesStamp);

        // Assert
        isChecked.Should().BeFalse();
    }

    [Fact]
    public async Task Pack_list_with_new_rules_asks_for_a_new_check()
    {
        // Arrange
        using var services = await BuildAsync();
        var rules = services.GetRequiredService<AntiCheatRules>();
        rules.MarkChecked(rules.Current.RulesStamp);
        var raised = 0;
        rules.RulesChanged += (_, _) => raised++;
        _registry.AntiCheatRules.Add(new AntiCheatRule("acme-guard", "Acme Guard", ["AcmeGuard/"]));
        _registry.Write();

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        raised.Should().Be(1);
    }

    [Fact]
    public async Task Pack_list_with_the_same_rules_asks_for_nothing()
    {
        // Arrange
        using var services = await BuildAsync();
        var rules = services.GetRequiredService<AntiCheatRules>();
        rules.MarkChecked(rules.Current.RulesStamp);
        var raised = 0;
        rules.RulesChanged += (_, _) => raised++;
        _registry.OptOut(10).Write();

        // Act
        await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        raised.Should().Be(0);
    }
}
