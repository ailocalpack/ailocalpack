using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiLocalPack.Desktop.Tests.Settings;

// On the headless UI thread: the preload hops to a pool thread, which must not be the first to touch Avalonia's dispatcher.
public sealed class SettingsPreloaderTests
{
    [Fact]
    public Task Preload_creates_the_settings_sections_before_the_first_visit() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        var created = false;
        using var services = TestServices.Build(s => OnlySection(s, sp =>
        {
            created = true;
            return new SettingsSection(SettingsSectionId.About, "IconInfo", "Settings_About_Title", "Settings_About_Description", sp.GetRequiredService<Localizer>());
        }));
        var preloader = services.GetRequiredService<SettingsPreloader>();

        // Act
        await preloader.PreloadAsync();

        // Assert
        created.Should().BeTrue();
    });

    [Fact]
    public Task Preload_does_not_throw_when_a_section_cannot_be_created() => HeadlessUi.RunAsync(async () =>
    {
        // Arrange
        using var services = TestServices.Build(s => OnlySection(s, _ => throw new InvalidOperationException("broken section")));
        var preloader = services.GetRequiredService<SettingsPreloader>();

        // Act
        var act = preloader.PreloadAsync;

        // Assert
        await act.Should().NotThrowAsync();
    });

    // Only a test section: the immediate test dispatcher would build the real ones on a pool thread.
    private static void OnlySection(IServiceCollection services, Func<IServiceProvider, SettingsSection> section)
    {
        services.RemoveAll<SettingsSection>();
        services.AddSingleton(section);
    }
}
