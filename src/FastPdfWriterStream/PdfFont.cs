using FastPdfWriterStream.Fonts;

namespace FastPdfWriterStream;

/// <summary>
/// فونت ثبت‌شده در یک سند PDF. شیء فونت در پایان سند (پس از مشخص شدن glyph های استفاده‌شده) نوشته می‌شود.
/// </summary>
public sealed class PdfFont
{
    internal readonly TrueTypeFont Ttf;
    internal readonly string ResourceName;
    internal readonly int ObjectId;
    internal readonly bool[] Used;
    internal readonly string?[] ToUnicode;
    internal readonly double Scale; // 1/unitsPerEm

    // جداول شکل‌دهی (lazy)
    private ArabicFeatures? _arabic;

    internal PdfFont(TrueTypeFont ttf, string resourceName, int objectId, string? alias)
    {
        Ttf = ttf;
        ResourceName = resourceName;
        ObjectId = objectId;
        Name = alias ?? ttf.FamilyName;
        Used = new bool[Math.Max(ttf.NumGlyphs, 1)];
        ToUnicode = new string?[Math.Max(ttf.NumGlyphs, 1)];
        Scale = 1.0 / ttf.UnitsPerEm;
        Used[0] = true;
    }

    /// <summary>نام منطقی فونت (برای استفاده در قالب‌ها).</summary>
    public string Name { get; }

    public TrueTypeFont Font => Ttf;

    /// <summary>ارتفاع بالای خط پایه برای اندازهٔ داده‌شده (point).</summary>
    public double Ascent(double size) => Ttf.Ascender * Scale * size;

    /// <summary>عمق زیر خط پایه (عدد مثبت).</summary>
    public double Descent(double size) => -Ttf.Descender * Scale * size;

    /// <summary>ارتفاع خط پیشنهادی.</summary>
    public double LineHeight(double size) => (Ttf.Ascender - Ttf.Descender + Ttf.LineGap) * Scale * size;

    internal ArabicFeatures Arabic => _arabic ??= new ArabicFeatures(Ttf);

    private readonly List<PdfFont> _fallbacks = new();

    /// <summary>فونت‌های جایگزین برای کاراکترهایی که در این فونت وجود ندارند (مثلا حروف لاتین در B Nazanin).</summary>
    public IReadOnlyList<PdfFont> Fallbacks => _fallbacks;

    internal bool HasFallbacks => _fallbacks.Count > 0;

    internal PdfFont FallbackAt(byte index) => _fallbacks[index - 1];

    /// <summary>افزودن فونت جایگزین (حداکثر ۸ فونت).</summary>
    public PdfFont AddFallback(PdfFont fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (ReferenceEquals(fallback, this) || _fallbacks.Contains(fallback)) return this;
        if (_fallbacks.Count >= 8) throw new InvalidOperationException("Too many fallback fonts.");
        _fallbacks.Add(fallback);
        ShapeCache.Clear();
        return this;
    }

    internal void Use(ushort glyph, char c)
    {
        Used[glyph] = true;
        ToUnicode[glyph] ??= c.ToString();
    }

    internal void Use(ushort glyph, string text)
    {
        Used[glyph] = true;
        ToUnicode[glyph] ??= text;
    }

    // کش متن‌های شکل‌دهی‌شده (فارسی) — کلید: متن + جهت + نوع ارقام
    internal readonly Text.ShapeCache ShapeCache = new();
}

/// <summary>نتیجهٔ شکل‌دهی یک خط متن (ترتیب نمایشی).</summary>
internal sealed class ShapedText
{
    public readonly ushort[] Glyphs;
    public readonly byte[]? Fonts;
    public readonly int Width; // واحد فونت

    public ShapedText(ushort[] glyphs, byte[]? fonts, int width)
    {
        Glyphs = glyphs;
        Fonts = fonts;
        Width = width;
    }
}

/// <summary>جداول GSUB برای حروف عربی/فارسی (isol/init/medi/fina + لیگچرها).</summary>
internal sealed class ArabicFeatures
{
    public readonly Dictionary<ushort, ushort>? Isol, Init, Medi, Fina;
    public readonly Dictionary<ushort, List<GsubTable.Ligature>>? Rlig;

    public ArabicFeatures(TrueTypeFont ttf)
    {
        var g = ttf.Gsub;
        if (g == null || !g.HasScript("arab")) return;
        Isol = NullIfEmpty(g.GetSingleMap("arab", "isol"));
        Init = NullIfEmpty(g.GetSingleMap("arab", "init"));
        Medi = NullIfEmpty(g.GetSingleMap("arab", "medi"));
        Fina = NullIfEmpty(g.GetSingleMap("arab", "fina"));
        var rlig = new Dictionary<ushort, List<GsubTable.Ligature>>(g.GetLigatures("arab", "rlig"));
        var liga = g.GetLigatures("arab", "liga");
        foreach (var kv in liga)
            if (!rlig.ContainsKey(kv.Key)) rlig[kv.Key] = kv.Value;
        Rlig = rlig.Count > 0 ? rlig : null;
    }

    private static Dictionary<ushort, ushort>? NullIfEmpty(Dictionary<ushort, ushort> d) => d.Count == 0 ? null : d;
}
