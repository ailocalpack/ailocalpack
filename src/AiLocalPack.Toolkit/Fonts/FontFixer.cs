using System.Globalization;
using System.Text;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Core.Registry;
using AiLocalPack.Toolkit.Recipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLocalPack.Toolkit.Fonts;

/// <summary>How well one game font covers the characters of the translations.</summary>
/// <param name="Family">Family name from the font file, or <c>null</c>.</param>
/// <param name="MissingLetters">Letters, marks and digits the translations use and the font lacks: these make a swap needed.</param>
/// <param name="MissingSymbols">Punctuation and symbols it lacks: only reported (a swap would change the look for a quote mark).</param>
/// <param name="Problem">Why the font couldn't be read (left alone), else <c>null</c>.</param>
public sealed record GameFontCoverage(
    GameFont Font,
    string? Family,
    IReadOnlyList<int> MissingLetters,
    IReadOnlyList<int> MissingSymbols,
    string? Problem = null)
{
    public bool NeedsSwap => MissingLetters.Count > 0;

    /// <summary>The family name, else the game's name of the file.</summary>
    public string DisplayName => Family ?? Path.GetFileNameWithoutExtension(Font.Name);
}

/// <summary>The game's fonts against the characters the translations need (<see cref="FontFixer.Check"/>).</summary>
/// <param name="Letters">Letters, marks and digits the translations use that the game's own text doesn't (code points, ascending).</param>
/// <param name="Symbols">Punctuation and symbols they add the same way (ascending).</param>
public sealed record FontCheck(IReadOnlyList<GameFontCoverage> Fonts, IReadOnlyList<int> Letters, IReadOnlyList<int> Symbols)
{
    public bool NeedsSwap => Fonts.Any(f => f.NeedsSwap);

    public IReadOnlyList<GameFontCoverage> ToSwap => [.. Fonts.Where(f => f.NeedsSwap)];
}

/// <summary>What <see cref="FontFixer.ApplyAsync"/> did.</summary>
/// <param name="Check">The coverage check; <c>null</c> when the engine has no font hook (nothing checked).</param>
/// <param name="Replacement">The whitelisted font swapped in, or <c>null</c> (no swap needed, or it failed).</param>
/// <param name="Uncovered">Letters the replacement lacks too (the best whitelisted font didn't have them all).</param>
/// <param name="UnknownFonts">Font IDs the pack lists that this app's whitelist lacks (ignored).</param>
/// <param name="FailedFont">The font that couldn't be had, with <paramref name="Failure"/>; nothing was swapped.</param>
public sealed record FontFixResult(
    FontCheck? Check,
    WhitelistFont? Replacement,
    IReadOnlyList<int> Uncovered,
    FontSwapFiles Files,
    IReadOnlyList<string> UnknownFonts,
    WhitelistFont? FailedFont = null,
    FontFetchResult? Failure = null)
{
    public bool Failed => Failure is not null;

    /// <summary>The manifest's <c>fonts</c> for a pack made from these translations (task 51 writes it).</summary>
    public IReadOnlyList<string> ManifestFonts => Replacement is null ? [] : [Replacement.Id];
}

/// <summary>
/// Makes a translation readable in the game's own fonts (task 31). <see cref="Check"/> reads each font the game's text
/// uses (<see cref="IGameFonts.Fonts"/>, <see cref="OpenTypeFont"/>) and compares it with the characters the
/// translations bring (placeholders left out, and characters the game's own text already uses: however the game draws
/// those, a translation doesn't change it): a font lacking any of those letters, marks or digits needs a swap. The replacement
/// is the first whitelisted font that has every letter the translations use, trying the pack's <c>fonts</c> first,
/// then the fonts made for the language, then the default (<see cref="FontWhitelist.For"/>); the bundled ones are at
/// hand, the others are downloaded (<see cref="FontStore"/>). When none has them all, the one with the most is used and
/// the rest are reported. <see cref="ApplyAsync"/> then has the engine write the swap (<see cref="IGameFonts.Swap"/>).
/// Fonts are swapped only for the text of the translations: packs never ship fonts.
/// </summary>
public sealed class FontFixer
{
    private readonly FontWhitelist _whitelist;
    private readonly FontStore _store;
    private readonly ILogger _logger;

    public FontFixer(FontWhitelist whitelist, FontStore store, ILogger<FontFixer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(whitelist);
        ArgumentNullException.ThrowIfNull(store);
        _whitelist = whitelist;
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// The game's fonts against the characters of <paramref name="translations"/>, or <c>null</c> when the engine has
    /// no font hook. A font that can't be read is listed with its problem and left alone.
    /// </summary>
    /// <exception cref="RecipeException">Not a game of the tool's engine, or a file naming the fonts can't be read.</exception>
    public static FontCheck? Check(IRecipeTool tool, string gameDir, IEnumerable<string> translations)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(translations);
        if (tool is not IGameFonts hook)
        {
            return null;
        }

        // Characters of the original text: a decorative font missing a glitch letter the game itself shows is not ours to fix.
        var original = new HashSet<int>();
        foreach (var text in tool.Extract(gameDir).Strings)
        {
            foreach (var rune in text.Original.EnumerateRunes())
            {
                original.Add(rune.Value);
            }
        }

        var (letters, symbols) = Needed(hook, translations, original);
        var fonts = new List<GameFontCoverage>();
        foreach (var font in hook.Fonts(gameDir))
        {
            FontInfo info;
            try
            {
                info = OpenTypeFont.Read(hook.ReadFont(gameDir, font));
            }
            catch (Exception e) when (e is FontFormatException or RecipeException)
            {
                fonts.Add(new GameFontCoverage(font, null, [], [], e.Message));
                continue;
            }

            fonts.Add(new GameFontCoverage(
                font,
                info.FamilyName,
                [.. letters.Where(c => !info.Characters.Contains(c))],
                [.. symbols.Where(c => !info.Characters.Contains(c))]));
        }

        return new FontCheck(fonts, letters, symbols);
    }

    /// <summary>
    /// Checks the game's fonts and, when one lacks letters of <paramref name="translations"/>, writes the swap into
    /// <paramref name="output"/> (next to the rebuilt files). Fails (nothing written) only when a needed whitelisted
    /// font can't be had and no other has the letters: e.g. Noto Sans JP offline.
    /// </summary>
    /// <param name="preferred">The pack manifest's <c>fonts</c> (tried first).</param>
    /// <param name="progress">Download of a font that isn't bundled.</param>
    /// <exception cref="RecipeException">The game's font files can't be listed, or the swap can't be written.</exception>
    public async Task<FontFixResult> ApplyAsync(
        IRecipeTool tool,
        string gameDir,
        string language,
        IReadOnlyDictionary<string, string> translations,
        IReadOnlyList<string> preferred,
        IRebuildOutput output,
        IProgress<PackFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrEmpty(language);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(output);
        var unknown = preferred.Where(id => _whitelist.Find(id) is null).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in unknown)
        {
            _logger.LogWarning("The pack lists the font {Font}, which this app's whitelist lacks; ignored", id);
        }

        var none = new FontSwapFiles([], []);
        var check = Check(tool, gameDir, translations.Values);
        if (check is null || !check.NeedsSwap)
        {
            return new FontFixResult(check, null, [], none, unknown);
        }

        var hook = (IGameFonts)tool;
        var candidates = preferred.Select(_whitelist.Find).OfType<WhitelistFont>().Concat(_whitelist.For(language)).DistinctBy(f => f.Id).ToList();
        (WhitelistFont Font, byte[] Regular, List<int> Uncovered)? best = null;
        (WhitelistFont Font, FontFetchResult Result)? failure = null;
        foreach (var candidate in candidates)
        {
            var fetched = await _store.GetAsync(candidate, candidate.File(FontFace.Regular)!, progress, cancellationToken).ConfigureAwait(false);
            if (fetched.Content is not { } regular)
            {
                failure ??= (candidate, fetched);
                continue;
            }

            FontCharacters characters;
            try
            {
                characters = OpenTypeFont.Read(regular).Characters;
            }
            catch (FontFormatException e)
            {
                _logger.LogError(e, "The whitelisted font {Font} can't be read", candidate.Id);
                continue;
            }

            var uncovered = check.Letters.Where(c => !characters.Contains(c)).ToList();
            if (best is null || uncovered.Count < best.Value.Uncovered.Count)
            {
                best = (candidate, regular, uncovered);
            }

            if (uncovered.Count == 0)
            {
                break;
            }
        }

        // A font that couldn't be had might have covered what the others lack: fail so the player can retry.
        if (failure is { } failed && (best is null || best.Value.Uncovered.Count > 0))
        {
            return new FontFixResult(check, null, [], none, unknown, failed.Font, failed.Result);
        }

        if (best is not { } chosen)
        {
            _logger.LogWarning("No whitelisted font could be read for {Language}; the game's fonts stay", language);
            return new FontFixResult(check, null, [], none, unknown);
        }

        byte[]? bold = null;
        if (hook.UsesBold && chosen.Font.File(FontFace.Bold) is { } boldFile)
        {
            var fetched = await _store.GetAsync(chosen.Font, boldFile, progress, cancellationToken).ConfigureAwait(false);
            bold = fetched.Content;
            if (bold is null)
            {
                // The engine makes a bold face from the regular one.
                _logger.LogWarning("The bold face of {Font} couldn't be had ({Problem}); swapping in the regular one only", chosen.Font.Id, fetched.Problem);
            }
        }

        var swaps = check.ToSwap.Select(f => new FontSwap(f.Font, chosen.Font, chosen.Regular, bold)).ToList();
        var files = hook.Swap(gameDir, language, swaps, output);
        _logger.LogInformation(
            "Fonts for {Language}: {Fonts} swapped for {Replacement}{Uncovered}",
            language,
            string.Join(", ", swaps.Select(s => s.Font.Name)),
            chosen.Font.Name,
            chosen.Uncovered.Count > 0 ? $" ({chosen.Uncovered.Count} letters it lacks too)" : string.Empty);
        return new FontFixResult(check, chosen.Font, chosen.Uncovered, files, unknown);
    }

    /// <summary>Up to <paramref name="count"/> code points as text (<c>Б, Ж, Я</c>), for logs and messages.</summary>
    public static string Sample(IReadOnlyList<int> codePoints, int count = 8)
    {
        ArgumentNullException.ThrowIfNull(codePoints);
        var shown = string.Join(", ", codePoints.Take(count).Select(char.ConvertFromUtf32));
        return codePoints.Count > count ? shown + ", …" : shown;
    }

    /// <summary>Characters the translations draw that the original text doesn't: placeholders removed, whitespace, controls and private use left out.</summary>
    private static (IReadOnlyList<int> Letters, IReadOnlyList<int> Symbols) Needed(IGameFonts hook, IEnumerable<string> translations, HashSet<int> original)
    {
        var letters = new SortedSet<int>();
        var symbols = new SortedSet<int>();
        foreach (var translation in translations)
        {
            if (string.IsNullOrEmpty(translation))
            {
                continue;
            }

            var text = translation;
            foreach (var placeholder in hook.Placeholders(translation).Distinct(StringComparer.Ordinal).OrderByDescending(p => p.Length))
            {
                text = text.Replace(placeholder, " ", StringComparison.Ordinal);
            }

            foreach (var rune in text.EnumerateRunes())
            {
                if (original.Contains(rune.Value))
                {
                    continue;
                }

                switch (Rune.GetUnicodeCategory(rune))
                {
                    case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                        or UnicodeCategory.DecimalDigitNumber:
                        letters.Add(rune.Value);
                        break;
                    case UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                        or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                        or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned:
                        break;
                    default:
                        symbols.Add(rune.Value);
                        break;
                }
            }
        }

        return ([.. letters], [.. symbols]);
    }
}
