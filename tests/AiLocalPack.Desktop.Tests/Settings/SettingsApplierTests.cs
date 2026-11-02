using System.Globalization;
using AiLocalPack.Core.Settings;
using AiLocalPack.Core.Storage;
using AiLocalPack.Desktop.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Desktop.Tests.Settings;

public sealed class SettingsApplierTests : IDisposable
{
    private sealed class FakeTheming : IAppTheming
    {
        public List<AppTheme> Applied { get; } = [];

        public void Apply(AppTheme theme) => Applied.Add(theme);
    }

    private readonly TempDir _dir = new();
    private readonly FakeTheming _theming = new();
    private readonly Localizer _localizer = new(CultureInfo.GetCultureInfo("en"));
    private readonly SettingsStore _store;
    private readonly SettingsApplier _applier;

    public SettingsApplierTests()
    {
        _store = new SettingsStore(AppPaths.UnderHome(_dir.Path), NullLogger<SettingsStore>.Instance, new FakeClock(DateTimeOffset.UnixEpoch));
        _applier = new SettingsApplier(_store, _localizer, _theming);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Start_applies_the_loaded_settings()
    {
        // Arrange
        _store.Load();
        _store.Update(s => s with { UiLanguage = "ru", Theme = AppTheme.Dark });

        // Act
        _applier.Start();

        // Assert
        _localizer.Culture.Name.Should().Be("ru");
        _theming.Applied.Should().Equal(AppTheme.Dark);
    }

    [Fact]
    public void A_change_applies_immediately()
    {
        // Arrange
        _store.Load();
        _applier.Start();

        // Act
        _store.Update(s => s with { UiLanguage = "ru", Theme = AppTheme.Light });

        // Assert
        _localizer.Culture.Name.Should().Be("ru");
        _theming.Applied.Should().Equal(AppTheme.System, AppTheme.Light);
    }

    [Fact]
    public void Nothing_is_applied_before_start()
    {
        // Arrange
        _store.Load();

        // Act
        _store.Update(s => s with { Theme = AppTheme.Light });

        // Assert
        _theming.Applied.Should().BeEmpty();
    }

    [Fact]
    public void Starting_twice_applies_each_change_once()
    {
        // Arrange
        _store.Load();
        _applier.Start();
        _applier.Start();

        // Act
        _store.Update(s => s with { Theme = AppTheme.Dark });

        // Assert
        _theming.Applied.Should().Equal(AppTheme.System, AppTheme.Dark);
    }
}
