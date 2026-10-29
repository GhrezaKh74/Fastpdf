using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FastPdfWriterStream.IO;

namespace FastPdfWriterStream;

/// <summary>
/// تصویر برداری SVG (زیرمجموعهٔ ساده: path/rect/circle/polygon با رنگ پرکننده) — مناسب لوگو.
/// در سند فقط یک بار به صورت Form XObject نوشته می‌شود و در هر صفحه با یک دستور Do رسم می‌شود.
/// </summary>
public sealed class PdfSvg
{
    internal string ResourceName = "";
    internal int ObjectId;

    public double Width { get; }
    public double Height { get; }

    /// <summary>اگر همهٔ اجزا یک رنگ داشته باشند، آن رنگ (و هنگام رسم قابل جایگزینی است).</summary>
    public PdfColor? MonoColor { get; }

    private readonly List<Shape> _shapes;

    private readonly record struct Shape(string Ops, PdfColor? Fill, bool EvenOdd);

    private PdfSvg(double w, double h, List<Shape> shapes)
    {
        Width = w;
        Height = h;
        _shapes = shapes;
        var colors = shapes.Select(s => s.Fill).Distinct().ToList();
        if (colors.Count == 1) MonoColor = colors[0];
    }

    public static PdfSvg FromFile(string path) => Parse(File.ReadAllText(path));

    public static PdfSvg Parse(string svgText)
    {
        var doc = XDocument.Parse(svgText);
        var root = doc.Root ?? throw new InvalidDataException("Empty SVG.");
        double minX = 0, minY = 0, w, h;
        var vb = (string?)root.Attribute("viewBox");
        if (!string.IsNullOrWhiteSpace(vb))
        {
            var p = Numbers(vb);
            if (p.Count < 4) throw new InvalidDataException("Invalid viewBox.");
            (minX, minY, w, h) = (p[0], p[1], p[2], p[3]);
        }
        else
        {
            w = Length((string?)root.Attribute("width")) ?? 100;
            h = Length((string?)root.Attribute("height")) ?? 100;
        }

        var classes = ParseStyleClasses(root);
        var shapes = new List<Shape>();
        Walk(root, Matrix.Identity, classes, shapes, minX, minY, h, inheritedFill: "#000000");
        return new PdfSvg(w, h, shapes);
    }

    // ── پیمایش عناصر ──────────────────────────────────────────────────

    private static void Walk(XElement el, Matrix m, Dictionary<string, string> classes, List<Shape> shapes,
        double minX, double minY, double h, string inheritedFill)
    {
        foreach (var child in el.Elements())
        {
            string name = child.Name.LocalName;
            if (name is "defs" or "style" or "title" or "desc" or "metadata" or "clipPath" or "mask") continue;
            var cm = m.Multiply(Transform((string?)child.Attribute("transform")));
            string fill = FillOf(child, classes) ?? inheritedFill;
            if (name == "g" || name == "svg")
            {
                Walk(child, cm, classes, shapes, minX, minY, h, fill);
                continue;
            }
            if (fill == "none") continue;
            string? d = name switch
            {
                "path" => (string?)child.Attribute("d"),
                "rect" => RectPath(child),
                "circle" or "ellipse" => EllipsePath(child),
                "polygon" or "polyline" => PolyPath((string?)child.Attribute("points")),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(d)) continue;
            var sink = new ByteSink(1024);
            PathToPdf(d, cm, minX, minY, h, sink);
            string ops = Encoding.ASCII.GetString(sink.WrittenSpan);
            sink.ReturnBuffer();
            bool evenOdd = string.Equals(AttrOrStyle(child, classes, "fill-rule"), "evenodd", StringComparison.OrdinalIgnoreCase);
            shapes.Add(new Shape(ops, PdfColor.TryParse(fill, out var c) ? c : PdfColor.Black, evenOdd));
        }
    }

    private static Dictionary<string, string> ParseStyleClasses(XElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var style in root.Descendants().Where(e => e.Name.LocalName == "style"))
            foreach (Match m in Regex.Matches(style.Value, @"\.([\w-]+)\s*\{([^}]*)\}"))
                map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    private static string? AttrOrStyle(XElement el, Dictionary<string, string> classes, string prop)
    {
        var style = (string?)el.Attribute("style");
        var fromStyle = StyleProp(style, prop);
        if (fromStyle != null) return fromStyle;
        var attr = (string?)el.Attribute(prop);
        if (attr != null) return attr.Trim();
        var cls = (string?)el.Attribute("class");
        if (cls != null)
            foreach (var c in cls.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (classes.TryGetValue(c, out var css) && StyleProp(css, prop) is { } v) return v;
        return null;
    }

    private static string? FillOf(XElement el, Dictionary<string, string> classes)
    {
        var f = AttrOrStyle(el, classes, "fill");
        if (f == null) return null;
        f = f.Trim();
        if (f.Length == 4 && f[0] == '#') f = $"#{f[1]}{f[1]}{f[2]}{f[2]}{f[3]}{f[3]}";
        return f.Equals("none", StringComparison.OrdinalIgnoreCase) ? "none" : f;
    }

    private static string? StyleProp(string? css, string prop)
    {
        if (string.IsNullOrEmpty(css)) return null;
        foreach (var part in css.Split(';'))
        {
            int i = part.IndexOf(':');
            if (i > 0 && part[..i].Trim().Equals(prop, StringComparison.OrdinalIgnoreCase)) return part[(i + 1)..].Trim();
        }
        return null;
    }

    private static double? Length(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var n = Numbers(s);
        return n.Count > 0 ? n[0] : null;
    }

    private static string RectPath(XElement e)
    {
        double x = Length((string?)e.Attribute("x")) ?? 0, y = Length((string?)e.Attribute("y")) ?? 0;
        double w = Length((string?)e.Attribute("width")) ?? 0, h = Length((string?)e.Attribute("height")) ?? 0;
        return FormattableString.Invariant($"M{x},{y}h{w}v{h}h{-w}Z");
    }

    private static string EllipsePath(XElement e)
    {
        double cx = Length((string?)e.Attribute("cx")) ?? 0, cy = Length((string?)e.Attribute("cy")) ?? 0;
        double rx = Length((string?)e.Attribute("r") ?? (string?)e.Attribute("rx")) ?? 0;
        double ry = Length((string?)e.Attribute("r") ?? (string?)e.Attribute("ry")) ?? rx;
        double k = 0.5523;
        return FormattableString.Invariant(
                   $"M{cx + rx},{cy}C{cx + rx},{cy + ry * k} {cx + rx * k},{cy + ry} {cx},{cy + ry}C{cx - rx * k},{cy + ry} {cx - rx},{cy + ry * k} {cx - rx},{cy}")
               + FormattableString.Invariant(
                   $"C{cx - rx},{cy - ry * k} {cx - rx * k},{cy - ry} {cx},{cy - ry}C{cx + rx * k},{cy - ry} {cx + rx},{cy - ry * k} {cx + rx},{cy}Z");
    }

    private static string? PolyPath(string? points)
    {
        var n = Numbers(points ?? "");
        if (n.Count < 4) return null;
        var sb = new StringBuilder(FormattableString.Invariant($"M{n[0]},{n[1]}"));
        for (int i = 2; i + 1 < n.Count; i += 2) sb.Append(FormattableString.Invariant($"L{n[i]},{n[i + 1]}"));
        return sb.Append('Z').ToString();
    }

    private static List<double> Numbers(string s)
    {
        var list = new List<double>();
        foreach (Match m in NumberRegex.Matches(s))
            list.Add(double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture));
        return list;
    }

    private static readonly Regex NumberRegex = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    // ── تبدیل مسیر SVG به دستورات PDF ────────────────────────────────

    private static void PathToPdf(string d, Matrix m, double minX, double minY, double h, ByteSink s)
    {
        // y در SVG رو به پایین است؛ در Form XObject رو به بالا
        void Pt(double x, double y)
        {
            var (tx, ty) = m.Apply(x, y);
            s.Real(tx - minX); s.Space(); s.Real(h - (ty - minY)); s.Space();
        }

        int i = 0;
        char cmd = ' ';
        double cx = 0, cy = 0, sx = 0, sy = 0, lcx = 0, lcy = 0;
        char prev = ' ';
        while (true)
        {
            SkipSep(d, ref i);
            if (i >= d.Length) break;
            char c = d[i];
            if (char.IsLetter(c) && c != 'e' && c != 'E') { cmd = c; i++; }
            else if (cmd == ' ') throw new InvalidDataException("Invalid SVG path.");
            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    double x = Num(d, ref i), y = Num(d, ref i);
                    if (rel) { x += cx; y += cy; }
                    Pt(x, y); s.Ascii("m ");
                    cx = sx = x; cy = sy = y;
                    cmd = rel ? 'l' : 'L'; // جفت‌های بعدی خط هستند
                    break;
                }
                case 'L':
                {
                    double x = Num(d, ref i), y = Num(d, ref i);
                    if (rel) { x += cx; y += cy; }
                    Pt(x, y); s.Ascii("l ");
                    cx = x; cy = y;
                    break;
                }
                case 'H':
                {
                    double x = Num(d, ref i);
                    if (rel) x += cx;
                    Pt(x, cy); s.Ascii("l ");
                    cx = x;
                    break;
                }
                case 'V':
                {
                    double y = Num(d, ref i);
                    if (rel) y += cy;
                    Pt(cx, y); s.Ascii("l ");
                    cy = y;
                    break;
                }
                case 'C':
                {
                    double x1 = Num(d, ref i), y1 = Num(d, ref i), x2 = Num(d, ref i), y2 = Num(d, ref i), x = Num(d, ref i), y = Num(d, ref i);
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                    Pt(x1, y1); Pt(x2, y2); Pt(x, y); s.Ascii("c ");
                    lcx = x2; lcy = y2; cx = x; cy = y;
                    break;
                }
                case 'S':
                {
                    double x2 = Num(d, ref i), y2 = Num(d, ref i), x = Num(d, ref i), y = Num(d, ref i);
                    if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                    bool smooth = char.ToUpperInvariant(prev) is 'C' or 'S';
                    double x1 = smooth ? 2 * cx - lcx : cx, y1 = smooth ? 2 * cy - lcy : cy;
                    Pt(x1, y1); Pt(x2, y2); Pt(x, y); s.Ascii("c ");
                    lcx = x2; lcy = y2; cx = x; cy = y;
                    break;
                }
                case 'Q':
                {
                    double qx = Num(d, ref i), qy = Num(d, ref i), x = Num(d, ref i), y = Num(d, ref i);
                    if (rel) { qx += cx; qy += cy; x += cx; y += cy; }
                    Quad(qx, qy, x, y);
                    break;
                }
                case 'T':
                {
                    double x = Num(d, ref i), y = Num(d, ref i);
                    if (rel) { x += cx; y += cy; }
                    bool smooth = char.ToUpperInvariant(prev) is 'Q' or 'T';
                    double qx = smooth ? 2 * cx - lcx : cx, qy = smooth ? 2 * cy - lcy : cy;
                    Quad(qx, qy, x, y);
                    break;
                }
                case 'Z':
                    s.Ascii("h ");
                    cx = sx; cy = sy;
                    break;
                case 'A':
                    throw new NotSupportedException("SVG arc commands (A) are not supported; convert arcs to curves.");
                default:
                    throw new InvalidDataException($"Unknown SVG path command '{cmd}'.");
            }
            prev = cmd;
        }

        void Quad(double qx, double qy, double x, double y)
        {
            double x1 = cx + 2.0 / 3 * (qx - cx), y1 = cy + 2.0 / 3 * (qy - cy);
            double x2 = x + 2.0 / 3 * (qx - x), y2 = y + 2.0 / 3 * (qy - y);
            Pt(x1, y1); Pt(x2, y2); Pt(x, y); s.Ascii("c ");
            lcx = qx; lcy = qy; cx = x; cy = y;
        }
    }

    private static void SkipSep(string d, ref int i)
    {
        while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
    }

    private static double Num(string d, ref int i)
    {
        SkipSep(d, ref i);
        var m = NumberRegex.Match(d, i);
        if (!m.Success || m.Index != i) throw new InvalidDataException("Invalid number in SVG path at " + i);
        i += m.Length;
        return double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static Matrix Transform(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return Matrix.Identity;
        var result = Matrix.Identity;
        foreach (Match m in Regex.Matches(t, @"(matrix|translate|scale|rotate)\s*\(([^)]*)\)"))
        {
            var n = Numbers(m.Groups[2].Value);
            var step = m.Groups[1].Value switch
            {
                "matrix" when n.Count == 6 => new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]),
                "translate" => new Matrix(1, 0, 0, 1, n.ElementAtOrDefault(0), n.ElementAtOrDefault(1)),
                "scale" => new Matrix(n.ElementAtOrDefault(0), 0, 0, n.Count > 1 ? n[1] : n.ElementAtOrDefault(0), 0, 0),
                "rotate" => Matrix.Rotate(n.ElementAtOrDefault(0)),
                _ => Matrix.Identity
            };
            result = result.Multiply(step);
        }
        return result;
    }

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        public static Matrix Rotate(double deg)
        {
            double r = deg * Math.PI / 180;
            return new Matrix(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0);
        }

        /// <summary>this × other (other اول اعمال می‌شود).</summary>
        public Matrix Multiply(Matrix o) => new(
            A * o.A + C * o.B, B * o.A + D * o.B,
            A * o.C + C * o.D, B * o.C + D * o.D,
            A * o.E + C * o.F + E, B * o.E + D * o.F + F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }

    // ── نوشتن در PDF ──────────────────────────────────────────────────

    /// <summary>محتوای Form XObject. اگر تک‌رنگ باشد رنگ نوشته نمی‌شود تا هنگام رسم قابل تعیین باشد.</summary>
    internal byte[] BuildContent()
    {
        var s = new ByteSink(4096);
        PdfColor? current = null;
        foreach (var shape in _shapes)
        {
            if (MonoColor == null && shape.Fill is { } f && f != current)
            {
                s.Real(f.R / 255.0); s.Space(); s.Real(f.G / 255.0); s.Space(); s.Real(f.B / 255.0); s.Ascii(" rg\n");
                current = f;
            }
            s.Ascii(shape.Ops);
            s.Ascii(shape.EvenOdd ? "f*\n" : "f\n");
        }
        var bytes = s.WrittenSpan.ToArray();
        s.ReturnBuffer();
        return bytes;
    }
}
