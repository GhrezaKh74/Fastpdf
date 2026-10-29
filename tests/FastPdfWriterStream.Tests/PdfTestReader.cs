using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FastPdfWriterStream.Tests;

/// <summary>
/// خوانندهٔ حداقلی PDF برای تست خروجی همین کتابخانه: اعتبار xref، تعداد صفحات و استخراج متن (از طریق ToUnicode).
/// </summary>
internal sealed class PdfTestReader
{
    public readonly byte[] Data;
    public readonly Dictionary<int, (string Dict, byte[]? Stream)> Objects = new();
    private readonly string _latin;

    public PdfTestReader(byte[] data)
    {
        Data = data;
        _latin = Encoding.Latin1.GetString(data);
        Assert.StartsWith("%PDF-1.7", _latin);
        Assert.EndsWith("%%EOF\n", _latin);
        ValidateXref();
    }

    private void ValidateXref()
    {
        int sx = _latin.LastIndexOf("startxref", StringComparison.Ordinal);
        long xrefPos = long.Parse(_latin[(sx + 10).._latin.IndexOf('\n', sx + 10)].Trim());
        Assert.Equal("xref", _latin.Substring((int)xrefPos, 4));
        var lines = _latin[(int)xrefPos..].Split('\n');
        var header = lines[1].Split(' ');
        int count = int.Parse(header[1]);
        for (int i = 1; i < count; i++)
        {
            var entry = lines[2 + i];
            if (entry[17] != 'n') continue;
            long off = long.Parse(entry[..10]);
            var expected = $"{i} 0 obj";
            Assert.True(_latin.Substring((int)off, expected.Length) == expected, $"xref offset of object {i} is wrong");
            ReadObject(i, (int)off + expected.Length);
        }
    }

    private void ReadObject(int id, int pos)
    {
        int end = _latin.IndexOf("endobj", pos, StringComparison.Ordinal);
        var body = _latin[pos..end];
        int st = body.IndexOf("stream\n", StringComparison.Ordinal);
        if (st < 0)
        {
            Objects[id] = (body.Trim(), null);
            return;
        }
        var dict = body[..st];
        var m = Regex.Match(dict, @"/Length (\d+)");
        int len = int.Parse(m.Groups[1].Value);
        var raw = Data.AsSpan(pos + st + 7, len).ToArray();
        Assert.Equal("\nendstream", _latin.Substring(pos + st + 7 + len, 10));
        if (dict.Contains("/FlateDecode") && !dict.Contains("/Subtype /Image") && !dict.Contains("/DecodeParms"))
        {
            using var zs = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            zs.CopyTo(ms);
            raw = ms.ToArray();
        }
        Objects[id] = (dict, raw);
    }

    public int PageCount
    {
        get
        {
            var catalog = Objects.Values.First(o => o.Dict.Contains("/Type /Catalog"));
            int pagesId = int.Parse(Regex.Match(catalog.Dict, @"/Pages (\d+) 0 R").Groups[1].Value);
            return int.Parse(Regex.Match(Objects[pagesId].Dict, @"/Count (\d+)").Groups[1].Value);
        }
    }

    public List<int> PageIds() => Objects.Where(o => o.Value.Dict.Contains("/Type /Page ")).Select(o => o.Key).OrderBy(k => k).ToList();

    private Dictionary<string, Dictionary<int, string>>? _fontMaps;

    private Dictionary<string, Dictionary<int, string>> FontMaps()
    {
        if (_fontMaps != null) return _fontMaps;
        _fontMaps = new();
        var res = Objects.Values.First(o => o.Dict.StartsWith("<< /ProcSet"));
        foreach (Match m in Regex.Matches(res.Dict, @"/(F\d+) (\d+) 0 R"))
        {
            var type0 = Objects[int.Parse(m.Groups[2].Value)].Dict;
            int tu = int.Parse(Regex.Match(type0, @"/ToUnicode (\d+) 0 R").Groups[1].Value);
            var cmap = Encoding.ASCII.GetString(Objects[tu].Stream!);
            var map = new Dictionary<int, string>();
            foreach (Match e in Regex.Matches(cmap, @"<([0-9A-F]{4})> <([0-9A-F]+)>"))
            {
                var hex = e.Groups[2].Value;
                var sb = new StringBuilder();
                for (int i = 0; i < hex.Length; i += 4) sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
                map[Convert.ToInt32(e.Groups[1].Value, 16)] = sb.ToString();
            }
            _fontMaps[m.Groups[1].Value] = map;
        }
        return _fontMaps;
    }

    /// <summary>متن صفحه به ترتیب نمایشی (هر دستور Tj با فاصله جدا می‌شود).</summary>
    public string PageText(int pageIndex)
    {
        var pageDict = Objects[PageIds()[pageIndex]].Dict;
        int contentId = int.Parse(Regex.Match(pageDict, @"/Contents (\d+) 0 R").Groups[1].Value);
        var content = Objects[contentId].Stream!;
        var maps = FontMaps();
        var sb = new StringBuilder();
        string font = "F1";
        int i = 0;
        while (i < content.Length)
        {
            byte b = content[i];
            if (b == '/' && i + 1 < content.Length && content[i + 1] == 'F')
            {
                int s = i + 1;
                while (i < content.Length && content[i] != ' ') i++;
                font = Encoding.ASCII.GetString(content, s, i - s);
                continue;
            }
            if (b == '(')
            {
                var bytes = new List<byte>();
                i++;
                while (content[i] != ')')
                {
                    if (content[i] == '\\')
                    {
                        i++;
                        bytes.Add(content[i] == 'r' ? (byte)'\r' : content[i]);
                    }
                    else bytes.Add(content[i]);
                    i++;
                }
                i++;
                var map = maps.GetValueOrDefault(font) ?? new();
                for (int k = 0; k + 1 < bytes.Count; k += 2)
                {
                    int gid = (bytes[k] << 8) | bytes[k + 1];
                    sb.Append(map.TryGetValue(gid, out var u) ? u : "?");
                }
                sb.Append(' ');
                continue;
            }
            i++;
        }
        return sb.ToString();
    }

    public string AllText() => string.Join("\n", Enumerable.Range(0, PageIds().Count).Select(PageText));

    public static string Reverse(string s)
    {
        var arr = s.ToCharArray();
        Array.Reverse(arr);
        return new string(arr);
    }
}
