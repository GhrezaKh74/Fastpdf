using FastPdfWriterStream.Fonts;
using FastPdfWriterStream.Text;

namespace FastPdfWriterStream.Tests;

public class WriterTests
{
    private static byte[] Build(Action<PdfWriter, PdfFont> draw, PdfWriterOptions? options = null)
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfWriter(ms, leaveOpen: true, options ?? new PdfWriterOptions { RightToLeft = true }))
        {
            var font = pdf.AddFont(TestFonts.RegularFont, "Vazir");
            draw(pdf, font);
        }
        return ms.ToArray();
    }

    [Fact]
    public void EmptyDocument_HasOnePage_AndValidXref()
    {
        var bytes = Build((_, _) => { });
        var r = new PdfTestReader(bytes);
        Assert.Equal(1, r.PageCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ManyPages_AllCompressionModes(bool compress, bool parallel)
    {
        var bytes = Build((pdf, font) =>
        {
            var style = new TextStyle { Font = font, Size = 10 };
            for (int i = 0; i < 600; i++)
            {
                var p = pdf.NewPage();
                p.DrawText("صفحه " + (i + 1), 20, 20, 300, 20, style);
            }
        }, new PdfWriterOptions { Compress = compress, ParallelCompression = parallel, RightToLeft = true });
        var r = new PdfTestReader(bytes);
        Assert.Equal(600, r.PageCount);
        // درخت صفحات چندسطحی است (هر ۲۵۶ صفحه یک گره)
        Assert.Equal(600, r.PageIds().Count);
        Assert.Contains("600", r.PageText(599));
    }

    [Fact]
    public void LatinText_IsExtractable()
    {
        var bytes = Build((pdf, font) =>
            pdf.NewPage().DrawText("Hello PDF 123", 20, 20, 300, 20, new TextStyle { Font = font }),
            new PdfWriterOptions());
        Assert.Contains("Hello PDF 123", new PdfTestReader(bytes).PageText(0));
    }

    [Fact]
    public void PersianText_IsShapedAndReversedVisually()
    {
        var bytes = Build((pdf, font) =>
            pdf.NewPage().DrawText("سپرده بانکی", 20, 20, 300, 20, new TextStyle { Font = font }));
        var text = new PdfTestReader(bytes).PageText(0).Trim();
        // ترتیب نمایشی راست‌به‌چپ معکوس ترتیب منطقی است
        Assert.Equal("سپرده بانکی", PdfTestReader.Reverse(text));
    }

    [Fact]
    public void PersianDigits_AreConverted()
    {
        var bytes = Build((pdf, font) =>
            pdf.NewPage().DrawText("1,234,567", 20, 20, 300, 20, new TextStyle { Font = font, Digits = DigitStyle.Persian }));
        Assert.Contains("۱,۲۳۴,۵۶۷", new PdfTestReader(bytes).PageText(0));
    }

    [Fact]
    public void MixedText_NumbersKeepLeftToRightOrder()
    {
        var bytes = Build((pdf, font) =>
            pdf.NewPage().DrawText("مبلغ 1500 ریال", 20, 20, 400, 20, new TextStyle { Font = font }));
        var text = new PdfTestReader(bytes).PageText(0);
        Assert.Contains("1500", text);
    }

    [Fact]
    public void TotalPages_IsWrittenAsFormXObject()
    {
        var bytes = Build((pdf, font) =>
        {
            for (int i = 0; i < 3; i++)
                pdf.NewPage().DrawText("صفحه ", 20, 20, 300, 20, new TextStyle { Font = font });
        });
        var r = new PdfTestReader(bytes);
        var form = r.Objects.Values.Single(o => o.Dict.Contains("/Subtype /Form"));
        Assert.Contains("Tj", System.Text.Encoding.Latin1.GetString(form.Stream!));
        Assert.Contains("/TP1 Do", System.Text.Encoding.Latin1.GetString(r.Objects[int.Parse(System.Text.RegularExpressions.Regex.Match(r.Objects[r.PageIds()[0]].Dict, @"/Contents (\d+)").Groups[1].Value)].Stream!));
    }

    [Fact]
    public void Shapes_Images_Watermark_DoNotBreakDocument()
    {
        var png = CreatePng(4, 4, withAlpha: true);
        var bytes = Build((pdf, font) =>
        {
            var p = pdf.NewPage();
            p.DrawLine(10, 10, 100, 10, PdfColor.Red, 1, dashed: true);
            p.DrawRect(10, 20, 100, 50, PdfColor.Black, PdfColor.LightGray);
            p.DrawRoundedRect(10, 80, 100, 50, 8, PdfColor.Blue, null);
            p.DrawImage(pdf.AddImage(png), 10, 140, 50, 50);
            p.DrawRotatedText("نمونه", 300, 400, 45, font, 50, PdfColor.Red, 0.2);
        });
        var r = new PdfTestReader(bytes);
        Assert.Contains(r.Objects.Values, o => o.Dict.Contains("/Subtype /Image") && o.Dict.Contains("/SMask"));
        Assert.Contains(r.Objects.Values, o => o.Dict.Contains("/ExtGState"));
    }

    [Fact]
    public void FontSubset_IsValidTrueType()
    {
        var font = TestFonts.RegularFont;
        var used = new[] { font.GetGlyph('a'), font.GetGlyph('ب'), font.GetGlyph('۱') };
        var subset = FontSubsetter.Subset(font, used);
        Assert.True(subset.Length < font.Data.Length / 2);
        var reparsed = new TrueTypeFont(subset);
        Assert.Equal(font.NumGlyphs, reparsed.NumGlyphs);
    }

    [Fact]
    public void Fallback_UsesSecondFontForMissingGlyphs()
    {
        using var ms = new MemoryStream();
        using (var pdf = new PdfWriter(ms, true))
        {
            var main = pdf.AddFont(TestFonts.RegularFont, "A");
            var bold = pdf.AddFont(TestFonts.Bold, "B");
            main.AddFallback(bold);
            pdf.NewPage().DrawText("abc", 10, 10, 100, 20, new TextStyle { Font = main });
        }
        Assert.Contains("abc", new PdfTestReader(ms.ToArray()).PageText(0));
    }

    internal static byte[] CreatePng(int w, int h, bool withAlpha)
    {
        int bpp = withAlpha ? 4 : 3;
        var raw = new byte[(w * bpp + 1) * h];
        for (int y = 0; y < h; y++)
        {
            raw[y * (w * bpp + 1)] = (byte)(y % 5); // انواع فیلتر
            for (int x = 0; x < w * bpp; x++) raw[y * (w * bpp + 1) + 1 + x] = (byte)(x * 13 + y * 7);
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            ms.Write(len);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]); // CRC (بررسی نمی‌شود)
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = (byte)(withAlpha ? 6 : 2);
        Chunk("IHDR", ihdr);
        Chunk("IDAT", PdfImage.Deflate(raw));
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}
