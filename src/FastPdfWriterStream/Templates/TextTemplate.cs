using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FastPdfWriterStream.Text;

namespace FastPdfWriterStream.Templates;

internal enum SegmentKind : byte { Literal, Param, Field, PageNumber, TotalPages, RowNumber, Count, Today, Now, ReportName, Aggregate }

internal enum AggregateKind : byte { None, Sum, Count, Avg, Min, Max }

internal enum AggregateScope : byte { Report, Page, Group }

internal sealed class Segment<T>
{
    public SegmentKind Kind;
    public string Text = "";       // متن ثابت یا نام
    public FormatSpec Spec = FormatSpec.Default;
    public FieldGetter<T>? Getter;
    public AggregateKind Agg;
    public AggregateScope Scope;
    public int AggIndex = -1;
}

/// <summary>
/// متن قالب کامپایل‌شده: "تاریخ: {Date:persian} - جمع: {Sum(Amount):N0}".
/// </summary>
internal sealed class TextTemplate<T>
{
    private static readonly Regex AggRegex = new(@"^(Page|Group)?(Sum|Count|Avg|Min|Max)\(\s*([\w.]*)\s*\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public readonly Segment<T>[] Segments;
    public readonly bool IsConstant;
    public readonly string? ConstantText;
    public readonly bool UsesRow;

    private TextTemplate(Segment<T>[] segments)
    {
        Segments = segments;
        IsConstant = segments.All(s => s.Kind == SegmentKind.Literal);
        UsesRow = segments.Any(s => s.Kind is SegmentKind.Field or SegmentKind.RowNumber);
        if (IsConstant) ConstantText = string.Concat(segments.Select(s => s.Text));
    }

    /// <summary>پارس متن. اگر <paramref name="bareField"/> true باشد، متن بدون {} به عنوان نام فیلد تفسیر می‌شود.</summary>
    public static TextTemplate<T> Parse(string? text, ICompileContext<T> ctx, bool bareField = false, string? defaultFormat = null)
    {
        var list = new List<Segment<T>>();
        text ??= "";
        if (bareField && text.Length > 0 && text.IndexOf('{') < 0)
        {
            list.Add(Resolve(text.Trim(), defaultFormat, ctx, preferRow: true));
            return new TextTemplate<T>(list.ToArray());
        }

        var lit = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { lit.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { lit.Append('}'); i += 2; continue; }
            if (c == '{')
            {
                int end = FindClose(text, i + 1);
                if (end < 0) { lit.Append(text, i, text.Length - i); break; }
                if (lit.Length > 0) { list.Add(new Segment<T> { Kind = SegmentKind.Literal, Text = lit.ToString() }); lit.Clear(); }
                var inner = text.Substring(i + 1, end - i - 1).Trim();
                string name = inner;
                string? fmt = defaultFormat;
                // فرمت بعد از اولین ':' خارج از پرانتز
                int depth = 0;
                for (int k = 0; k < inner.Length; k++)
                {
                    if (inner[k] == '(') depth++;
                    else if (inner[k] == ')') depth--;
                    else if (inner[k] == ':' && depth == 0)
                    {
                        name = inner[..k].Trim();
                        fmt = inner[(k + 1)..];
                        break;
                    }
                }
                list.Add(Resolve(name, fmt, ctx, preferRow: bareField));
                i = end + 1;
                continue;
            }
            lit.Append(c);
            i++;
        }
        if (lit.Length > 0) list.Add(new Segment<T> { Kind = SegmentKind.Literal, Text = lit.ToString() });
        return new TextTemplate<T>(list.ToArray());
    }

    private static int FindClose(string s, int from)
    {
        for (int i = from; i < s.Length; i++)
            if (s[i] == '}') return i;
        return -1;
    }

    private static Segment<T> Resolve(string name, string? fmt, ICompileContext<T> ctx, bool preferRow)
    {
        var seg = new Segment<T> { Text = name, Spec = FormatSpec.Get(fmt) };
        switch (name.ToLowerInvariant())
        {
            case "pagenumber": case "page": seg.Kind = SegmentKind.PageNumber; return seg;
            case "totalpages": case "totalpagecount": case "pagecount": seg.Kind = SegmentKind.TotalPages; return seg;
            case "rownumber": case "line": case "#": seg.Kind = SegmentKind.RowNumber; return seg;
            case "count": seg.Kind = SegmentKind.Count; return seg;
            case "today": seg.Kind = SegmentKind.Today; return seg;
            case "now": seg.Kind = SegmentKind.Now; return seg;
            case "reportname": seg.Kind = SegmentKind.ReportName; return seg;
        }
        var m = AggRegex.Match(name);
        if (m.Success)
        {
            seg.Kind = SegmentKind.Aggregate;
            seg.Scope = m.Groups[1].Value.ToLowerInvariant() switch { "page" => AggregateScope.Page, "group" => AggregateScope.Group, _ => AggregateScope.Report };
            seg.Agg = Enum.Parse<AggregateKind>(m.Groups[2].Value, true);
            var field = m.Groups[3].Value;
            if (field.Length == 0)
            {
                if (seg.Agg != AggregateKind.Count) throw new InvalidDataException($"Aggregate '{name}' needs a field name.");
                seg.Kind = seg.Scope == AggregateScope.Report ? SegmentKind.Count : SegmentKind.Aggregate;
                seg.AggIndex = seg.Kind == SegmentKind.Aggregate ? ctx.RegisterAggregate("") : -1;
                return seg;
            }
            seg.AggIndex = ctx.RegisterAggregate(field);
            return seg;
        }

        if (!preferRow && ctx.HasParameter(name)) { seg.Kind = SegmentKind.Param; return seg; }
        var getter = ctx.GetField(name);
        if (getter != null)
        {
            seg.Kind = SegmentKind.Field;
            seg.Getter = getter;
            return seg;
        }
        seg.Kind = SegmentKind.Param;
        return seg;
    }

    /// <summary>ارزیابی متن.</summary>
    public string Evaluate(IEvalContext<T> ctx, T? row, bool hasRow, ref char[] buf)
    {
        if (IsConstant) return ConstantText!;
        var sb = new StringBuilder();
        foreach (var s in Segments)
        {
            int n;
            switch (s.Kind)
            {
                case SegmentKind.Literal: sb.Append(s.Text); continue;
                case SegmentKind.TotalPages:
                    sb.Append(UnicodeData.TotalPagesMarker);
                    continue;
                case SegmentKind.PageNumber: n = ValueFormatter.WriteDecimal(ctx.PageNumber, s.Spec, ref buf); break;
                case SegmentKind.RowNumber: n = ValueFormatter.WriteDecimal(ctx.RowNumber, s.Spec, ref buf); break;
                case SegmentKind.Count: n = ValueFormatter.WriteDecimal(ctx.RowCount, s.Spec, ref buf); break;
                case SegmentKind.Today:
                    n = ValueFormatter.WriteDate(DateTime.Today, s.Spec.Raw == null ? FormatSpec.Get("yyyy/MM/dd") : s.Spec, ref buf);
                    break;
                case SegmentKind.Now:
                    n = ValueFormatter.WriteDate(DateTime.Now, s.Spec.Raw == null ? FormatSpec.Get("yyyy/MM/dd HH:mm") : s.Spec, ref buf);
                    break;
                case SegmentKind.ReportName: sb.Append(ctx.ReportName); continue;
                case SegmentKind.Aggregate:
                    var v = ctx.GetAggregate(s.AggIndex, s.Agg, s.Scope);
                    n = v.HasValue ? ValueFormatter.WriteDecimal(v.Value, s.Spec, ref buf) : 0;
                    break;
                case SegmentKind.Field:
                    if (!hasRow) continue;
                    n = s.Getter!.Write(row!, s.Spec, ref buf);
                    break;
                default: // Param
                    var p = ctx.GetParameter(s.Text, out bool found);
                    if (!found && hasRow && s.Getter == null)
                    {
                        // شاید فیلد ردیف باشد که در زمان کامپایل شناخته نشده
                        continue;
                    }
                    n = ValueFormatter.WriteObject(p, s.Spec, ref buf);
                    break;
            }
            sb.Append(buf, 0, n);
        }
        return sb.ToString();
    }
}

internal interface ICompileContext<T>
{
    bool HasParameter(string name);
    FieldGetter<T>? GetField(string name);
    int RegisterAggregate(string field);
}

internal interface IEvalContext<T>
{
    int PageNumber { get; }
    long RowNumber { get; }
    long RowCount { get; }
    string ReportName { get; }
    object? GetParameter(string name, out bool found);
    decimal? GetAggregate(int index, AggregateKind kind, AggregateScope scope);
}

internal static class Parse
{
    public static HAlign? Align(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "left" => HAlign.Left,
        "right" => HAlign.Right,
        "center" or "centre" => HAlign.Center,
        "start" => HAlign.Start,
        "end" => HAlign.End,
        _ => null
    };

    public static VAlign? VAlign(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "top" => FastPdfWriterStream.VAlign.Top,
        "bottom" => FastPdfWriterStream.VAlign.Bottom,
        "middle" or "center" => FastPdfWriterStream.VAlign.Middle,
        _ => null
    };

    public static TextOverflow? Overflow(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "shrink" => TextOverflow.Shrink,
        "wrap" => TextOverflow.Wrap,
        "clip" => TextOverflow.Clip,
        "ellipsis" => TextOverflow.Ellipsis,
        "visible" => TextOverflow.Visible,
        _ => null
    };

    public static TextDirection? Direction(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "ltr" or "lefttoright" => TextDirection.LeftToRight,
        "rtl" or "righttoleft" => TextDirection.RightToLeft,
        "auto" => TextDirection.Auto,
        _ => null
    };

    public static DigitStyle? Digits(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "latin" or "english" or "en" => DigitStyle.Latin,
        "persian" or "farsi" or "fa" => DigitStyle.Persian,
        "arabic" or "ar" => DigitStyle.Arabic,
        _ => null
    };

    public static AggregateKind Aggregate(string? s) =>
        Enum.TryParse<AggregateKind>(s, true, out var k) ? k : AggregateKind.None;

    public static string Invariant(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
