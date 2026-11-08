using System.Globalization;
using System.Text;

namespace AiLocalPack.Toolkit.Validation;

/// <summary>What a length limit counts (<see cref="Recipes.ExtractedString.MaxLengthUnit"/>).</summary>
public enum LengthUnit
{
    /// <summary>Characters as the reader sees them: text elements (a letter with its combining marks, an emoji sequence = 1).</summary>
    Characters,

    /// <summary>Monospace cells: wide and fullwidth characters (CJK, Hangul, fullwidth forms) and emoji count 2, others 1.</summary>
    DisplayWidth,
}

/// <summary>Length of drawn text for limit checks. Line breaks are never counted; zero-width text elements count 0.</summary>
public static class TextLength
{
    public static int Measure(string text, LengthUnit unit)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "\n" or "\r" or "\r\n" || IsZeroWidth(element))
            {
                continue;
            }

            length += unit == LengthUnit.DisplayWidth && IsWide(element) ? 2 : 1;
        }

        return length;
    }

    private static bool IsZeroWidth(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWide(string element)
    {
        var first = Rune.GetRuneAt(element, 0).Value;
        return element.Contains('️', StringComparison.Ordinal) || IsWide(first);
    }

    /// <summary>
    /// East Asian Width W and F (Unicode's <c>EastAsianWidth.txt</c>, the ranges games meet: CJK, kana, Hangul, fullwidth
    /// forms, emoji presentation characters); ambiguous-width characters count as narrow.
    /// </summary>
    private static bool IsWide(int c) => c switch
    {
        >= 0x1100 and <= 0x115F => true,
        0x231A or 0x231B or 0x2329 or 0x232A or 0x23F0 or 0x23F3 or 0x267F or 0x2693 or 0x26A1 or 0x26CE or 0x26D4 or 0x26EA => true,
        >= 0x23E9 and <= 0x23EC => true,
        0x25FD or 0x25FE or 0x2614 or 0x2615 or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or 0x26C5 => true,
        >= 0x2648 and <= 0x2653 => true,
        0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E => true,
        >= 0x2753 and <= 0x2755 => true,
        0x2757 or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
        >= 0x2795 and <= 0x2797 => true,
        >= 0x2E80 and <= 0x303E => true,
        >= 0x3041 and <= 0x33FF => true,
        >= 0x3400 and <= 0x4DBF => true,
        >= 0x4E00 and <= 0x9FFF => true,
        >= 0xA000 and <= 0xA4CF => true,
        >= 0xA960 and <= 0xA97F => true,
        >= 0xAC00 and <= 0xD7A3 => true,
        >= 0xF900 and <= 0xFAFF => true,
        >= 0xFE10 and <= 0xFE19 => true,
        >= 0xFE30 and <= 0xFE6F => true,
        >= 0xFF00 and <= 0xFF60 => true,
        >= 0xFFE0 and <= 0xFFE6 => true,
        >= 0x16FE0 and <= 0x16FE4 => true,
        >= 0x17000 and <= 0x18AFF => true,
        >= 0x1B000 and <= 0x1B2FF => true,
        0x1F004 or 0x1F0CF or 0x1F18E => true,
        >= 0x1F191 and <= 0x1F19A => true,
        >= 0x1F1E6 and <= 0x1F1FF => true,
        >= 0x1F200 and <= 0x1F251 => true,
        >= 0x1F300 and <= 0x1F64F => true,
        >= 0x1F680 and <= 0x1F6FF => true,
        >= 0x1F900 and <= 0x1F9FF => true,
        >= 0x1FA70 and <= 0x1FAFF => true,
        >= 0x20000 and <= 0x2FFFD => true,
        >= 0x30000 and <= 0x3FFFD => true,
        _ => false,
    };
}
