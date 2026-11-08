using System.Globalization;

namespace AiLocalPack.Core.Generation;

/// <summary>How much text a game has to translate.</summary>
/// <param name="Strings">Strings to translate.</param>
/// <param name="Words">Words of the originals (<see cref="CountWords"/>).</param>
/// <param name="Characters">UTF-16 characters of the originals.</param>
public sealed record TextSize(int Strings, long Words, long Characters)
{
    public static TextSize Empty { get; } = new(0, 0, 0);

    /// <summary>The size of <paramref name="originals"/>.</summary>
    public static TextSize Of(IEnumerable<string> originals)
    {
        ArgumentNullException.ThrowIfNull(originals);
        int strings = 0;
        long words = 0, characters = 0;
        foreach (var text in originals)
        {
            strings++;
            words += CountWords(text);
            characters += text.Length;
        }

        return new TextSize(strings, words, characters);
    }

    /// <summary>
    /// Words = runs of letters and digits (an apostrophe or hyphen between letters keeps the run), except that each
    /// Chinese / Japanese character counts as a word on its own (those scripts don't separate words).
    /// </summary>
    public static long CountWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long words = 0;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsCjk(c))
            {
                words++;
                inWord = false;
            }
            else if (char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else if (!(inWord && c is '\'' or '’' or '-' && i + 1 < text.Length && char.IsLetter(text[i + 1])))
            {
                inWord = false;
            }
        }

        return words;
    }

    private static bool IsCjk(char c) =>
        c is (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '䶿') or (>= '一' and <= '鿿') or (>= '豈' and <= '﫿');
}

/// <summary>Where the text size of an estimate comes from.</summary>
public enum EstimateBasis
{
    /// <summary>The toolkit extracted the strings: counts are exact.</summary>
    Toolkit,

    /// <summary>Guessed from the sizes of the game's files (unknown engine, or the toolkit couldn't read it): rough.</summary>
    FileSizes,
}

/// <summary>
/// What generating a pack is expected to cost (<see cref="GenerationEstimateModel.Estimate"/>): tokens, agent time
/// and the share of a subscription's usage limits, each low and high bound.
/// </summary>
/// <param name="LimitWindowsLow">Usage-limit windows (5 h each) of an entry-level plan the job uses up.</param>
public sealed record GenerationEstimate(
    EstimateBasis Basis,
    TextSize Text,
    int Chunks,
    long InputTokens,
    long OutputTokens,
    TimeSpan TimeLow,
    TimeSpan TimeHigh,
    double LimitWindowsLow,
    double LimitWindowsHigh)
{
    /// <summary>The same in days of limits, a player's day = <see cref="GenerationEstimateModel.LimitWindowsPerDay"/> windows.</summary>
    public double LimitDaysLow => LimitWindowsLow / GenerationEstimateModel.LimitWindowsPerDay;

    public double LimitDaysHigh => LimitWindowsHigh / GenerationEstimateModel.LimitWindowsPerDay;
}

/// <summary>
/// A deliberately simple cost model, enough for the start form's "3–5 h, 1–2 days of limits". The constants are rough
/// guesses (2026-11, no measured jobs yet), to be tuned once real jobs record their usage (task 47); the ranges are
/// wide on purpose. Pure.
/// </summary>
public static class GenerationEstimateModel
{
    /// <summary>Characters per token of the originals (mostly English; tokenizers average ~4).</summary>
    public const double SourceCharactersPerToken = 4;

    /// <summary>
    /// Output tokens per source token of a translation: translations run ~20 % longer and non-Latin scripts take more
    /// tokens per character (one factor for every language for now).
    /// </summary>
    public const double TranslationExpansion = 1.6;

    /// <summary>Input tokens per string besides its text: key, context note, length limit, placeholders, JSON.</summary>
    public const int InputTokensPerString = 40;

    /// <summary>Output tokens per string besides its translation: key and JSON of the put call.</summary>
    public const int OutputTokensPerString = 25;

    /// <summary>Strings fed to the agent per chunk (task 50's chunking uses the same).</summary>
    public const int StringsPerChunk = 50;

    /// <summary>Input tokens per chunk: system prompt, glossary and tool definitions read again every turn (mostly cache reads).</summary>
    public const int InputTokensPerChunk = 12_000;

    /// <summary>Output tokens per chunk: the agent's own messages and tool calls.</summary>
    public const int OutputTokensPerChunk = 500;

    /// <summary>Exploring the game and building the glossary (stages 1–2), input / output tokens.</summary>
    public const int SetupInputTokens = 150_000;

    public const int SetupOutputTokens = 15_000;

    /// <summary>An unknown engine: the agent works out the formats and writes its own reader, so setup costs this many times more.</summary>
    public const int UnknownFormatSetupFactor = 4;

    /// <summary>Output tokens per second an agent writes, tool round trips aside.</summary>
    public const double OutputTokensPerSecond = 60;

    /// <summary>Seconds per chunk spent on tool calls (get strings, validate, put translations) and turn latency.</summary>
    public const double SecondsPerChunk = 20;

    /// <summary>Fixed time: starting the agent, extraction, rebuild and test.</summary>
    public static readonly TimeSpan FixedTime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Weighted tokens (<see cref="CachedInputWeight"/> × input + output) an entry-level plan (Claude Pro, ChatGPT Plus)
    /// allows per 5-hour window. Neither publishes a token figure; this is a working guess.
    /// </summary>
    public const double TokensPerLimitWindow = 300_000;

    /// <summary>Input counts this much of an output token toward the limits (agents re-read mostly cached input).</summary>
    public const double CachedInputWeight = 0.1;

    /// <summary>Windows a player gets through in a day (their limits reset while they sleep).</summary>
    public const double LimitWindowsPerDay = 3;

    /// <summary>Bounds around the middle value: time and limits vary with the model, the language and the agent's habits.</summary>
    public const double LowFactor = 0.7;

    public const double HighFactor = 1.6;

    /// <summary>
    /// Share of the bytes of text files (JSON, CSV, scripts…) taken as text the player sees: most of those bytes are
    /// keys, markup, map data and code. Checked against the toolkit's counts on the test games (2026-11-08): Nepenthe
    /// 48 MB of JSON / JS → 0.36 M characters; with <see cref="DataTextShare"/> the guess lands within ~2× for Brotato,
    /// EVERSPACE and Nepenthe, and overshoots small games with big packs (Golfing in Aether ~8×).
    /// </summary>
    public const double VisibleTextShare = 0.01;

    /// <summary>Share of the bytes of other data files (packs, archives, assets) taken as text, at most <see cref="MaxDataCharacters"/>.</summary>
    public const double DataTextShare = 0.0002;

    public const long MaxDataCharacters = 500_000;

    /// <summary>Average length of a string and of a word (with its space) in characters, for guesses from file sizes.</summary>
    public const double CharactersPerString = 40;

    public const double CharactersPerWord = 6;

    /// <summary>The cost of translating <paramref name="text"/>.</summary>
    public static GenerationEstimate Estimate(TextSize text, EstimateBasis basis)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = (int)Math.Ceiling(text.Strings / (double)StringsPerChunk);
        var sourceTokens = text.Characters / SourceCharactersPerToken;
        var setupFactor = basis == EstimateBasis.FileSizes ? UnknownFormatSetupFactor : 1;
        var input = (long)Math.Round(
            sourceTokens + ((double)text.Strings * InputTokensPerString) + ((double)chunks * InputTokensPerChunk) + ((double)SetupInputTokens * setupFactor));
        var output = (long)Math.Round(
            (sourceTokens * TranslationExpansion) + ((double)text.Strings * OutputTokensPerString) + ((double)chunks * OutputTokensPerChunk) + ((double)SetupOutputTokens * setupFactor));

        var seconds = (output / OutputTokensPerSecond) + (chunks * SecondsPerChunk) + (FixedTime.TotalSeconds * setupFactor);
        var windows = ((input * CachedInputWeight) + output) / TokensPerLimitWindow;
        return new GenerationEstimate(
            basis,
            text,
            chunks,
            input,
            output,
            TimeSpan.FromSeconds(Math.Round(seconds * LowFactor)),
            TimeSpan.FromSeconds(Math.Round(seconds * HighFactor)),
            windows * LowFactor,
            windows * HighFactor);
    }

    /// <summary>
    /// A guess of the text size from file sizes: <see cref="VisibleTextShare"/> of the text files' bytes plus
    /// <see cref="DataTextShare"/> of the other data files' (capped), cut into strings and words by the averages.
    /// </summary>
    public static TextSize GuessFromFileSizes(long textFileBytes, long dataFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(dataFileBytes);
        var characters = (long)Math.Round((textFileBytes * VisibleTextShare) + Math.Min(dataFileBytes * DataTextShare, MaxDataCharacters));
        return new TextSize((int)Math.Min(int.MaxValue, Math.Round(characters / CharactersPerString)), (long)Math.Round(characters / CharactersPerWord), characters);
    }
}
