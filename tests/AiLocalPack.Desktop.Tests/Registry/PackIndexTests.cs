using AiLocalPack.Core.Registry;
using AiLocalPack.Core.Settings;
using AiLocalPack.Desktop.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace AiLocalPack.Desktop.Tests.Registry;

public sealed class PackIndexTests
{
    private const string EmptyIndex = """{ "schemaVersion": 1, "generated": "2026-11-04T12:00:00Z", "games": {} }""";

    [Fact]
    public async Task Refresh_keeps_the_result_as_current()
    {
        // Arrange
        using var registry = new TempDir().File("index.json", EmptyIndex);
        using var services = TestServices.Build(registry: registry.Path);
        var index = services.GetRequiredService<PackIndex>();

        // Act
        var snapshot = await index.RefreshAsync();

        // Assert
        (index.Current, snapshot.State).Should().Be((snapshot, RegistryIndexState.Fresh));
    }

    [Fact]
    public async Task Refresh_raises_changed()
    {
        // Arrange
        using var services = TestServices.Build();
        var index = services.GetRequiredService<PackIndex>();
        RegistryIndexSnapshot? raised = null;
        index.Changed += (_, s) => raised = s;

        // Act
        var snapshot = await index.RefreshAsync();

        // Assert
        raised.Should().BeSameAs(snapshot);
    }

    [Fact]
    public async Task Request_during_a_refresh_joins_it()
    {
        // Arrange
        using var services = TestServices.Build();
        var index = services.GetRequiredService<PackIndex>();
        var first = index.RefreshAsync();

        // Act
        var second = index.RefreshAsync();

        // Assert
        (second.IsCompleted || ReferenceEquals(first, second)).Should().BeTrue();
        await first;
    }

    [Fact]
    public async Task Hidden_setting_names_the_registry()
    {
        // Arrange
        using var registry = new TempDir().File("index.json", EmptyIndex);
        using var services = TestServices.Build(registry: "");
        services.GetRequiredService<SettingsStore>().Update(s => s with { RegistryIndexUrl = registry.Path });

        // Act
        var snapshot = await services.GetRequiredService<PackIndex>().RefreshAsync();

        // Assert
        snapshot.Location!.LocalIndexPath.Should().Be(Path.Combine(registry.Path, "index.json"));
    }
}
