using FastPdfWriterStream.Text;

namespace FastPdfWriterStream.Tests;

public class TextTests
{
    private static byte[] Levels(string s, int baseLevel)
    {
        var levels = new byte[s.Length];
        BidiAlgorithm.ResolveLevels(s, baseLevel, levels, new BidiClass[s.Length * 2]);
        return levels;
    }

    private static string Visual(string s, int baseLevel)
    {
        var levels = Levels(s, baseLevel);
        var order = new int[s.Length];
        BidiAlgorithm.Reorder(levels, order);
        return new string(order.Select(i => s[i]).ToArray());
    }

    [Fact]
    public void Bidi_LatinUnchanged() => Assert.Equal("abc 123", Visual("abc 123", 0));

    [Fact]
    public void Bidi_ArabicReversed() => Assert.Equal("مالس", Visual("سلام", 1));

    [Fact]
    public void Bidi_NumbersInsideRtlStayLtr() => Assert.Equal("لایر 1500 غلبم", Visual("مبلغ 1500 ریال", 1));

    [Fact]
    public void Bidi_DateStaysTogether() => Assert.Equal("1403/05/12 خیرات", Visual("تاریخ 1403/05/12", 1));

    [Fact]
    public void Bidi_LatinWordInsideRtl() => Assert.Equal("کناب IR12 هب", Visual("به IR12 بانک", 1));

    [Fact]
    public void Shaper_UsesContextualForms()
    {
        using var ms = new MemoryStream();
        using var pdf = new PdfWriter(ms, true);
        var font = pdf.AddFont(TestFonts.RegularFont);
        var shaper = new TextShaper();
        var ttf = font.Font;
        // «ببب»: init + medi + fina — هر سه glyph متفاوت از شکل مستقل
        var line = shaper.ShapeLine(font, "ببب", TextDirection.Auto, DigitStyle.Latin, true);
        var glyphs = line.Glyphs.ToArray();
        Assert.Equal(3, glyphs.Length);
        Assert.Equal(3, glyphs.Distinct().Count());
        Assert.DoesNotContain(ttf.GetGlyph('ب'), glyphs);
    }

    [Fact]
    public void Shaper_LamAlefLigature()
    {
        using var ms = new MemoryStream();
        using var pdf = new PdfWriter(ms, true);
        var font = pdf.AddFont(TestFonts.RegularFont);
        var line = new TextShaper().ShapeLine(font, "لا", TextDirection.Auto, DigitStyle.Latin, true);
        Assert.Equal(1, line.Count);
    }

    [Fact]
    public void Shaper_ZwnjBreaksJoining()
    {
        using var ms = new MemoryStream();
        using var pdf = new PdfWriter(ms, true);
        var font = pdf.AddFont(TestFonts.RegularFont);
        var shaper = new TextShaper();
        var joined = shaper.ShapeLine(font, "میشود", TextDirection.Auto, DigitStyle.Latin, true).Glyphs.ToArray();
        var zwnj = shaper.ShapeLine(font, "می‌شود", TextDirection.Auto, DigitStyle.Latin, true).Glyphs.ToArray();
        Assert.Equal(joined.Length, zwnj.Length); // ZWNJ خودش glyph ندارد
        Assert.NotEqual(joined, zwnj);
    }

    [Fact]
    public void Shaper_WrapsLines()
    {
        using var ms = new MemoryStream();
        using var pdf = new PdfWriter(ms, true);
        var font = pdf.AddFont(TestFonts.RegularFont);
        var lines = new List<GlyphLine>();
        var text = string.Join(" ", Enumerable.Repeat("کلمه", 40));
        new TextShaper().ShapeWrapped(font, text, TextDirection.Auto, DigitStyle.Latin, true, font.Font.UnitsPerEm * 10, lines);
        Assert.True(lines.Count > 3);
        Assert.All(lines, l => Assert.True(l.Width <= font.Font.UnitsPerEm * 10));
    }

    [Fact]
    public void ShapeCache_ReturnsSameResult()
    {
        using var ms = new MemoryStream();
        using var pdf = new PdfWriter(ms, true);
        var font = pdf.AddFont(TestFonts.RegularFont);
        var shaper = new TextShaper();
        var a = shaper.ShapeLine(font, "انتقال وجه", TextDirection.Auto, DigitStyle.Latin, true).Glyphs.ToArray();
        var b = shaper.ShapeLine(font, "انتقال وجه".AsSpan(), TextDirection.Auto, DigitStyle.Latin, true).Glyphs.ToArray();
        Assert.Equal(a, b);
        Assert.Equal(1, font.ShapeCache.Count);
    }
}
