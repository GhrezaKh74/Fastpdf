using System.Buffers.Binary;
using System.Text;

namespace FastPdfWriterStream.Fonts;

/// <summary>
/// پارسر سبک فونت TrueType/OpenType. فقط جداول لازم برای اندازه‌گیری، شکل‌دهی و embed را می‌خواند.
/// </summary>
public sealed class TrueTypeFont
{
    internal readonly byte[] Data;
    internal readonly Dictionary<string, (int Offset, int Length)> Tables = new(StringComparer.Ordinal);

    public string FamilyName { get; private set; } = "Font";
    public string PostScriptName { get; private set; } = "Font";
    public int UnitsPerEm { get; private set; } = 1000;
    public int NumGlyphs { get; private set; }
    public short Ascender { get; private set; }
    public short Descender { get; private set; }
    public short LineGap { get; private set; }
    public short CapHeight { get; private set; }
    public short XMin { get; private set; }
    public short YMin { get; private set; }
    public short XMax { get; private set; }
    public short YMax { get; private set; }
    public float ItalicAngle { get; private set; }
    public bool IsFixedPitch { get; private set; }
    public int Weight { get; private set; } = 400;
    public bool IsBold { get; private set; }
    public bool IsItalic { get; private set; }

    /// <summary>true اگر خطوط فونت CFF باشد (OpenType/CFF) — در این حالت subset انجام نمی‌شود.</summary>
    public bool IsCff { get; private set; }

    internal int IndexToLocFormat;
    internal ushort[] Advances = Array.Empty<ushort>();

    // نگاشت سریع BMP: char → glyph id
    private readonly ushort[] _bmp = new ushort[65536];
    private Dictionary<int, ushort>? _supplementary;

    internal GsubTable? Gsub { get; private set; }
    internal GdefTable? Gdef { get; private set; }

    public static TrueTypeFont FromFile(string path, int collectionIndex = 0) =>
        new(File.ReadAllBytes(path), collectionIndex);

    public static TrueTypeFont FromStream(Stream stream, int collectionIndex = 0)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new TrueTypeFont(ms.ToArray(), collectionIndex);
    }

    public TrueTypeFont(byte[] data, int collectionIndex = 0)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        int dirOffset = 0;
        uint tag = U32(0);
        if (tag == 0x74746366) // 'ttcf'
        {
            int numFonts = (int)U32(8);
            if (collectionIndex < 0 || collectionIndex >= numFonts)
                throw new ArgumentOutOfRangeException(nameof(collectionIndex));
            dirOffset = (int)U32(12 + collectionIndex * 4);
            tag = U32(dirOffset);
        }
        if (tag != 0x00010000 && tag != 0x4F54544F /*OTTO*/ && tag != 0x74727565 /*true*/)
            throw new InvalidDataException("Not a TrueType/OpenType font.");
        IsCff = tag == 0x4F54544F;

        int numTables = U16(dirOffset + 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = dirOffset + 12 + i * 16;
            string t = Encoding.ASCII.GetString(Data, rec, 4);
            int off = (int)U32(rec + 8);
            int len = (int)U32(rec + 12);
            if (off + len > Data.Length) len = Math.Max(0, Data.Length - off);
            Tables[t] = (off, len);
        }

        ReadHead();
        ReadMaxp();
        ReadHhea(out int numberOfHMetrics);
        ReadHmtx(numberOfHMetrics);
        ReadOs2();
        ReadPost();
        ReadName();
        ReadCmap();
        if (Tables.ContainsKey("CFF ")) IsCff = true;

        if (Tables.TryGetValue("GDEF", out var gdef) && gdef.Length > 0)
        {
            try { Gdef = new GdefTable(Data, gdef.Offset); } catch { Gdef = null; }
        }
        if (Tables.TryGetValue("GSUB", out var gsub) && gsub.Length > 0)
        {
            try { Gsub = new GsubTable(Data, gsub.Offset); } catch { Gsub = null; }
        }
    }

    // ── خواندن اعداد Big-Endian ──────────────────────────────────────
    internal ushort U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(o, 2));
    internal short S16(int o) => BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(o, 2));
    internal uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o, 4));

    private int Table(string tag, bool required = true)
    {
        if (Tables.TryGetValue(tag, out var t)) return t.Offset;
        if (required) throw new InvalidDataException($"Font table '{tag}' is missing.");
        return -1;
    }

    private void ReadHead()
    {
        int o = Table("head");
        UnitsPerEm = U16(o + 18);
        XMin = S16(o + 36); YMin = S16(o + 38); XMax = S16(o + 40); YMax = S16(o + 42);
        int macStyle = U16(o + 44);
        IsBold = (macStyle & 1) != 0;
        IsItalic = (macStyle & 2) != 0;
        IndexToLocFormat = S16(o + 50);
    }

    private void ReadMaxp() => NumGlyphs = U16(Table("maxp") + 4);

    private void ReadHhea(out int numberOfHMetrics)
    {
        int o = Table("hhea");
        Ascender = S16(o + 4);
        Descender = S16(o + 6);
        LineGap = S16(o + 8);
        numberOfHMetrics = U16(o + 34);
    }

    private void ReadHmtx(int numberOfHMetrics)
    {
        int o = Table("hmtx");
        Advances = new ushort[Math.Max(NumGlyphs, 1)];
        ushort last = 0;
        for (int i = 0; i < NumGlyphs; i++)
        {
            if (i < numberOfHMetrics)
            {
                int p = o + i * 4;
                if (p + 2 <= Data.Length) last = U16(p);
            }
            Advances[i] = last;
        }
    }

    private void ReadOs2()
    {
        int o = Table("OS/2", false);
        CapHeight = (short)(Ascender * 0.7);
        if (o < 0) return;
        int version = U16(o);
        Weight = U16(o + 4);
        if (Weight >= 600) IsBold = true;
        short typoAsc = S16(o + 68), typoDesc = S16(o + 70);
        ushort winAsc = U16(o + 74), winDesc = U16(o + 76);
        // اکثر فونت‌های فارسی مقادیر win را دقیق‌تر دارند (برای جلوگیری از بریده شدن نقطه‌ها)
        if (winAsc > 0)
        {
            Ascender = (short)winAsc;
            Descender = (short)-winDesc;
            LineGap = 0;
        }
        else if (typoAsc > 0)
        {
            Ascender = typoAsc;
            Descender = typoDesc;
        }
        if (version >= 2 && Tables["OS/2"].Length >= 90) CapHeight = S16(o + 88);
        if (CapHeight <= 0) CapHeight = (short)(Ascender * 0.7);
    }

    private void ReadPost()
    {
        int o = Table("post", false);
        if (o < 0) return;
        ItalicAngle = S16(o + 4) + U16(o + 6) / 65536f;
        IsFixedPitch = U32(o + 12) != 0;
    }

    private void ReadName()
    {
        int o = Table("name", false);
        if (o < 0) return;
        int count = U16(o + 2);
        int strOff = o + U16(o + 4);
        string? family = null, ps = null;
        for (int i = 0; i < count; i++)
        {
            int r = o + 6 + i * 12;
            int platform = U16(r), encoding = U16(r + 2), nameId = U16(r + 6);
            int len = U16(r + 8), off = U16(r + 10);
            if (nameId != 1 && nameId != 6) continue;
            string? value = null;
            if (platform == 3 || platform == 0)
                value = Encoding.BigEndianUnicode.GetString(Data, strOff + off, len);
            else if (platform == 1 && encoding == 0)
                value = Encoding.Latin1.GetString(Data, strOff + off, len);
            if (value == null) continue;
            if (nameId == 1 && (family == null || platform == 3)) family = value;
            if (nameId == 6 && (ps == null || platform == 3)) ps = value;
        }
        FamilyName = family ?? "Font";
        PostScriptName = Sanitize(ps ?? FamilyName);
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c > 32 && c < 127 && c != '[' && c != ']' && c != '(' && c != ')' && c != '{' && c != '}' &&
                c != '<' && c != '>' && c != '/' && c != '%' && c != '#')
                sb.Append(c);
        return sb.Length == 0 ? "Font" : sb.ToString();
    }

    private void ReadCmap()
    {
        int o = Table("cmap", false);
        if (o < 0) return; // فونت subset شده (بدون cmap)
        int n = U16(o + 2);
        int best = -1, bestScore = -1;
        for (int i = 0; i < n; i++)
        {
            int r = o + 4 + i * 8;
            int platform = U16(r), encoding = U16(r + 2);
            int sub = o + (int)U32(r + 4);
            int format = U16(sub);
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (0, 4, 12) or (0, 6, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                (3, 0, 4) => 1, // symbol
                _ => -1
            };
            if (score > bestScore) { bestScore = score; best = sub; }
        }
        if (best < 0) return;
        // اگر فرمت ۱۲ هست آن را بخوان، وگرنه فرمت ۴
        if (U16(best) == 12) ReadCmap12(best); else ReadCmap4(best);

        // فونت‌های symbol: نگاشت F000 به ASCII
        if (bestScore == 1)
            for (int c = 0x20; c < 0x100; c++)
                if (_bmp[c] == 0 && _bmp[0xF000 + c] != 0) _bmp[c] = _bmp[0xF000 + c];
    }

    private void ReadCmap4(int o)
    {
        int segX2 = U16(o + 6);
        int ends = o + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (int s = 0; s < segX2 / 2; s++)
        {
            int end = U16(ends + s * 2), start = U16(starts + s * 2);
            short delta = S16(deltas + s * 2);
            int rangeOffPos = ranges + s * 2;
            int rangeOff = U16(rangeOffPos);
            if (start > end) continue;
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int g;
                if (rangeOff == 0) g = (c + delta) & 0xFFFF;
                else
                {
                    int gp = rangeOffPos + rangeOff + (c - start) * 2;
                    if (gp + 2 > Data.Length) continue;
                    g = U16(gp);
                    if (g != 0) g = (g + delta) & 0xFFFF;
                }
                if (g < NumGlyphs) _bmp[c] = (ushort)g;
            }
        }
    }

    private void ReadCmap12(int o)
    {
        int nGroups = (int)U32(o + 12);
        for (int i = 0; i < nGroups; i++)
        {
            int r = o + 16 + i * 12;
            uint start = U32(r), end = U32(r + 4), g = U32(r + 8);
            for (uint c = start; c <= end && c <= 0x10FFFF; c++, g++)
            {
                if (g >= NumGlyphs) continue;
                if (c < 0x10000) _bmp[c] = (ushort)g;
                else (_supplementary ??= new())[(int)c] = (ushort)g;
            }
        }
    }

    /// <summary>شناسهٔ glyph برای یک کاراکتر (0 اگر وجود نداشته باشد).</summary>
    public ushort GetGlyph(int codePoint)
    {
        if ((uint)codePoint < 0x10000) return _bmp[codePoint];
        return _supplementary != null && _supplementary.TryGetValue(codePoint, out var g) ? g : (ushort)0;
    }

    public bool HasGlyph(int codePoint) => GetGlyph(codePoint) != 0;

    internal ushort[] BmpMap => _bmp;

    /// <summary>عرض یک glyph در واحد فونت.</summary>
    public int GetAdvance(ushort glyph) => glyph < Advances.Length ? Advances[glyph] : 0;

    /// <summary>عرض متن ساده (بدون شکل‌دهی) در واحد point.</summary>
    public double MeasureSimple(ReadOnlySpan<char> text, double size)
    {
        long w = 0;
        foreach (var c in text) w += Advances[_bmp[c]];
        return w * size / UnitsPerEm;
    }

    internal ReadOnlySpan<byte> TableSpan(string tag) =>
        Tables.TryGetValue(tag, out var t) ? Data.AsSpan(t.Offset, t.Length) : ReadOnlySpan<byte>.Empty;
}
