using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Tests;

[assembly: AssemblyFixture(typeof(EnglishUiFixture))]

namespace AiLocalPack.Desktop.Tests;

/// <summary>
/// Runs the app-wide <see cref="Localizer.Current"/> in English whatever the machine's language is, so UI tests can
/// assert English text. Tests that switch the language belong to <see cref="UiLanguageSwitchCollection"/>.
/// </summary>
public sealed class EnglishUiFixture
{
    public EnglishUiFixture() => Localizer.Current.SetLanguage(UiLanguages.Fallback);
}

/// <summary>Tests that change <see cref="Localizer.Current"/>: run alone, after the parallel tests, and restore English.</summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class UiLanguageSwitchCollection;
