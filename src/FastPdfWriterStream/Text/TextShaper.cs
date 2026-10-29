using System.Runtime.CompilerServices;

namespace FastPdfWriterStream.Text;

/// <summary>جهت متن.</summary>
public enum TextDirection : byte
{
    /// <summary>اگر متن حرف فارسی/عربی داشته باشد راست‌به‌چپ، وگرنه چپ‌به‌راست.</summary>
    Auto,
    LeftToRight,
    RightToLeft
}

/// <summary>نوع نمایش ارقام.</summary>
public enum DigitStyle : byte
{
    /// <summary>ارقام انگلیسی 0123456789</summary>
    Latin,
    /// <summary>ارقام فارسی ۰۱۲۳۴۵۶۷۸۹</summary>
    Persian,
    /// <summary>ارقام عربی ٠١٢٣٤٥٦٧٨٩</summary>
    Arabic
}

/// <summary>
/// یک خط شکل‌دهی‌شده (ترتیب نمایشی). اگر <see cref="Fonts"/> null نباشد، برای هر glyph
/// اندیس فونت (۰ = فونت اصلی، ۱.. = فونت‌های جایگزین) مشخص شده است.
/// </summary>
internal readonly struct GlyphLine
{
    public readonly ushort[] Buffer;
    public readonly byte[]? FontBuffer;
    public readonly int Offset;
    public readonly int Count;
    public readonly int Width; // واحد فونت اصلی

    public GlyphLine(ushort[] buffer, byte[]? fonts, int offset, int count, int width)
    {
        Buffer = buffer;
        FontBuffer = fonts;
        Offset = offset;
        Count = count;
        Width = width;
    }

    public ReadOnlySpan<ushort> Glyphs => Buffer.AsSpan(Offset, Count);
    public ReadOnlySpan<byte> Fonts => FontBuffer == null ? default : FontBuffer.AsSpan(Offset, Count);
    public bool HasFallback => FontBuffer != null;
}

/// <summary>
/// موتور شکل‌دهی متن: تبدیل ارقام، bidi، اتصال حروف فارسی (GSUB یا Presentation Forms)،
/// لیگچر لام‌الف، فونت جایگزین، شکستن خط و چیدمان نمایشی.
/// بافرها بازاستفاده می‌شوند (بدون تخصیص حافظه در مسیر داغ). نمونه thread-safe نیست.
/// </summary>
internal sealed class TextShaper
{
    private char[] _chars = new char[256];
    private byte[] _levels = new byte[256];
    private BidiClass[] _types = new BidiClass[512];
    // آیتم‌های glyph منطقی
    private ushort[] _gid = new ushort[256];
    private byte[] _gfont = new byte[256];
    private int[] _cluster = new int[256];
    private int[] _clusterLen = new int[256];
    private int[] _order = new int[256];
    private ushort[] _out = new ushort[256];
    private byte[] _outFont = new byte[256];
    private bool _anyFallback;

    private static readonly ushort[] Empty = Array.Empty<ushort>();

    /// <summary>عرض (واحد فونت) نشانگر تعداد کل صفحات در فراخوانی جاری.</summary>
    public int SlotAdvance;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char MapDigit(char c, DigitStyle style)
    {
        if (style == DigitStyle.Latin || c < '0' || c > '9') return c;
        return (char)(c - '0' + (style == DigitStyle.Persian ? 0x06F0 : 0x0660));
    }

    /// <summary>عرض glyph در واحد فونت اصلی.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Adv(PdfFont font, ushort g, byte fi)
    {
        if (g == UnicodeData.SlotGlyph) return SlotAdvance;
        if (fi == 0) return font.Ttf.Advances[g];
        var fb = font.FallbackAt(fi);
        return (int)Math.Round(fb.Ttf.Advances[g] * (double)font.Ttf.UnitsPerEm / fb.Ttf.UnitsPerEm);
    }

    private void EnsureChars(int n)
    {
        if (_chars.Length >= n) return;
        int size = Math.Max(n, _chars.Length * 2);
        _chars = new char[size];
        _levels = new byte[size];
        _types = new BidiClass[size * 2];
        _gid = new ushort[size];
        _gfont = new byte[size];
        _cluster = new int[size];
        _clusterLen = new int[size];
        _order = new int[size];
        _out = new ushort[size];
        _outFont = new byte[size];
    }

    /// <summary>شکل‌دهی یک خط (بدون شکستن).</summary>
    public GlyphLine ShapeLine(PdfFont font, ReadOnlySpan<char> text, TextDirection dir, DigitStyle digits, bool documentRtl)
    {
        int n = text.Length;
        if (n == 0) return new GlyphLine(Empty, null, 0, 0, 0);
        EnsureChars(n);

        // مسیر سریع: متن ساده بدون حروف راست‌به‌چپ و بدون glyph گمشده
        bool complex = dir == TextDirection.RightToLeft;
        if (!complex)
        {
            var map = font.Ttf.BmpMap;
            var adv = font.Ttf.Advances;
            var output = _out;
            int width = 0;
            for (int i = 0; i < n; i++)
            {
                char c = text[i];
                if (c >= 0x0590 || c < 0x20) { complex = true; break; }
                c = MapDigit(c, digits);
                ushort g = map[c];
                if (g == 0 && font.HasFallbacks && c != ' ') { complex = true; break; }
                if (!font.Used[g] || font.ToUnicode[g] == null) font.Use(g, c);
                output[i] = g;
                width += adv[g];
            }
            if (!complex) return new GlyphLine(output, null, 0, n, width);
        }

        // کش برای متن‌های فارسی پرتکرار (مثل شرح تراکنش)
        if (n <= 128 && text.IndexOf(UnicodeData.TotalPagesMarker) < 0)
        {
            byte flags = (byte)((int)dir | ((int)digits << 2) | (documentRtl ? 16 : 0));
            if (font.ShapeCache.TryGet(text, flags, out var cached))
                return new GlyphLine(cached.Glyphs, cached.Fonts, 0, cached.Glyphs.Length, cached.Width);
            var line = ShapeComplex(font, text, dir, digits, documentRtl, wrapWidth: -1, lines: null);
            var copy = line.Glyphs.ToArray();
            var fonts = line.HasFallback ? line.Fonts.ToArray() : null;
            font.ShapeCache.Add(text, flags, new ShapedText(copy, fonts, line.Width));
            return new GlyphLine(copy, fonts, 0, copy.Length, line.Width);
        }
        return ShapeComplex(font, text, dir, digits, documentRtl, wrapWidth: -1, lines: null);
    }

    /// <summary>شکل‌دهی با شکستن خط در عرض داده‌شده (واحد فونت). خطوط به <paramref name="lines"/> اضافه می‌شوند.</summary>
    public void ShapeWrapped(PdfFont font, ReadOnlySpan<char> text, TextDirection dir, DigitStyle digits, bool documentRtl,
        int maxWidth, List<GlyphLine> lines)
    {
        lines.Clear();
        if (text.Length == 0) return;
        EnsureChars(text.Length);
        ShapeComplex(font, text, dir, digits, documentRtl, maxWidth, lines);
    }

    private GlyphLine ShapeComplex(PdfFont font, ReadOnlySpan<char> text, TextDirection dir, DigitStyle digits,
        bool documentRtl, int wrapWidth, List<GlyphLine>? lines)
    {
        int n = text.Length;
        var chars = _chars;
        bool hasRtl = false;
        for (int i = 0; i < n; i++)
        {
            char c = MapDigit(text[i], digits);
            if (c == '\r' || c == '\t') c = ' ';
            if (c == '\n' && lines == null) c = ' ';
            chars[i] = c;
            if (!hasRtl && UnicodeData.IsRtl(c)) hasRtl = true;
        }
        var span = chars.AsSpan(0, n);

        int baseLevel = dir switch
        {
            TextDirection.RightToLeft => 1,
            TextDirection.LeftToRight => 0,
            _ => hasRtl ? (documentRtl ? 1 : FirstStrongLevel(span)) : 0
        };

        var levels = _levels.AsSpan(0, n);
        if (hasRtl || baseLevel == 1)
            BidiAlgorithm.ResolveLevels(span, baseLevel, levels, _types.AsSpan(0, n * 2));
        else
            levels.Clear();

        _anyFallback = false;
        int count = ShapeGlyphs(font, span);

        if (lines == null)
            return EmitLine(font, span, 0, count, baseLevel, 0);

        // شکستن خط
        int outPos = 0;
        int start = 0;
        while (start < count)
        {
            int width = 0, lastBreak = -1, i = start;
            bool hardBreak = false;
            for (; i < count; i++)
            {
                char c = span[_cluster[i]];
                if (c == '\n') { hardBreak = true; break; }
                int w = Adv(font, _gid[i], _gfont[i]);
                if (width + w > wrapWidth && i > start && !IsSpace(c))
                {
                    if (lastBreak > start) i = lastBreak;
                    break;
                }
                width += w;
                if (IsSpace(c)) lastBreak = i + 1;
            }
            int end = i;
            int visibleEnd = end;
            while (visibleEnd > start && IsSpace(span[_cluster[visibleEnd - 1]])) visibleEnd--;
            var line = EmitLine(font, span, start, visibleEnd, baseLevel, outPos);
            lines.Add(line);
            outPos += line.Count;
            start = hardBreak ? end + 1 : end;
            if (!hardBreak) while (start < count && span[_cluster[start]] == ' ') start++;
        }
        return lines.Count > 0 ? lines[0] : new GlyphLine(Empty, null, 0, 0, 0);
    }

    private static bool IsSpace(char c) => c == ' ' || c == ' ' || (c >= ' ' && c <= ' ');

    private static int FirstStrongLevel(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            var t = UnicodeData.GetBidi(c);
            if (t == BidiClass.L) return 0;
            if (t == BidiClass.R || t == BidiClass.AL) return 1;
        }
        return 0;
    }

    private static JoiningType Join(char c)
    {
        if (c == '‌') return JoiningType.U;
        if (c == '‍') return JoiningType.C;
        if (UnicodeData.IsInvisibleControl(c)) return JoiningType.T;
        return UnicodeData.GetJoining(c);
    }

    /// <summary>انتخاب فونت برای یک کاراکتر (اصلی یا اولین جایگزینی که glyph دارد).</summary>
    private static byte PickFont(PdfFont font, char c)
    {
        if (!font.HasFallbacks || font.Ttf.GetGlyph(c) != 0 || c == ' ') return 0;
        var fbs = font.Fallbacks;
        for (int k = 0; k < fbs.Count; k++)
            if (fbs[k].Ttf.GetGlyph(c) != 0) return (byte)(k + 1);
        return 0;
    }

    /// <summary>تولید glyph ها در ترتیب منطقی (اتصال حروف + لیگچر).</summary>
    private int ShapeGlyphs(PdfFont primary, ReadOnlySpan<char> s)
    {
        int n = s.Length;
        int count = 0;

        for (int i = 0; i < n; i++)
        {
            char c = s[i];
            if (UnicodeData.IsInvisibleControl(c) || c == '\n')
            {
                if (c == '\n') Add(primary, 0, ref count, 0, i, 1, s);
                continue;
            }
            if (c == UnicodeData.TotalPagesMarker)
            {
                _gid[count] = UnicodeData.SlotGlyph; _gfont[count] = 0; _cluster[count] = i; _clusterLen[count] = 1; count++;
                continue;
            }

            byte fi = PickFont(primary, c);
            var font = fi == 0 ? primary : primary.FallbackAt(fi);
            var ttf = font.Ttf;

            var jt = Join(c);
            if (jt == JoiningType.U || jt == JoiningType.T || jt == JoiningType.C || c < 0x0600)
            {
                ushort g0 = ttf.GetGlyph(c);
                if (g0 == 0 && c == ' ') g0 = ttf.GetGlyph(' ');
                Add(font, fi, ref count, g0, i, 1, s);
                continue;
            }

            // پیدا کردن حرف قبلی و بعدی غیرشفاف
            int p = i - 1;
            while (p >= 0 && Join(s[p]) == JoiningType.T) p--;
            int q = i + 1;
            while (q < n && Join(s[q]) == JoiningType.T) q++;
            bool prevJoins = p >= 0 && Join(s[p]) is JoiningType.D or JoiningType.C;
            bool nextJoins = jt == JoiningType.D && q < n && Join(s[q]) is JoiningType.R or JoiningType.D or JoiningType.C;

            // لام‌الف
            if (c == 'ل' && q == i + 1 && q < n && UnicodeData.TryLamAlef(s[q], out var isolLig, out var finaLig))
            {
                ushort lig = ttf.GetGlyph(prevJoins ? finaLig : isolLig);
                if (lig != 0)
                {
                    font.Use(lig, new string(s.Slice(i, 2)));
                    _gid[count] = lig; _gfont[count] = fi; _cluster[count] = i; _clusterLen[count] = 2; count++;
                    i = q;
                    continue;
                }
            }

            int form; // 0 isol, 1 fina, 2 init, 3 medi
            if (jt == JoiningType.R) form = prevJoins ? 1 : 0;
            else form = prevJoins ? (nextJoins ? 3 : 1) : (nextJoins ? 2 : 0);

            var ar = font.Arabic;
            ushort g = ttf.GetGlyph(c);
            ushort shaped = 0;
            var gsubMap = form switch { 0 => ar.Isol, 1 => ar.Fina, 2 => ar.Init, _ => ar.Medi };
            if (gsubMap != null && g != 0 && gsubMap.TryGetValue(g, out var sg)) shaped = sg;
            else if (form != 0 || gsubMap == null)
            {
                if (UnicodeData.TryGetForms(c, out var forms))
                {
                    char pf = form switch { 0 => forms.Isol, 1 => forms.Fina, 2 => forms.Init, _ => forms.Medi };
                    if (pf != '\0') shaped = ttf.GetGlyph(pf);
                }
            }
            if (shaped == 0) shaped = g;
            Add(font, fi, ref count, shaped, i, 1, s);
        }

        // لیگچرهای GSUB (فقط فونت اصلی)
        if (primary.Arabic.Rlig != null && count > 1) count = ApplyLigatures(primary, primary.Arabic.Rlig, count, s);
        return count;
    }

    private void Add(PdfFont font, byte fi, ref int count, ushort g, int cluster, int len, ReadOnlySpan<char> s)
    {
        if (!font.Used[g] || font.ToUnicode[g] == null)
        {
            if (len == 1) font.Use(g, s[cluster]);
            else font.Use(g, new string(s.Slice(cluster, len)));
        }
        if (fi != 0) _anyFallback = true;
        _gid[count] = g;
        _gfont[count] = fi;
        _cluster[count] = cluster;
        _clusterLen[count] = len;
        count++;
    }

    private int ApplyLigatures(PdfFont font, Dictionary<ushort, List<Fonts.GsubTable.Ligature>> ligs, int count, ReadOnlySpan<char> s)
    {
        int w = 0;
        for (int r = 0; r < count; r++)
        {
            ushort g = _gid[r];
            int matched = 0;
            ushort ligGlyph = 0;
            if (_gfont[r] == 0 && ligs.TryGetValue(g, out var list) && s[_cluster[r]] >= 0x0600)
            {
                foreach (var lig in list)
                {
                    var comps = lig.Components;
                    if (r + comps.Length > count) continue;
                    bool ok = true;
                    for (int k = 1; k < comps.Length; k++)
                        if (_gid[r + k] != comps[k] || _gfont[r + k] != 0) { ok = false; break; }
                    if (ok) { matched = comps.Length; ligGlyph = lig.Glyph; break; }
                }
            }
            if (matched > 1)
            {
                int cl = _cluster[r];
                int last = r + matched - 1;
                int len = _cluster[last] + _clusterLen[last] - cl;
                font.Use(ligGlyph, new string(s.Slice(cl, len)));
                _gid[w] = ligGlyph; _gfont[w] = 0; _cluster[w] = cl; _clusterLen[w] = len;
                w++;
                r = last;
            }
            else
            {
                _gid[w] = _gid[r]; _gfont[w] = _gfont[r]; _cluster[w] = _cluster[r]; _clusterLen[w] = _clusterLen[r];
                w++;
            }
        }
        return w;
    }

    /// <summary>چیدمان نمایشی (L1, L2, L4) یک بازه از glyph ها و نوشتن در بافر خروجی.</summary>
    private GlyphLine EmitLine(PdfFont font, ReadOnlySpan<char> s, int start, int end, int baseLevel, int outPos)
    {
        int len = end - start;
        var fontBuf = _anyFallback ? _outFont : null;
        if (len <= 0) return new GlyphLine(_out, fontBuf, outPos, 0, 0);
        Span<byte> lv = len <= 512 ? stackalloc byte[len] : new byte[len];
        for (int k = 0; k < len; k++) lv[k] = _levels[_cluster[start + k]];
        // L1: فاصله‌های انتهای خط
        for (int k = len - 1; k >= 0 && IsSpace(s[_cluster[start + k]]); k--) lv[k] = (byte)baseLevel;

        var order = _order.AsSpan(0, len);
        BidiAlgorithm.Reorder(lv, order);

        int width = 0;
        for (int k = 0; k < len; k++)
        {
            int li = order[k];
            int gi = start + li;
            ushort g = _gid[gi];
            byte fi = _gfont[gi];
            if ((lv[li] & 1) == 1 && _clusterLen[gi] == 1 && UnicodeData.TryMirror(s[_cluster[gi]], out var m))
            {
                var f = fi == 0 ? font : font.FallbackAt(fi);
                ushort mg = f.Ttf.GetGlyph(m);
                if (mg != 0)
                {
                    g = mg;
                    if (!f.Used[g] || f.ToUnicode[g] == null) f.Use(g, m);
                }
            }
            _out[outPos + k] = g;
            _outFont[outPos + k] = fi;
            width += Adv(font, g, fi);
        }
        return new GlyphLine(_out, fontBuf, outPos, len, width);
    }
}
