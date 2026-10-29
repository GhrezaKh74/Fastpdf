using System.Buffers.Binary;

namespace FastPdfWriterStream.Fonts;

/// <summary>جدول GDEF — فقط کلاس glyph ها (برای تشخیص اعراب/mark).</summary>
internal sealed class GdefTable
{
    private readonly Dictionary<ushort, byte> _classes = new();

    public GdefTable(byte[] d, int o)
    {
        int classDef = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o + 4));
        if (classDef != 0) OpenType.ReadClassDef(d, o + classDef, _classes);
    }

    public bool IsMark(ushort glyph) => _classes.TryGetValue(glyph, out var c) && c == 3;
}

/// <summary>
/// جدول GSUB — پشتیبانی از lookup های نوع ۱ (single)، ۴ (ligature) و ۷ (extension).
/// نتایج هر feature یک بار محاسبه و در Dictionary کش می‌شود تا شکل‌دهی O(1) باشد.
/// </summary>
internal sealed class GsubTable
{
    private readonly byte[] _d;
    private readonly int _o;
    private readonly int _scriptList, _featureList, _lookupList;
    private readonly Dictionary<string, Dictionary<ushort, ushort>> _singleCache = new();
    private readonly Dictionary<string, Dictionary<ushort, List<Ligature>>> _ligCache = new();

    internal readonly record struct Ligature(ushort[] Components, ushort Glyph, bool IgnoreMarks);

    public GsubTable(byte[] d, int o)
    {
        _d = d;
        _o = o;
        _scriptList = o + U16(o + 4);
        _featureList = o + U16(o + 6);
        _lookupList = o + U16(o + 8);
    }

    private ushort U16(int p) => BinaryPrimitives.ReadUInt16BigEndian(_d.AsSpan(p, 2));
    private uint U32(int p) => BinaryPrimitives.ReadUInt32BigEndian(_d.AsSpan(p, 4));
    private string Tag(int p) => System.Text.Encoding.ASCII.GetString(_d, p, 4);

    public bool HasScript(string script)
    {
        int n = U16(_scriptList);
        for (int i = 0; i < n; i++)
            if (Tag(_scriptList + 2 + i * 6) == script) return true;
        return false;
    }

    /// <summary>اندیس lookup های یک feature برای اسکریپت داده‌شده (با fallback به DFLT).</summary>
    public List<int> GetLookups(string script, string feature)
    {
        var result = new List<int>();
        int langSys = FindLangSys(script) ;
        if (langSys < 0) langSys = FindLangSys("DFLT");
        if (langSys < 0) langSys = FindLangSys("latn");
        if (langSys < 0) return result;

        int featCount = U16(langSys + 4);
        int nFeatures = U16(_featureList);
        for (int i = 0; i < featCount; i++)
        {
            int fi = U16(langSys + 6 + i * 2);
            if (fi >= nFeatures) continue;
            int rec = _featureList + 2 + fi * 6;
            if (Tag(rec) != feature) continue;
            int feat = _featureList + U16(rec + 4);
            int lc = U16(feat + 2);
            for (int k = 0; k < lc; k++) result.Add(U16(feat + 4 + k * 2));
        }
        result.Sort();
        return result;
    }

    private int FindLangSys(string script)
    {
        int n = U16(_scriptList);
        for (int i = 0; i < n; i++)
        {
            int rec = _scriptList + 2 + i * 6;
            if (Tag(rec) != script) continue;
            int s = _scriptList + U16(rec + 4);
            int def = U16(s);
            if (def != 0) return s + def;
            int lc = U16(s + 2);
            // اولویت با زبان فارسی، سپس اولین زبان
            for (int k = 0; k < lc; k++)
            {
                int lr = s + 4 + k * 6;
                if (Tag(lr) == "FAR ") return s + U16(lr + 4);
            }
            if (lc > 0) return s + U16(s + 4 + 4);
        }
        return -1;
    }

    /// <summary>جدول جایگزینی تکی یک feature (ترکیب همهٔ lookup ها به ترتیب).</summary>
    public Dictionary<ushort, ushort> GetSingleMap(string script, string feature)
    {
        string key = script + "/" + feature;
        lock (_singleCache)
        {
            if (_singleCache.TryGetValue(key, out var cached)) return cached;
            var map = new Dictionary<ushort, ushort>();
            foreach (var li in GetLookups(script, feature))
            {
                var step = new Dictionary<ushort, ushort>();
                foreach (var (type, sub, _) in Subtables(li))
                    if (type == 1) ReadSingle(sub, step);
                if (step.Count == 0) continue;
                // ترکیب: اعمال روی خروجی‌های قبلی و افزودن ورودی‌های جدید
                foreach (var k in map.Keys.ToArray())
                    if (step.TryGetValue(map[k], out var v2)) map[k] = v2;
                foreach (var kv in step)
                    if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
            }
            _singleCache[key] = map;
            return map;
        }
    }

    /// <summary>لیگچرهای یک feature، بر اساس glyph اول.</summary>
    public Dictionary<ushort, List<Ligature>> GetLigatures(string script, string feature)
    {
        string key = script + "/" + feature;
        lock (_ligCache)
        {
            if (_ligCache.TryGetValue(key, out var cached)) return cached;
            var map = new Dictionary<ushort, List<Ligature>>();
            foreach (var li in GetLookups(script, feature))
                foreach (var (type, sub, flag) in Subtables(li))
                    if (type == 4) ReadLigatures(sub, map, (flag & 0x8) != 0);
            // لیگچرهای طولانی‌تر اولویت دارند
            foreach (var list in map.Values) list.Sort((a, b) => b.Components.Length.CompareTo(a.Components.Length));
            _ligCache[key] = map;
            return map;
        }
    }

    private IEnumerable<(int Type, int Offset, int Flag)> Subtables(int lookupIndex)
    {
        int n = U16(_lookupList);
        if (lookupIndex >= n) yield break;
        int lk = _lookupList + U16(_lookupList + 2 + lookupIndex * 2);
        int type = U16(lk), flag = U16(lk + 2), count = U16(lk + 4);
        for (int i = 0; i < count; i++)
        {
            int sub = lk + U16(lk + 6 + i * 2);
            if (type == 7)
            {
                int extType = U16(sub + 2);
                int extOff = (int)U32(sub + 4);
                yield return (extType, sub + extOff, flag);
            }
            else yield return (type, sub, flag);
        }
    }

    private void ReadSingle(int sub, Dictionary<ushort, ushort> map)
    {
        int format = U16(sub);
        var coverage = OpenType.ReadCoverage(_d, sub + U16(sub + 2));
        if (format == 1)
        {
            short delta = (short)U16(sub + 4);
            foreach (var g in coverage)
                map.TryAdd(g, (ushort)((g + delta) & 0xFFFF));
        }
        else if (format == 2)
        {
            int count = U16(sub + 4);
            for (int i = 0; i < coverage.Count && i < count; i++)
                map.TryAdd(coverage[i], U16(sub + 6 + i * 2));
        }
    }

    private void ReadLigatures(int sub, Dictionary<ushort, List<Ligature>> map, bool ignoreMarks)
    {
        if (U16(sub) != 1) return;
        var coverage = OpenType.ReadCoverage(_d, sub + U16(sub + 2));
        int setCount = U16(sub + 4);
        for (int i = 0; i < setCount && i < coverage.Count; i++)
        {
            int set = sub + U16(sub + 6 + i * 2);
            int ligCount = U16(set);
            for (int k = 0; k < ligCount; k++)
            {
                int lig = set + U16(set + 2 + k * 2);
                ushort glyph = U16(lig);
                int comp = U16(lig + 2);
                var comps = new ushort[comp];
                comps[0] = coverage[i];
                for (int c = 1; c < comp; c++) comps[c] = U16(lig + 4 + (c - 1) * 2);
                if (!map.TryGetValue(coverage[i], out var list)) map[coverage[i]] = list = new List<Ligature>();
                list.Add(new Ligature(comps, glyph, ignoreMarks));
            }
        }
    }
}

internal static class OpenType
{
    private static ushort U16(byte[] d, int p) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 2));

    /// <summary>جدول Coverage را به لیست glyph ها (به ترتیب اندیس coverage) تبدیل می‌کند.</summary>
    public static List<ushort> ReadCoverage(byte[] d, int o)
    {
        var list = new List<ushort>();
        int format = U16(d, o);
        if (format == 1)
        {
            int n = U16(d, o + 2);
            for (int i = 0; i < n; i++) list.Add(U16(d, o + 4 + i * 2));
        }
        else if (format == 2)
        {
            int n = U16(d, o + 2);
            var tmp = new SortedDictionary<int, ushort>();
            for (int i = 0; i < n; i++)
            {
                int r = o + 4 + i * 6;
                int start = U16(d, r), end = U16(d, r + 2), idx = U16(d, r + 4);
                for (int g = start; g <= end; g++) tmp[idx + g - start] = (ushort)g;
            }
            list.AddRange(tmp.Values);
        }
        return list;
    }

    public static void ReadClassDef(byte[] d, int o, Dictionary<ushort, byte> classes)
    {
        int format = U16(d, o);
        if (format == 1)
        {
            int start = U16(d, o + 2), n = U16(d, o + 4);
            for (int i = 0; i < n; i++) classes[(ushort)(start + i)] = (byte)U16(d, o + 6 + i * 2);
        }
        else if (format == 2)
        {
            int n = U16(d, o + 2);
            for (int i = 0; i < n; i++)
            {
                int r = o + 4 + i * 6;
                int start = U16(d, r), end = U16(d, r + 2);
                byte cls = (byte)U16(d, r + 4);
                for (int g = start; g <= end; g++) classes[(ushort)g] = cls;
            }
        }
    }
}
