using System.Buffers.Binary;
using AiLocalPack.Core.Fonts;
using AiLocalPack.Toolkit.Fonts;

namespace AiLocalPack.Toolkit.Tests.Fonts;

public sealed class OpenTypeFontTests
{
    private static readonly byte[] Latin = FontWriter.TrueType("Fixture Sans", FontWriter.Ascii);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A bundled whitelisted font: a real font, the sample of the cmap reader.</summary>
    private static async Task<byte[]> Bundled(string id, FontFace face = FontFace.Regular)
    {
        using var temp = new TempDir();
        using var http = new FakeHttpHandler();
        var font = FontWhitelist.Bundled.Find(id)!;
        var result = await new FontStore(temp.Path, http.CreateClient()).GetAsync(font, font.File(face)!, cancellationToken: Ct);
        return result.Content!;
    }

    [Fact]
    public void A_truetype_font_has_the_characters_its_cmap_maps()
    {
        // Act
        var info = OpenTypeFont.Read(Latin);

        // Assert
        (info.Format, info.Characters.Count, info.Characters.Contains('A'), info.Characters.Contains('Я')).Should().Be(("ttf", 95, true, false));
    }

    [Fact]
    public void The_end_segment_of_format_4_is_not_a_character()
    {
        // Act
        var info = OpenTypeFont.Read(Latin);

        // Assert
        info.Characters.Contains(0xFFFF).Should().BeFalse();
    }

    [Fact]
    public void Characters_beyond_the_bmp_come_from_format_12()
    {
        // Arrange
        var font = FontWriter.TrueType("Emoji", ['A', 0x1F600]);

        // Act
        var info = OpenTypeFont.Read(font);

        // Assert
        (info.Characters.Contains(0x1F600), info.Characters.Contains(0x1F601), info.Characters.Count).Should().Be((true, false, 2));
    }

    [Fact]
    public void The_family_name_comes_from_the_name_table()
    {
        // Act
        var info = OpenTypeFont.Read(Latin);

        // Assert
        info.FamilyName.Should().Be("Fixture Sans");
    }

    [Theory]
    [InlineData("woff")]
    [InlineData("woff2")]
    [InlineData("ttc")]
    public void A_wrapped_font_has_the_same_characters(string format)
    {
        // Arrange
        var wrapped = format switch
        {
            "woff" => FontWriter.Woff(Latin),
            "woff2" => FontWriter.Woff2(Latin),
            _ => FontWriter.Collection(Latin, FontWriter.TrueType("Second", ['Z'])),
        };

        // Act
        var info = OpenTypeFont.Read(wrapped);

        // Assert
        (info.Format, info.FamilyName, info.Characters.Count, info.Characters.Contains('~')).Should().Be((format, "Fixture Sans", 95, true));
    }

    [Fact]
    public async Task Noto_sans_has_latin_cyrillic_greek_and_vietnamese_but_no_kana()
    {
        // Arrange
        var font = await Bundled("noto-sans");

        // Act
        var info = OpenTypeFont.Read(font);

        // Assert
        (info.FamilyName, info.Characters.ContainsAll("AzЖяЁΩλỹ€".EnumerateRunes().Select(r => r.Value)), info.Characters.Contains('あ'))
            .Should().Be(("Noto Sans", true, false));
    }

    [Fact]
    public async Task Noto_sans_thai_has_thai_letters()
    {
        // Arrange
        var font = await Bundled("noto-sans-thai", FontFace.Bold);

        // Act
        var info = OpenTypeFont.Read(font);

        // Assert
        info.Characters.ContainsAll("สวัสดี".EnumerateRunes().Select(r => r.Value)).Should().BeTrue();
    }

    [Fact]
    public async Task Noto_sans_arabic_has_arabic_letters()
    {
        // Arrange
        var font = await Bundled("noto-sans-arabic");

        // Act
        var info = OpenTypeFont.Read(font);

        // Assert
        info.Characters.ContainsAll("مرحبا".EnumerateRunes().Select(r => r.Value)).Should().BeTrue();
    }

    [Fact]
    public void A_file_that_is_not_a_font_is_refused()
    {
        // Act
        var act = () => OpenTypeFont.Read("not a font at all"u8);

        // Assert
        act.Should().Throw<FontFormatException>();
    }

    [Fact]
    public void A_truncated_font_is_refused()
    {
        // Act
        var act = () => OpenTypeFont.Read(Latin.AsSpan(0, Latin.Length / 2));

        // Assert
        act.Should().Throw<FontFormatException>();
    }

    [Fact]
    public void A_table_offset_outside_the_file_is_refused()
    {
        // Arrange
        var damaged = (byte[])Latin.Clone();
        for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(damaged.AsSpan(4)); i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(damaged.AsSpan(12 + (i * 16) + 8), 0x7FFFFFF0);
        }

        // Act
        var act = () => OpenTypeFont.Read(damaged);

        // Assert
        act.Should().Throw<FontFormatException>();
    }

    [Fact]
    public void A_damaged_woff2_stream_is_refused()
    {
        // Arrange
        var woff2 = FontWriter.Woff2(Latin);
        woff2.AsSpan(woff2.Length - 8).Fill(0xFF);

        // Act
        var act = () => OpenTypeFont.Read(woff2);

        // Assert
        act.Should().Throw<FontFormatException>();
    }
}
