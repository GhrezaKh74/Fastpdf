using System.Buffers.Binary;

namespace FastPdfWriterStream.Fonts;

/// <summary>
/// Subset کردن فونت TrueType: شناسهٔ glyph ها ثابت می‌ماند (CIDToGIDMap=Identity) ولی دادهٔ
/// glyph های استفاده‌نشده حذف می‌شود. بسیار سریع است و نیازی به نگاشت مجدد ندارد.
/// </summary>
internal static class FontSubsetter
{
    private static readonly string[] KeepTables = { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cvt ", "fpgm", "prep" };

    public static byte[] Subset(TrueTypeFont font, ICollection<ushort> usedGlyphs)
    {
        if (!font.Tables.ContainsKey("glyf") || !font.Tables.ContainsKey("loca"))
            return font.Data;

        var data = font.Data;
        int n = font.NumGlyphs;
        var (locaOff, _) = font.Tables["loca"];
        var (glyfOff, glyfLen) = font.Tables["glyf"];
        bool longLoca = font.IndexToLocFormat == 1;

        int Loca(int i) => longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(locaOff + i * 4))
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(locaOff + i * 2)) * 2;

        // مجموعهٔ glyph ها به همراه اجزای glyph های مرکب
        var keep = new bool[n];
        keep[0] = true;
        var stack = new Stack<int>();
        foreach (var g in usedGlyphs)
            if (g < n && !keep[g]) { keep[g] = true; stack.Push(g); }
        while (stack.Count > 0)
        {
            int g = stack.Pop();
            int start = Loca(g), end = Loca(g + 1);
            if (end - start < 10 || start + 10 > glyfLen) continue;
            int p = glyfOff + start;
            short contours = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p));
            if (contours >= 0) continue;
            p += 10;
            while (true)
            {
                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                ushort comp = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 2));
                if (comp < n && !keep[comp]) { keep[comp] = true; stack.Push(comp); }
                p += 4;
                p += (flags & 0x0001) != 0 ? 4 : 2;
                if ((flags & 0x0008) != 0) p += 2;
                else if ((flags & 0x0040) != 0) p += 4;
                else if ((flags & 0x0080) != 0) p += 8;
                if ((flags & 0x0020) == 0) break;
            }
        }

        // ساخت glyf و loca جدید (loca همیشه long)
        var newLoca = new byte[(n + 1) * 4];
        int total = 0;
        for (int g = 0; g < n; g++)
            if (keep[g]) total += Align4(Loca(g + 1) - Loca(g));
        var newGlyf = new byte[Math.Max(total, 4)];
        int pos = 0;
        for (int g = 0; g < n; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)pos);
            if (!keep[g]) continue;
            int s = Loca(g), e = Loca(g + 1);
            int len = Math.Max(0, Math.Min(e, glyfLen) - s);
            if (len > 0) data.AsSpan(glyfOff + s, len).CopyTo(newGlyf.AsSpan(pos));
            pos += Align4(len);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(n * 4), (uint)pos);

        var tables = new List<(string Tag, byte[] Data)>();
        foreach (var tag in KeepTables)
        {
            if (!font.Tables.TryGetValue(tag, out var t)) continue;
            byte[] td = tag switch
            {
                "glyf" => newGlyf.AsSpan(0, Math.Max(pos, 4)).ToArray(),
                "loca" => newLoca,
                _ => data.AsSpan(t.Offset, t.Length).ToArray()
            };
            if (tag == "head")
            {
                BinaryPrimitives.WriteInt16BigEndian(td.AsSpan(50), 1); // indexToLocFormat = long
                BinaryPrimitives.WriteUInt32BigEndian(td.AsSpan(8), 0);  // checkSumAdjustment
            }
            tables.Add((tag, td));
        }
        return BuildFont(tables);
    }

    private static int Align4(int v) => (v + 3) & ~3;

    private static byte[] BuildFont(List<(string Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
        int num = tables.Count;
        int headerSize = 12 + num * 16;
        int size = headerSize;
        foreach (var t in tables) size += Align4(t.Data.Length);
        var output = new byte[size];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)num);
        int entrySelector = (int)Math.Floor(Math.Log2(num));
        int searchRange = (1 << entrySelector) * 16;
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(num * 16 - searchRange));

        int offset = headerSize;
        for (int i = 0; i < num; i++)
        {
            var (tag, td) = tables[i];
            int rec = 12 + i * 16;
            for (int k = 0; k < 4; k++) output[rec + k] = (byte)tag[k];
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 4)..], Checksum(td));
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 8)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 12)..], (uint)td.Length);
            td.CopyTo(output, offset);
            offset += Align4(td.Length);
        }
        return output;
    }

    private static uint Checksum(byte[] d)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= d.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
        if (i < d.Length)
        {
            Span<byte> last = stackalloc byte[4];
            d.AsSpan(i).CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }
        return sum;
    }
}
