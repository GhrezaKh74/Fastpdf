using FastPdfWriterStream.Text;

namespace FastPdfWriterStream.Templates;

/// <summary>سبک نهایی یک عنصر/سلول (متن + پس‌زمینه + حاشیه).</summary>
internal sealed class ResolvedStyle
{
    public TextStyle Text = new();
    public PdfColor? Background;
    public BorderDefinition? Border;
    public PdfColor BorderColor;
}

/// <summary>ادغام سبک‌ها: پیش‌فرض قالب ← سبک‌های نام‌دار (basedOn) ← مقادیر درون‌خطی.</summary>
internal sealed class StyleResolver
{
    private readonly ReportTemplate _t;
    private readonly Func<string?, bool, (PdfFont Font, bool FakeBold)> _fontLookup;

    public StyleResolver(ReportTemplate template, Func<string?, bool, (PdfFont, bool)> fontLookup)
    {
        _t = template;
        _fontLookup = fontLookup;
    }

    internal sealed class Acc
    {
        public string? Font, Color, Background, Align, VAlign, Overflow, Direction, Digits;
        public double? FontSize, Padding, MinFontSize, LineSpacing;
        public bool? Bold, Underline;
        public BorderDefinition? Border;

        public void Apply(StyleDefinition? s)
        {
            if (s == null) return;
            Font = s.Font ?? Font; Color = s.Color ?? Color; Background = s.Background ?? Background;
            Align = s.Align ?? Align; VAlign = s.VAlign ?? VAlign; Overflow = s.Overflow ?? Overflow;
            Direction = s.Direction ?? Direction; Digits = s.Digits ?? Digits;
            FontSize = s.FontSize ?? FontSize; Padding = s.Padding ?? Padding; MinFontSize = s.MinFontSize ?? MinFontSize;
            LineSpacing = s.LineSpacing ?? LineSpacing; Bold = s.Bold ?? Bold; Underline = s.Underline ?? Underline;
            Border = s.Border ?? Border;
        }
    }

    public Acc Start(string? namedStyle, Action<Acc>? defaults = null)
    {
        var acc = new Acc();
        defaults?.Invoke(acc);
        ApplyNamed(acc, namedStyle, 0);
        return acc;
    }

    public void ApplyNamed(Acc acc, string? name) => ApplyNamed(acc, name, 0);

    private void ApplyNamed(Acc acc, string? name, int depth)
    {
        if (string.IsNullOrEmpty(name) || depth > 10) return;
        if (!_t.Styles.TryGetValue(name, out var s)) return;
        ApplyNamed(acc, s.BasedOn, depth + 1);
        acc.Apply(s);
    }

    public void ApplyInline(Acc acc, ElementDefinition e)
    {
        acc.Font = e.Font ?? acc.Font; acc.FontSize = e.FontSize ?? acc.FontSize; acc.Bold = e.Bold ?? acc.Bold;
        acc.Underline = e.Underline ?? acc.Underline; acc.Color = e.Color ?? acc.Color; acc.Background = e.Background ?? acc.Background;
        acc.Align = e.Align ?? acc.Align; acc.VAlign = e.VAlign ?? acc.VAlign; acc.Overflow = e.Overflow ?? acc.Overflow;
        acc.Direction = e.Direction ?? acc.Direction; acc.Digits = e.Digits ?? acc.Digits; acc.Padding = e.Padding ?? acc.Padding;
        acc.Border = e.Border ?? acc.Border;
    }

    public void ApplyInline(Acc acc, ColumnDefinition c)
    {
        acc.Font = c.Font ?? acc.Font; acc.FontSize = c.FontSize ?? acc.FontSize; acc.Bold = c.Bold ?? acc.Bold;
        acc.Color = c.Color ?? acc.Color; acc.Align = c.Align ?? acc.Align; acc.Overflow = c.Overflow ?? acc.Overflow;
        acc.Direction = c.Direction ?? acc.Direction;
    }

    public ResolvedStyle Build(Acc acc)
    {
        var (font, fakeBold) = _fontLookup(acc.Font, acc.Bold == true);
        var r = new ResolvedStyle();
        var ts = r.Text;
        ts.Font = font;
        ts.FakeBold = fakeBold;
        ts.Size = acc.FontSize ?? _t.DefaultFontSize;
        ts.Color = PdfColor.TryParse(acc.Color, out var c) ? c : PdfColor.Black;
        ts.Align = Parse.Align(acc.Align) ?? HAlign.Start;
        ts.VAlign = Parse.VAlign(acc.VAlign) ?? VAlign.Middle;
        ts.Overflow = Parse.Overflow(acc.Overflow) ?? TextOverflow.Shrink;
        ts.Direction = Parse.Direction(acc.Direction) ?? TextDirection.Auto;
        ts.Digits = Parse.Digits(acc.Digits);
        ts.Underline = acc.Underline == true;
        double pad = acc.Padding ?? 2;
        ts.PaddingLeft = ts.PaddingRight = pad;
        ts.PaddingTop = ts.PaddingBottom = Math.Min(pad, 1);
        ts.MinSize = acc.MinFontSize ?? Math.Min(4, ts.Size);
        ts.LineSpacing = acc.LineSpacing ?? 1.0;
        if (PdfColor.TryParse(acc.Background, out var bg)) r.Background = bg;
        r.Border = acc.Border;
        if (acc.Border != null && PdfColor.TryParse(acc.Border.Color, out var bc)) r.BorderColor = bc;
        return r;
    }
}
