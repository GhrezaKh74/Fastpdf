using System.Runtime.CompilerServices;
using FastPdfWriterStream.IO;
using FastPdfWriterStream.Text;

namespace FastPdfWriterStream;

/// <summary>
/// بوم یک صفحه. مختصات از گوشهٔ بالا-چپ صفحه و بر حسب point است (y رو به پایین).
/// وضعیت گرافیکی (رنگ، فونت، ضخامت خط) کش می‌شود تا دستورات تکراری نوشته نشوند.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfWriter _writer;
    internal ByteSink Content;

    public int Number { get; }
    public double Width { get; }
    public double Height { get; }

    // ── وضعیت کش‌شده ──
    private struct GState
    {
        public int Fill, Stroke; // -1 = نامشخص
        public double LineWidth;
        public bool Dashed;
        public PdfFont? Font;
        public double FontSize;
        public int RenderMode;
    }

    private GState _gs = new() { Fill = 0, Stroke = 0, LineWidth = 1, FontSize = -1 };
    private readonly Stack<GState> _saved = new();
    private bool _inText;
    private double _tx, _ty;

    internal PdfPage(PdfWriter writer, int number, double width, double height, ByteSink content)
    {
        _writer = writer;
        Number = number;
        Width = width;
        Height = height;
        Content = content;
    }

    public PdfWriter Writer => _writer;

    internal void Finish() => EndText();

    // ── وضعیت گرافیکی ───────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pack(PdfColor c) => (c.R << 16) | (c.G << 8) | c.B;

    private void Color(PdfColor c, bool stroke)
    {
        int packed = Pack(c);
        if (stroke ? _gs.Stroke == packed : _gs.Fill == packed) return;
        var s = Content;
        if (c.R == c.G && c.G == c.B)
        {
            s.Real(c.R / 255.0);
            s.Ascii(stroke ? " G\n" : " g\n");
        }
        else
        {
            s.Real(c.R / 255.0); s.Space();
            s.Real(c.G / 255.0); s.Space();
            s.Real(c.B / 255.0);
            s.Ascii(stroke ? " RG\n" : " rg\n");
        }
        if (stroke) _gs.Stroke = packed; else _gs.Fill = packed;
    }

    private void LineWidth(double w)
    {
        if (Math.Abs(_gs.LineWidth - w) < 0.0001) return;
        Content.Real(w);
        Content.Ascii(" w\n");
        _gs.LineWidth = w;
    }

    private void Dash(bool dashed, double width)
    {
        if (_gs.Dashed == dashed) return;
        if (dashed)
        {
            Content.Ascii("[");
            Content.Real(Math.Max(1, width * 3));
            Content.Space();
            Content.Real(Math.Max(1, width * 2));
            Content.Ascii("] 0 d\n");
        }
        else Content.Ascii("[] 0 d\n");
        _gs.Dashed = dashed;
    }

    private void BeginText()
    {
        if (_inText) return;
        Content.Ascii("BT\n");
        _inText = true;
        _tx = _ty = 0;
    }

    private void EndText()
    {
        if (!_inText) return;
        Content.Ascii("ET\n");
        _inText = false;
    }

    /// <summary>ذخیرهٔ وضعیت گرافیکی (q).</summary>
    public void SaveState()
    {
        EndText();
        Content.Ascii("q\n");
        _saved.Push(_gs);
    }

    /// <summary>بازیابی وضعیت گرافیکی (Q).</summary>
    public void RestoreState()
    {
        EndText();
        if (_saved.Count == 0) return;
        Content.Ascii("Q\n");
        _gs = _saved.Pop();
    }

    /// <summary>محدود کردن ناحیهٔ رسم به مستطیل (تا RestoreState بعدی).</summary>
    public void Clip(double x, double y, double w, double h)
    {
        EndText();
        Rect(x, y, w, h);
        Content.Ascii("W n\n");
    }

    /// <summary>شفافیت برای رسم‌های بعدی (۰ تا ۱). از SaveState/RestoreState استفاده کنید.</summary>
    public void SetOpacity(double opacity)
    {
        EndText();
        Content.Byte((byte)'/');
        Content.Ascii(_writer.OpacityState(opacity));
        Content.Ascii(" gs\n");
    }

    private void Rect(double x, double y, double w, double h)
    {
        var s = Content;
        s.Real(x); s.Space();
        s.Real(Height - y - h); s.Space();
        s.Real(w); s.Space();
        s.Real(h);
        s.Ascii(" re ");
    }

    // ── اشکال ───────────────────────────────────────────────────────

    public void DrawLine(double x1, double y1, double x2, double y2, PdfColor color, double width = 0.5, bool dashed = false)
    {
        EndText();
        Color(color, true);
        LineWidth(width);
        Dash(dashed, width);
        var s = Content;
        s.Real(x1); s.Space(); s.Real(Height - y1); s.Ascii(" m ");
        s.Real(x2); s.Space(); s.Real(Height - y2); s.Ascii(" l S\n");
    }

    /// <summary>چند خط افقی/عمودی با یک دستور stroke (بسیار سریع‌تر برای جدول‌ها).</summary>
    internal void BeginPath(PdfColor color, double width, bool dashed = false)
    {
        EndText();
        Color(color, true);
        LineWidth(width);
        Dash(dashed, width);
    }

    internal void PathLine(double x1, double y1, double x2, double y2)
    {
        var s = Content;
        s.Real(x1); s.Space(); s.Real(Height - y1); s.Ascii(" m ");
        s.Real(x2); s.Space(); s.Real(Height - y2); s.Ascii(" l\n");
    }

    internal void StrokePath() => Content.Ascii("S\n");

    public void FillRect(double x, double y, double w, double h, PdfColor color)
    {
        EndText();
        Color(color, false);
        Rect(x, y, w, h);
        Content.Ascii("f\n");
    }

    public void DrawRect(double x, double y, double w, double h, PdfColor? stroke, PdfColor? fill = null, double lineWidth = 0.5, bool dashed = false)
    {
        if (stroke == null && fill == null) return;
        EndText();
        if (fill is { } f) Color(f, false);
        if (stroke is { } st)
        {
            Color(st, true);
            LineWidth(lineWidth);
            Dash(dashed, lineWidth);
        }
        Rect(x, y, w, h);
        Content.Ascii(fill != null && stroke != null ? "B\n" : fill != null ? "f\n" : "S\n");
    }

    /// <summary>مستطیل با گوشه‌های گرد.</summary>
    public void DrawRoundedRect(double x, double y, double w, double h, double radius, PdfColor? stroke, PdfColor? fill = null, double lineWidth = 0.5)
    {
        if (radius <= 0) { DrawRect(x, y, w, h, stroke, fill, lineWidth); return; }
        if (stroke == null && fill == null) return;
        EndText();
        if (fill is { } f) Color(f, false);
        if (stroke is { } st) { Color(st, true); LineWidth(lineWidth); Dash(false, lineWidth); }
        double r = Math.Min(radius, Math.Min(w, h) / 2);
        double k = r * 0.5523;
        double x0 = x, x1 = x + w, y0 = Height - y, y1 = Height - y - h; // y0 بالا
        var s = Content;
        void P(double a, double b) { s.Real(a); s.Space(); s.Real(b); s.Space(); }
        P(x0 + r, y0); s.Ascii("m ");
        P(x1 - r, y0); s.Ascii("l ");
        P(x1 - r + k, y0); P(x1, y0 - r + k); P(x1, y0 - r); s.Ascii("c ");
        P(x1, y1 + r); s.Ascii("l ");
        P(x1, y1 + r - k); P(x1 - r + k, y1); P(x1 - r, y1); s.Ascii("c ");
        P(x0 + r, y1); s.Ascii("l ");
        P(x0 + r - k, y1); P(x0, y1 + r - k); P(x0, y1 + r); s.Ascii("c ");
        P(x0, y0 - r); s.Ascii("l ");
        P(x0, y0 - r + k); P(x0 + r - k, y0); P(x0 + r, y0); s.Ascii("c ");
        s.Ascii(fill != null && stroke != null ? "h B\n" : fill != null ? "h f\n" : "h S\n");
    }

    /// <summary>رسم تصویر در کادر. اگر keepAspect باشد تصویر داخل کادر جا می‌شود و وسط‌چین می‌گردد.</summary>
    public void DrawImage(PdfImage image, double x, double y, double w, double h, bool keepAspect = true)
    {
        if (image.ObjectId == 0) _writer.AddImage(image);
        EndText();
        if (keepAspect && image.Width > 0 && image.Height > 0)
        {
            double scale = Math.Min(w / image.Width, h / image.Height);
            double nw = image.Width * scale, nh = image.Height * scale;
            x += (w - nw) / 2;
            y += (h - nh) / 2;
            w = nw;
            h = nh;
        }
        var s = Content;
        s.Ascii("q ");
        s.Real(w); s.Ascii(" 0 0 "); s.Real(h); s.Space();
        s.Real(x); s.Space(); s.Real(Height - y - h);
        s.Ascii(" cm /");
        s.Ascii(image.ResourceName);
        s.Ascii(" Do Q\n");
    }

    // ── متن ─────────────────────────────────────────────────────────

    internal static void WriteGlyphs(ByteSink s, ReadOnlySpan<ushort> glyphs)
    {
        Span<byte> tmp = glyphs.Length <= 256 ? stackalloc byte[glyphs.Length * 2] : new byte[glyphs.Length * 2];
        for (int i = 0; i < glyphs.Length; i++)
        {
            tmp[i * 2] = (byte)(glyphs[i] >> 8);
            tmp[i * 2 + 1] = (byte)glyphs[i];
        }
        s.LiteralString(tmp);
    }

    private void SetFont(PdfFont font, double size)
    {
        if (ReferenceEquals(_gs.Font, font) && Math.Abs(_gs.FontSize - size) < 0.0005) return;
        Content.Byte((byte)'/');
        Content.Ascii(font.ResourceName);
        Content.Space();
        Content.Real(size);
        Content.Ascii(" Tf\n");
        _gs.Font = font;
        _gs.FontSize = size;
    }

    private void RenderMode(bool fakeBold, double size, PdfColor color)
    {
        int mode = fakeBold ? 2 : 0;
        if (fakeBold)
        {
            Color(color, true);
            LineWidth(size * 0.035);
        }
        if (_gs.RenderMode == mode) return;
        Content.Int(mode);
        Content.Ascii(" Tr\n");
        _gs.RenderMode = mode;
    }

    private void MoveText(double x, double pdfY)
    {
        var s = Content;
        s.Real(x - _tx); s.Space(); s.Real(pdfY - _ty); s.Ascii(" Td ");
        _tx = x;
        _ty = pdfY;
    }

    /// <summary>
    /// رسم یک خط glyph در موقعیت x (چپ) و خط پایه baselineY (از بالای صفحه).
    /// </summary>
    internal void ShowGlyphs(GlyphLine line, double x, double baselineY, PdfFont font, double size, PdfColor color, bool fakeBold, TotalPagesSlot? slot)
    {
        if (line.Count == 0) return;
        BeginText();
        Color(color, false);
        RenderMode(fakeBold, size, color);
        MoveText(x, Height - baselineY);
        var glyphs = line.Glyphs;
        int slotIdx = slot == null ? -1 : glyphs.IndexOf(UnicodeData.SlotGlyph);
        if (slotIdx < 0)
        {
            ShowRuns(line, 0, glyphs.Length, font, size);
            return;
        }

        // نشانگر «تعداد کل صفحات»: فاصله با TJ رد می‌شود و Form XObject در آن نقطه رسم می‌گردد
        double before = 0;
        var fonts = line.Fonts;
        for (int i = 0; i < slotIdx; i++)
        {
            var f = fonts.IsEmpty || fonts[i] == 0 ? font : font.FallbackAt(fonts[i]);
            before += f.Ttf.Advances[glyphs[i]] * f.Scale * size;
        }
        double slotX = x + before;
        if (slotIdx > 0) ShowRuns(line, 0, slotIdx, font, size);
        SetFont(font, size);
        Content.Byte((byte)'[');
        Content.Real(-slot!.SlotWidth / size * 1000);
        Content.Ascii("] TJ\n");
        if (slotIdx + 1 < glyphs.Length) ShowRuns(line, slotIdx + 1, glyphs.Length, font, size);
        EndText();
        var s = Content;
        s.Ascii("q 1 0 0 1 ");
        s.Real(slotX); s.Space(); s.Real(Height - baselineY);
        s.Ascii(" cm /");
        s.Ascii(slot.Name);
        s.Ascii(" Do Q\n");
    }

    /// <summary>نوشتن glyph ها با تفکیک بر اساس فونت (فونت اصلی/جایگزین).</summary>
    private void ShowRuns(GlyphLine line, int from, int to, PdfFont font, double size)
    {
        var glyphs = line.Glyphs;
        if (!line.HasFallback)
        {
            SetFont(font, size);
            WriteGlyphs(Content, glyphs[from..to]);
            Content.Ascii(" Tj\n");
            return;
        }
        var fonts = line.Fonts;
        int i = from;
        while (i < to)
        {
            byte fi = fonts[i];
            int j = i + 1;
            while (j < to && fonts[j] == fi) j++;
            SetFont(fi == 0 ? font : font.FallbackAt(fi), size);
            WriteGlyphs(Content, glyphs[i..j]);
            Content.Ascii(" Tj\n");
            i = j;
        }
    }

    private DigitStyle Digits(TextStyle st) => st.Digits ?? _writer.Options.Digits;

    private PdfFont ResolveFont(TextStyle st) =>
        st.Font ?? (_writer.Fonts.Count > 0 ? _writer.Fonts[0] : throw new InvalidOperationException("No font registered. Call PdfWriter.AddFont first."));

    /// <summary>عرض متن (point) در یک خط.</summary>
    public double MeasureText(ReadOnlySpan<char> text, TextStyle style, double? size = null)
    {
        var font = ResolveFont(style);
        var line = _writer.Shaper.ShapeLine(font, text, style.Direction, Digits(style), _writer.Options.RightToLeft);
        return line.Width * font.Scale * (size ?? style.Size);
    }

    /// <summary>ارتفاع لازم برای متن در عرض داده‌شده با سبک داده‌شده (برای Wrap).</summary>
    public double MeasureHeight(ReadOnlySpan<char> text, double width, TextStyle style) =>
        MeasureTextHeight(_writer, text, width, style);

    internal static double MeasureTextHeight(PdfWriter writer, ReadOnlySpan<char> text, double width, TextStyle style)
    {
        var font = style.Font ?? writer.Fonts[0];
        double lineH = font.LineHeight(style.Size) * style.LineSpacing;
        double pad = style.PaddingTop + style.PaddingBottom;
        if (style.Overflow != TextOverflow.Wrap || text.IsEmpty) return lineH + pad;
        double aw = Math.Max(1, width - style.PaddingLeft - style.PaddingRight);
        var digits = style.Digits ?? writer.Options.Digits;
        bool rtl = writer.Options.RightToLeft;
        // همان قاعدهٔ DrawText: کلمهٔ بلندتر از عرض باعث کوچک شدن فونت می‌شود، نه شکستن کلمه
        double size = style.Size;
        int widest = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != ' ' && text[i] != '\n') continue;
            if (i > start) widest = Math.Max(widest, writer.Shaper.ShapeLine(font, text[start..i], style.Direction, digits, rtl).Width);
            start = i + 1;
        }
        if (widest > 0 && widest * font.Scale * size > aw) size = Math.Max(style.MinSize, aw / (widest * font.Scale));
        lineH = font.LineHeight(size) * style.LineSpacing;
        int maxUnits = (int)(aw / (font.Scale * size));
        writer.Shaper.ShapeWrapped(font, text, style.Direction, digits, rtl, maxUnits, writer.LineScratch);
        return Math.Max(1, writer.LineScratch.Count) * lineH + pad;
    }

    /// <summary>
    /// رسم متن داخل کادر با چینش، جهت، اندازهٔ خودکار یا شکستن خط. ارتفاع واقعی محتوا برگردانده می‌شود.
    /// </summary>
    public double DrawText(ReadOnlySpan<char> text, double x, double y, double w, double h, TextStyle style)
    {
        if (text.IsEmpty) return 0;
        var font = ResolveFont(style);
        var digits = Digits(style);
        bool docRtl = _writer.Options.RightToLeft;
        double size = style.Size;
        double ax = x + style.PaddingLeft;
        double aw = Math.Max(0, w - style.PaddingLeft - style.PaddingRight);
        double ay = y + style.PaddingTop;
        double ah = Math.Max(0, h - style.PaddingTop - style.PaddingBottom);
        var shaper = _writer.Shaper;

        TotalPagesSlot? slot = null;
        if (text.IndexOf(UnicodeData.TotalPagesMarker) >= 0)
        {
            var zero = font.Ttf.GetGlyph(digits == DigitStyle.Persian ? '۰' : digits == DigitStyle.Arabic ? '٠' : '0');
            int digitsCount = Math.Max(_writer.Options.TotalPagesDigits, Number.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            shaper.SlotAdvance = font.Ttf.GetAdvance(zero) * digitsCount;
        }

        if (style.Overflow == TextOverflow.Wrap)
        {
            // هیچ کلمه‌ای وسطش شکسته نشود: اگر بلندترین کلمه در عرض جا نشود، فونت کوچک می‌شود
            double widest = WidestWordUnits(font, text, style.Direction, digits, docRtl) * font.Scale;
            if (widest * size > aw && widest > 0) size = Math.Max(style.MinSize, aw / widest);
            var lines = _writer.LineScratch;
            double lineH, total;
            while (true)
            {
                int maxUnits = (int)Math.Max(1, aw / (font.Scale * size));
                shaper.ShapeWrapped(font, text, style.Direction, digits, docRtl, maxUnits, lines);
                lineH = font.LineHeight(size) * style.LineSpacing;
                total = lines.Count * lineH;
                // اگر چند خط هم در ارتفاع کادر جا نشود، فونت کوچک می‌شود (مثلا سرستون‌های بلند)
                if (total <= ah + 0.5 || size <= style.MinSize) break;
                size = Math.Max(style.MinSize, size - 0.5);
            }
            double top = style.VAlign switch
            {
                VAlign.Middle => ay + (ah - total) / 2,
                VAlign.Bottom => ay + ah - total,
                _ => ay
            };
            bool clip = total > ah + 0.5;
            if (clip) { SaveState(); Clip(x, y, w, h); }
            double asc = font.Ascent(size);
            double extra = (lineH - font.LineHeight(size)) / 2;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                double lw = line.Width * font.Scale * size;
                double lx = AlignX(style.Align, ax, aw, lw, docRtl);
                double baseline = top + i * lineH + extra + asc;
                if (baseline - asc > y + h) break;
                ShowGlyphs(line, lx, baseline, font, size, style.Color, style.FakeBold, null);
                if (style.Underline) Underline(lx, baseline, lw, font, size, style.Color);
            }
            if (clip) RestoreState();
            return total + style.PaddingTop + style.PaddingBottom;
        }

        var gl = shaper.ShapeLine(font, text, style.Direction, digits, docRtl);
        double width = gl.Width * font.Scale * size;
        bool needClip = false;
        if (width > aw + 0.01)
        {
            switch (style.Overflow)
            {
                case TextOverflow.Shrink:
                    double ns = size * aw / width;
                    if (ns < style.MinSize) { ns = style.MinSize; needClip = true; }
                    size = ns;
                    width = gl.Width * font.Scale * size;
                    break;
                case TextOverflow.Clip:
                    needClip = true;
                    break;
                case TextOverflow.Ellipsis:
                    gl = Ellipsize(font, text, style, digits, docRtl, aw, size);
                    width = gl.Width * font.Scale * size;
                    break;
            }
        }

        if (slot == null && text.IndexOf(UnicodeData.TotalPagesMarker) >= 0)
        {
            // تعیین جهت چینش عدد در جای خالی: به سمت متن مجاور
            var glyphs = gl.Glyphs;
            int idx = glyphs.IndexOf(UnicodeData.SlotGlyph);
            bool textOnRight = idx < glyphs.Length - 1, textOnLeft = idx > 0;
            bool alignRight = textOnRight && !textOnLeft || (textOnRight && textOnLeft && docRtl);
            slot = _writer.GetTotalPagesSlot(font, size, shaper.SlotAdvance * font.Scale * size, alignRight, digits);
        }

        double tx = AlignX(style.Align, ax, aw, width, docRtl);
        double ascent = font.Ascent(size), descent = font.Descent(size);
        double textH = ascent + descent;
        double baselineY = style.VAlign switch
        {
            VAlign.Top => ay + ascent,
            VAlign.Bottom => ay + ah - descent,
            _ => ay + (ah - textH) / 2 + ascent
        };

        if (needClip) { SaveState(); Clip(x, y, w, h); }
        ShowGlyphs(gl, tx, baselineY, font, size, style.Color, style.FakeBold, slot);
        if (style.Underline) Underline(tx, baselineY, width, font, size, style.Color);
        if (needClip) RestoreState();
        return textH + style.PaddingTop + style.PaddingBottom;
    }

    public double DrawText(string? text, double x, double y, double w, double h, TextStyle style) =>
        text == null ? 0 : DrawText(text.AsSpan(), x, y, w, h, style);

    /// <summary>عرض بلندترین کلمه (واحد فونت) — کلمه‌ها با فاصلهٔ معمولی جدا می‌شوند، NBSP جزو کلمه است.</summary>
    private int WidestWordUnits(PdfFont font, ReadOnlySpan<char> text, TextDirection dir, DigitStyle digits, bool docRtl)
    {
        int widest = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != ' ' && text[i] != '\n') continue;
            if (i > start)
            {
                var w = _writer.Shaper.ShapeLine(font, text[start..i], dir, digits, docRtl).Width;
                if (w > widest) widest = w;
            }
            start = i + 1;
        }
        return widest;
    }

    private void Underline(double x, double baseline, double width, PdfFont font, double size, PdfColor color)
    {
        double off = font.Descent(size) * 0.35;
        DrawLine(x, baseline + off, x + width, baseline + off, color, Math.Max(0.3, size * 0.05));
    }

    private GlyphLine Ellipsize(PdfFont font, ReadOnlySpan<char> text, TextStyle style, DigitStyle digits, bool docRtl, double aw, double size)
    {
        string dots = font.Ttf.HasGlyph('…') ? "…" : "...";
        int lo = 0, hi = text.Length;
        // جستجوی دودویی بزرگ‌ترین پیشوند که با «…» جا شود
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            var candidate = string.Concat(text[..mid].TrimEnd(), dots);
            var l = _writer.Shaper.ShapeLine(font, candidate, style.Direction, digits, docRtl);
            if (l.Width * font.Scale * size <= aw) lo = mid; else hi = mid - 1;
        }
        var final = lo == 0 ? dots : string.Concat(text[..lo].TrimEnd(), dots);
        return _writer.Shaper.ShapeLine(font, final, style.Direction, digits, docRtl);
    }

    private static double AlignX(HAlign align, double ax, double aw, double width, bool docRtl)
    {
        var a = align switch
        {
            HAlign.Start => docRtl ? HAlign.Right : HAlign.Left,
            HAlign.End => docRtl ? HAlign.Left : HAlign.Right,
            _ => align
        };
        return a switch
        {
            HAlign.Right => ax + aw - width,
            HAlign.Center => ax + (aw - width) / 2,
            _ => ax
        };
    }

    /// <summary>رسم متن چرخیده حول نقطهٔ مرکز (مثلا واترمارک). زاویه بر حسب درجه، پادساعتگرد.</summary>
    public void DrawRotatedText(string text, double centerX, double centerY, double angleDegrees, PdfFont font, double size,
        PdfColor color, double opacity = 1, bool fakeBold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var gl = _writer.Shaper.ShapeLine(font, text, TextDirection.Auto, _writer.Options.Digits, _writer.Options.RightToLeft);
        double width = gl.Width * font.Scale * size;
        double a = angleDegrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        SaveState();
        if (opacity < 1) SetOpacity(opacity);
        var s = Content;
        s.Real(cos); s.Space(); s.Real(sin); s.Space(); s.Real(-sin); s.Space(); s.Real(cos); s.Space();
        s.Real(centerX); s.Space(); s.Real(Height - centerY);
        s.Ascii(" cm\n");
        BeginText();
        Color(color, false);
        RenderMode(fakeBold, size, color);
        double vy = -(font.Ascent(size) - font.Descent(size)) / 2;
        s.Real(-width / 2); s.Space(); s.Real(vy); s.Ascii(" Td\n");
        _tx = -width / 2; _ty = vy;
        ShowRuns(gl, 0, gl.Count, font, size);
        EndText();
        RestoreState();
    }

    /// <summary>
    /// رسم متن ساده در نقطهٔ (x, baselineY) بدون کادر — سریع‌ترین روش برای متن تک‌خطی.
    /// </summary>
    public double DrawString(ReadOnlySpan<char> text, double x, double baselineY, PdfFont font, double size, PdfColor color,
        HAlign align = HAlign.Left, TextDirection direction = TextDirection.Auto, DigitStyle? digits = null)
    {
        var gl = _writer.Shaper.ShapeLine(font, text, direction, digits ?? _writer.Options.Digits, _writer.Options.RightToLeft);
        double width = gl.Width * font.Scale * size;
        double tx = align switch
        {
            HAlign.Right => x - width,
            HAlign.Center => x - width / 2,
            _ => x
        };
        ShowGlyphs(gl, tx, baselineY, font, size, color, false, null);
        return width;
    }
}
