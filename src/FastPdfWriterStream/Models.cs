using System.Globalization;
using System.IO.Compression;
using FastPdfWriterStream.Text;

namespace FastPdfWriterStream;

/// <summary>رنگ RGB.</summary>
public readonly record struct PdfColor(byte R, byte G, byte B)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor White = new(255, 255, 255);
    public static readonly PdfColor Gray = new(128, 128, 128);
    public static readonly PdfColor LightGray = new(217, 217, 217);
    public static readonly PdfColor Red = new(220, 38, 38);
    public static readonly PdfColor Blue = new(68, 114, 196);

    /// <summary>"#RRGGBB" یا "RRGGBB" یا "#RGB".</summary>
    public static PdfColor FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Black;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
        if (h.Length < 6) return Black;
        return new PdfColor(
            byte.Parse(h.AsSpan(0, 2), NumberStyles.HexNumber),
            byte.Parse(h.AsSpan(2, 2), NumberStyles.HexNumber),
            byte.Parse(h.AsSpan(4, 2), NumberStyles.HexNumber));
    }

    public static bool TryParse(string? hex, out PdfColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(hex) || hex.Equals("transparent", StringComparison.OrdinalIgnoreCase) || hex.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        try { color = FromHex(hex); return true; } catch { return false; }
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>اندازهٔ صفحه به point (1pt = 1/72 inch).</summary>
public readonly record struct PageSize(double Width, double Height)
{
    public static readonly PageSize A3 = new(841.89, 1190.55);
    public static readonly PageSize A4 = new(595.28, 841.89);
    public static readonly PageSize A5 = new(419.53, 595.28);
    public static readonly PageSize Letter = new(612, 792);
    public static readonly PageSize Legal = new(612, 1008);

    public PageSize Landscape() => Width > Height ? this : new PageSize(Height, Width);
    public PageSize Portrait() => Width < Height ? this : new PageSize(Height, Width);

    public static PageSize FromName(string? name, bool landscape = false)
    {
        var s = (name ?? "A4").Trim().ToUpperInvariant() switch
        {
            "A3" => A3,
            "A5" => A5,
            "LETTER" => Letter,
            "LEGAL" => Legal,
            _ => A4
        };
        return landscape ? s.Landscape() : s.Portrait();
    }

    /// <summary>تبدیل میلی‌متر به point.</summary>
    public static double Mm(double mm) => mm * 72.0 / 25.4;
}

public enum HAlign : byte
{
    /// <summary>ابتدای متن: راست در سند راست‌به‌چپ، چپ در سند چپ‌به‌راست.</summary>
    Start,
    Left,
    Center,
    Right,
    /// <summary>انتهای متن.</summary>
    End
}

public enum VAlign : byte { Top, Middle, Bottom }

/// <summary>رفتار متن بلندتر از کادر.</summary>
public enum TextOverflow : byte
{
    /// <summary>کوچک کردن فونت تا جا شود (مانند ShrinkFontToFit در Stimulsoft).</summary>
    Shrink,
    /// <summary>شکستن در چند خط.</summary>
    Wrap,
    /// <summary>بریدن متن در مرز کادر.</summary>
    Clip,
    /// <summary>بریدن و افزودن «…».</summary>
    Ellipsis,
    /// <summary>بدون محدودیت.</summary>
    Visible
}

/// <summary>سبک متن. برای کارایی، یک نمونه را بسازید و بارها استفاده کنید.</summary>
public sealed class TextStyle
{
    public PdfFont? Font { get; set; }
    public double Size { get; set; } = 9;
    public PdfColor Color { get; set; } = PdfColor.Black;
    public HAlign Align { get; set; } = HAlign.Start;
    public VAlign VAlign { get; set; } = VAlign.Middle;
    public TextOverflow Overflow { get; set; } = TextOverflow.Shrink;
    public TextDirection Direction { get; set; } = TextDirection.Auto;
    /// <summary>null یعنی تنظیم سند.</summary>
    public DigitStyle? Digits { get; set; }
    /// <summary>حداقل اندازهٔ فونت در حالت Shrink.</summary>
    public double MinSize { get; set; } = 4;
    /// <summary>ضریب فاصلهٔ خطوط در حالت Wrap.</summary>
    public double LineSpacing { get; set; } = 1.0;
    /// <summary>ضخیم‌سازی مصنوعی برای فونت‌هایی که نسخهٔ Bold ندارند.</summary>
    public bool FakeBold { get; set; }
    public bool Underline { get; set; }
    public double PaddingLeft { get; set; } = 2;
    public double PaddingRight { get; set; } = 2;
    public double PaddingTop { get; set; } = 1;
    public double PaddingBottom { get; set; } = 1;

    public TextStyle Clone() => (TextStyle)MemberwiseClone();

    public TextStyle With(Action<TextStyle> change)
    {
        var c = Clone();
        change(c);
        return c;
    }
}

/// <summary>تنظیمات سند.</summary>
public sealed class PdfWriterOptions
{
    public PageSize PageSize { get; set; } = PageSize.A4;

    /// <summary>فشرده‌سازی محتوای صفحات (FlateDecode).</summary>
    public bool Compress { get; set; } = true;

    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>فشرده‌سازی صفحات به صورت موازی روی چند هسته (پیشنهادی برای گزارش‌های بزرگ).</summary>
    public bool ParallelCompression { get; set; } = true;

    /// <summary>حداکثر تعداد صفحات در صف فشرده‌سازی (کنترل مصرف حافظه).</summary>
    public int MaxPendingPages { get; set; } = Math.Max(2, Environment.ProcessorCount * 2);

    /// <summary>جهت پیش‌فرض سند (برای متن‌های ترکیبی و چینش Start/End).</summary>
    public bool RightToLeft { get; set; }

    /// <summary>نوع ارقام پیش‌فرض.</summary>
    public DigitStyle Digits { get; set; } = DigitStyle.Latin;

    /// <summary>embed کردن فقط glyph های استفاده‌شده (حجم بسیار کمتر).</summary>
    public bool SubsetFonts { get; set; } = true;

    /// <summary>
    /// تعداد رقم رزرو شده برای «تعداد کل صفحات» (چون این عدد در پایان سند معلوم می‌شود).
    /// موتور قالب این مقدار را از روی تعداد ردیف‌ها تخمین می‌زند.
    /// </summary>
    public int TotalPagesDigits { get; set; } = 3;

    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Creator { get; set; }
}
