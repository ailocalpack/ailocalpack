using AiLocalPack.Core.Fonts;
using AiLocalPack.Toolkit.Recipes;

namespace AiLocalPack.Toolkit.Fonts;

/// <summary>
/// The font hook of an engine (task 31), implemented by the recipe tools whose games draw text with font files: which
/// fonts the game's text uses, and how to make the game use another font instead, written as files next to a
/// rebuild's output. <see cref="FontFixer"/> decides whether a swap is needed (the font lacks letters of the
/// translations) and with which whitelisted font. Methods read the disk and block.
/// </summary>
public interface IGameFonts
{
    /// <summary>Whether a swap uses the bold face too (the engine picks bold faces by file), else only the regular one.</summary>
    bool UsesBold { get; }

    /// <summary>The font files the game's text is drawn with (no duplicates), in a stable order.</summary>
    /// <exception cref="RecipeException">Not a game of this engine, or a file naming the fonts can't be read.</exception>
    IReadOnlyList<GameFont> Fonts(string gameDir);

    /// <summary>The bytes of <paramref name="font"/>.</summary>
    /// <exception cref="RecipeException">The file can't be read.</exception>
    byte[] ReadFont(string gameDir, GameFont font);

    /// <summary>Markup in a translation that is never drawn (escape codes, text tags), so needs no glyphs.</summary>
    IReadOnlyList<string> Placeholders(string text);

    /// <summary>
    /// Writes into <paramref name="output"/> what makes the game draw <paramref name="swaps"/>' fonts with their
    /// replacements in <paramref name="language"/> (the replacement files and the edited engine files; a file the
    /// rebuild already wrote is edited in its rebuilt form, <see cref="IRebuildOutput.Read"/>).
    /// </summary>
    /// <exception cref="RecipeException">A file to edit can't be read, parsed or written.</exception>
    FontSwapFiles Swap(string gameDir, string language, IReadOnlyList<FontSwap> swaps, IRebuildOutput output);
}

/// <summary>A font file a game draws text with.</summary>
/// <param name="Name">How the game names it (Ren'Py <c>gui/font/Aller_Rg.ttf</c>, RPG Maker MZ <c>mplus-1m-regular.woff</c>).</param>
/// <param name="File">The file holding it, relative to the game folder (<c>/</c>-separated; an archive for an archived font).</param>
/// <param name="Entry">The name inside the archive <paramref name="File"/>, or <c>null</c> for a loose file.</param>
public sealed record GameFont(string Name, string File, string? Entry = null);

/// <summary>A game font to replace, and the whitelisted font's bytes to replace it with.</summary>
/// <param name="Bold">The bold face, when the engine uses one (<see cref="IGameFonts.UsesBold"/>) and the font has it.</param>
public sealed record FontSwap(GameFont Font, WhitelistFont Replacement, byte[] Regular, byte[]? Bold);

/// <summary>Files <see cref="IGameFonts.Swap"/> wrote (relative to the game folder, ordinal order).</summary>
/// <param name="GeneratedFiles">Files the game makes from them when it runs (Ren'Py's compiled scripts).</param>
public sealed record FontSwapFiles(IReadOnlyList<string> Files, IReadOnlyList<string> GeneratedFiles);
