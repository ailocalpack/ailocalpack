using System.ComponentModel;
using System.Globalization;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.Tests;

public sealed class LocalizerTests
{
    private static Localizer Create(string culture) => new(CultureInfo.GetCultureInfo(culture));

    [Fact]
    public void Returns_the_string_in_the_current_language()
    {
        // Arrange
        var localizer = Create("ru-RU");

        // Act
        var title = localizer[nameof(Strings.Library_Title)];

        // Assert
        title.Should().Be("Библиотека");
    }

    [Fact]
    public void Unknown_culture_gets_english_strings()
    {
        // Arrange
        var localizer = Create("de-DE");

        // Act
        var title = localizer[nameof(Strings.Library_Title)];

        // Assert
        title.Should().Be("Library");
    }

    [Fact]
    public void Missing_key_shows_the_key_in_brackets()
    {
        // Arrange
        var localizer = Create("en");

        // Act
        var text = localizer["No_Such_Key"];

        // Assert
        text.Should().Be("[No_Such_Key]");
    }

    [Theory]
    [InlineData("en", 1, "1 game")]
    [InlineData("en", 2, "2 games")]
    [InlineData("en", 21, "21 games")]
    [InlineData("ru", 1, "1 игра")]
    [InlineData("ru", 2, "2 игры")]
    [InlineData("ru", 5, "5 игр")]
    [InlineData("ru", 11, "11 игр")]
    [InlineData("ru", 21, "21 игра")]
    [InlineData("ru", 22, "22 игры")]
    public void Plural_picks_the_form_for_the_count(string culture, long count, string expected)
    {
        // Arrange
        var localizer = Create(culture);

        // Act
        var text = localizer.Plural(nameof(Strings.Library_GameCount_One), count);

        // Assert
        text.Should().Be(expected);
    }

    [Theory]
    [InlineData("en-US", "1,234 games")]
    [InlineData("ru-RU", "1 234 игры")]
    public void Plural_formats_the_count_for_the_culture(string culture, string expected)
    {
        // Arrange
        var localizer = Create(culture);

        // Act
        var text = localizer.Plural(nameof(Strings.Library_GameCount_One), 1234);

        // Assert
        text.Replace(' ', ' ').Should().Be(expected);
    }

    [Fact]
    public void Format_fills_placeholders()
    {
        // Arrange
        var localizer = Create("en");

        // Act
        var text = localizer.Format(nameof(Strings.Chrome_BackTo), "Library");

        // Assert
        text.Should().Be("‹ Library");
    }

    [Fact]
    public void Switching_language_raises_culture_changed_and_indexer_change()
    {
        // Arrange
        var localizer = Create("en");
        var changed = new List<string?>();
        var raised = 0;
        localizer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        localizer.CultureChanged += (_, _) => raised++;

        // Act
        localizer.SetCulture(CultureInfo.GetCultureInfo("ru"));

        // Assert
        raised.Should().Be(1);
        changed.Should().Contain([nameof(Localizer.Culture), "Item[]"]);
        localizer[nameof(Strings.Settings_Title)].Should().Be("Настройки");
    }

    [Fact]
    public void Switching_to_the_same_culture_raises_nothing()
    {
        // Arrange
        var localizer = Create("ru");
        var raised = 0;
        localizer.CultureChanged += (_, _) => raised++;

        // Act
        localizer.SetCulture(CultureInfo.GetCultureInfo("ru"));

        // Assert
        raised.Should().Be(0);
    }

    [Fact]
    public void Set_language_with_unsupported_tag_falls_back_to_english_or_os()
    {
        // Arrange
        var localizer = Create("ru");

        // Act
        localizer.SetLanguage("xx-not-real");

        // Assert
        localizer.Culture.Should().Be(UiLanguages.Resolve(null, UiLanguages.Os));
    }

    [Fact]
    public void Observe_emits_now_and_after_each_switch()
    {
        // Arrange
        var localizer = Create("en");
        var values = new List<string>();
        using var subscription = localizer.Observe(nameof(Strings.Library_Title)).Subscribe(new Collect(values));

        // Act
        localizer.SetCulture(CultureInfo.GetCultureInfo("ru"));

        // Assert
        values.Should().Equal("Library", "Библиотека");
    }

    [Fact]
    public void Observe_stops_after_unsubscribe()
    {
        // Arrange
        var localizer = Create("en");
        var values = new List<string>();
        localizer.Observe(nameof(Strings.Library_Title)).Subscribe(new Collect(values)).Dispose();

        // Act
        localizer.SetCulture(CultureInfo.GetCultureInfo("ru"));

        // Assert
        values.Should().Equal("Library");
    }

    [Fact]
    public void Isolated_instance_does_not_touch_the_process_ui_culture()
    {
        // Arrange
        var before = Strings.Culture;
        var localizer = Create("en");

        // Act
        localizer.SetCulture(CultureInfo.GetCultureInfo("ru"));

        // Assert
        Strings.Culture.Should().BeSameAs(before);
    }

    private sealed class Collect(List<string> values) : IObserver<string>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string value) => values.Add(value);
    }
}
