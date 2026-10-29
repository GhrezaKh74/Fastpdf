using System.Globalization;
using System.Runtime.CompilerServices;

namespace FastPdfWriterStream.Templates;

/// <summary>
/// مشخصات فرمت پارس‌شده. فرمت‌های پشتیبانی‌شده:
/// <list type="bullet">
/// <item>رشته‌های فرمت .NET: <c>N0</c>، <c>N2</c>، <c>#,0.##</c>، <c>yyyy/MM/dd</c> و ...</item>
/// <item><c>persian</c> یا <c>persian:yyyy/MM/dd HH:mm</c> — تاریخ شمسی</item>
/// <item><c>mask:####/##/##</c> — قالب‌بندی رشته (هر # یک کاراکتر از مقدار)</item>
/// <item><c>abs:N0</c> — قدر مطلق عدد و سپس فرمت</item>
/// <item><c>div:100:#,0.00</c> — تقسیم عدد بر ۱۰۰ و سپس فرمت (برای ارزها با واحد اعشاری)</item>
/// <item><c>bool:بله|خیر</c> — نمایش مقدار بولی</item>
/// <item><c>money</c> — معادل N0</item>
/// </list>
/// </summary>
public sealed class FormatSpec
{
    public static readonly FormatSpec Default = new(null);

    public string? Raw { get; }
    internal readonly string? NetFormat;
    internal readonly bool Persian;
    internal readonly string PersianPattern = "yyyy/MM/dd";
    internal readonly string? Mask;
    internal readonly bool Abs;
    internal readonly decimal Divisor = 1;
    internal readonly string? TrueText, FalseText;

    public FormatSpec(string? raw)
    {
        Raw = raw;
        if (string.IsNullOrWhiteSpace(raw)) return;
        var f = raw.Trim();
        while (true)
        {
            if (f.StartsWith("abs:", StringComparison.OrdinalIgnoreCase)) { Abs = true; f = f[4..]; continue; }
            if (f.Equals("abs", StringComparison.OrdinalIgnoreCase)) { Abs = true; f = ""; }
            if (f.StartsWith("div:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = f.IndexOf(':', 4);
                var num = colon < 0 ? f[4..] : f[4..colon];
                if (decimal.TryParse(num, NumberStyles.Number, CultureInfo.InvariantCulture, out var div) && div != 0) Divisor = div;
                f = colon < 0 ? "" : f[(colon + 1)..];
                continue;
            }
            break;
        }
        if (f.StartsWith("mask:", StringComparison.OrdinalIgnoreCase)) { Mask = f[5..]; return; }
        if (f.StartsWith("bool:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = f[5..].Split('|');
            TrueText = parts[0];
            FalseText = parts.Length > 1 ? parts[1] : "";
            return;
        }
        if (f.Equals("persian", StringComparison.OrdinalIgnoreCase) || f.Equals("fa", StringComparison.OrdinalIgnoreCase) || f.Equals("shamsi", StringComparison.OrdinalIgnoreCase))
        {
            Persian = true;
            return;
        }
        if (f.StartsWith("persian:", StringComparison.OrdinalIgnoreCase) || f.StartsWith("fa:", StringComparison.OrdinalIgnoreCase))
        {
            Persian = true;
            PersianPattern = f[(f.IndexOf(':') + 1)..];
            return;
        }
        if (f.Equals("money", StringComparison.OrdinalIgnoreCase)) f = "N0";
        NetFormat = f.Length == 0 ? null : f;
    }

    private static readonly Dictionary<string, FormatSpec> Cache = new();

    public static FormatSpec Get(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Default;
        lock (Cache)
        {
            if (!Cache.TryGetValue(raw, out var spec)) Cache[raw] = spec = new FormatSpec(raw);
            return spec;
        }
    }
}

/// <summary>
/// تبدیل مقدار به متن در بافر char بدون تخصیص حافظه (برای مسیر داغ ردیف‌های جدول).
/// </summary>
public static class ValueFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly PersianCalendar Pc = new();

    /// <summary>نوشتن مقدار generic بدون boxing برای انواع رایج.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write<TValue>(TValue value, FormatSpec spec, ref char[] buf)
    {
        if (typeof(TValue) == typeof(string)) return WriteString(Unsafe.As<TValue, string>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(decimal)) return WriteDecimal(Unsafe.As<TValue, decimal>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(long)) return WriteDecimal(Unsafe.As<TValue, long>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(int)) return WriteDecimal(Unsafe.As<TValue, int>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(double)) return WriteDouble(Unsafe.As<TValue, double>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(DateTime)) return WriteDate(Unsafe.As<TValue, DateTime>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(decimal?)) { var v = Unsafe.As<TValue, decimal?>(ref value); return v.HasValue ? WriteDecimal(v.Value, spec, ref buf) : 0; }
        if (typeof(TValue) == typeof(long?)) { var v = Unsafe.As<TValue, long?>(ref value); return v.HasValue ? WriteDecimal(v.Value, spec, ref buf) : 0; }
        if (typeof(TValue) == typeof(int?)) { var v = Unsafe.As<TValue, int?>(ref value); return v.HasValue ? WriteDecimal(v.Value, spec, ref buf) : 0; }
        if (typeof(TValue) == typeof(double?)) { var v = Unsafe.As<TValue, double?>(ref value); return v.HasValue ? WriteDouble(v.Value, spec, ref buf) : 0; }
        if (typeof(TValue) == typeof(DateTime?)) { var v = Unsafe.As<TValue, DateTime?>(ref value); return v.HasValue ? WriteDate(v.Value, spec, ref buf) : 0; }
        if (typeof(TValue) == typeof(short)) return WriteDecimal(Unsafe.As<TValue, short>(ref value), spec, ref buf);
        if (typeof(TValue) == typeof(short?)) { var v = Unsafe.As<TValue, short?>(ref value); return v.HasValue ? WriteDecimal(v.Value, spec, ref buf) : 0; }
        if (typeof(TValue) == typeof(bool)) return WriteBool(Unsafe.As<TValue, bool>(ref value), spec, ref buf);
        return WriteObject(value, spec, ref buf);
    }

    public static int WriteObject(object? value, FormatSpec spec, ref char[] buf)
    {
        switch (value)
        {
            case null: return 0;
            case string s: return WriteString(s, spec, ref buf);
            case decimal d: return WriteDecimal(d, spec, ref buf);
            case int i: return WriteDecimal(i, spec, ref buf);
            case long l: return WriteDecimal(l, spec, ref buf);
            case short sh: return WriteDecimal(sh, spec, ref buf);
            case byte b: return WriteDecimal(b, spec, ref buf);
            case uint ui: return WriteDecimal(ui, spec, ref buf);
            case ulong ul: return WriteDecimal(ul, spec, ref buf);
            case double db: return WriteDouble(db, spec, ref buf);
            case float fl: return WriteDouble(fl, spec, ref buf);
            case DateTime dt: return WriteDate(dt, spec, ref buf);
            case DateTimeOffset dto: return WriteDate(dto.LocalDateTime, spec, ref buf);
            case DateOnly d0: return WriteDate(d0.ToDateTime(TimeOnly.MinValue), spec, ref buf);
            case TimeOnly t0: return WriteString(spec.NetFormat != null ? t0.ToString(spec.NetFormat, Inv) : t0.ToString("HH:mm:ss", Inv), FormatSpec.Default, ref buf);
            case TimeSpan ts: return WriteString(spec.NetFormat != null ? ts.ToString(spec.NetFormat, Inv) : ts.ToString("c", Inv), FormatSpec.Default, ref buf);
            case bool bo: return WriteBool(bo, spec, ref buf);
            case System.Text.Json.JsonElement je: return WriteJson(je, spec, ref buf);
            case IFormattable f when spec.NetFormat != null: return WriteString(f.ToString(spec.NetFormat, Inv), FormatSpec.Default, ref buf);
            default: return WriteString(Convert.ToString(value, Inv), spec, ref buf);
        }
    }

    private static int WriteJson(System.Text.Json.JsonElement je, FormatSpec spec, ref char[] buf)
    {
        switch (je.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Number:
                return je.TryGetDecimal(out var d) ? WriteDecimal(d, spec, ref buf) : WriteDouble(je.GetDouble(), spec, ref buf);
            case System.Text.Json.JsonValueKind.True: return WriteBool(true, spec, ref buf);
            case System.Text.Json.JsonValueKind.False: return WriteBool(false, spec, ref buf);
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined: return 0;
            case System.Text.Json.JsonValueKind.String:
                var s = je.GetString();
                if ((spec.Persian || spec.NetFormat != null) && je.TryGetDateTime(out var dt)) return WriteDate(dt, spec, ref buf);
                if (spec.NetFormat != null && decimal.TryParse(s, NumberStyles.Number, Inv, out var num)) return WriteDecimal(num, spec, ref buf);
                return WriteString(s, spec, ref buf);
            default: return WriteString(je.GetRawText(), spec, ref buf);
        }
    }

    public static string Format(object? value, FormatSpec spec)
    {
        var buf = new char[64];
        int n = WriteObject(value, spec, ref buf);
        return new string(buf, 0, n);
    }

    private static void Ensure(ref char[] buf, int n)
    {
        if (buf.Length < n) buf = new char[Math.Max(n, buf.Length * 2)];
    }

    public static int WriteString(string? s, FormatSpec spec, ref char[] buf)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        if (spec.Mask != null)
        {
            Ensure(ref buf, spec.Mask.Length + s.Length);
            int si = 0, o = 0;
            foreach (var m in spec.Mask)
            {
                if (m == '#')
                {
                    if (si >= s.Length) break;
                    buf[o++] = s[si++];
                }
                else
                {
                    if (si >= s.Length) break;
                    buf[o++] = m;
                }
            }
            while (si < s.Length) buf[o++] = s[si++];
            return o;
        }
        if (spec.NetFormat != null && decimal.TryParse(s, NumberStyles.Number, Inv, out var d))
            return WriteDecimal(d, spec, ref buf);
        Ensure(ref buf, s.Length);
        s.CopyTo(0, buf, 0, s.Length);
        return s.Length;
    }

    public static int WriteDecimal(decimal d, FormatSpec spec, ref char[] buf)
    {
        if (spec.Abs) d = Math.Abs(d);
        if (spec.Divisor != 1) d /= spec.Divisor;
        if (spec.Mask != null) return WriteString(d.ToString(Inv), spec, ref buf);
        Ensure(ref buf, 48);
        while (true)
        {
            if (d.TryFormat(buf, out int n, spec.NetFormat, Inv)) return n;
            buf = new char[buf.Length * 2];
        }
    }

    public static int WriteDouble(double d, FormatSpec spec, ref char[] buf)
    {
        if (spec.Abs) d = Math.Abs(d);
        if (spec.Divisor != 1) d /= (double)spec.Divisor;
        if (spec.Mask != null) return WriteString(d.ToString(Inv), spec, ref buf);
        Ensure(ref buf, 48);
        while (true)
        {
            if (d.TryFormat(buf, out int n, spec.NetFormat, Inv)) return n;
            buf = new char[buf.Length * 2];
        }
    }

    public static int WriteBool(bool b, FormatSpec spec, ref char[] buf)
    {
        string s = spec.TrueText != null ? (b ? spec.TrueText : spec.FalseText ?? "") : (b ? "True" : "False");
        return WriteString(s, FormatSpec.Default, ref buf);
    }

    public static int WriteDate(DateTime dt, FormatSpec spec, ref char[] buf)
    {
        if (spec.Persian) return WriteString(PersianDate(dt, spec.PersianPattern), FormatSpec.Default, ref buf);
        Ensure(ref buf, 64);
        while (true)
        {
            if (dt.TryFormat(buf, out int n, spec.NetFormat ?? "yyyy/MM/dd HH:mm", Inv)) return n;
            buf = new char[buf.Length * 2];
        }
    }

    /// <summary>تبدیل تاریخ میلادی به شمسی با الگوی yyyy MM dd HH mm ss (و M، d، yy).</summary>
    public static string PersianDate(DateTime dt, string pattern = "yyyy/MM/dd")
    {
        if (dt < Pc.MinSupportedDateTime) return "";
        int y = Pc.GetYear(dt), m = Pc.GetMonth(dt), d = Pc.GetDayOfMonth(dt);
        var sb = new System.Text.StringBuilder(pattern.Length + 4);
        for (int i = 0; i < pattern.Length;)
        {
            char c = pattern[i];
            int run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;
            switch (c)
            {
                case 'y': sb.Append(run <= 2 ? (y % 100).ToString("00", Inv) : y.ToString(Inv)); break;
                case 'M': sb.Append(run >= 2 ? m.ToString("00", Inv) : m.ToString(Inv)); break;
                case 'd': sb.Append(run >= 2 ? d.ToString("00", Inv) : d.ToString(Inv)); break;
                case 'H': sb.Append(run >= 2 ? dt.Hour.ToString("00", Inv) : dt.Hour.ToString(Inv)); break;
                case 'm': sb.Append(run >= 2 ? dt.Minute.ToString("00", Inv) : dt.Minute.ToString(Inv)); break;
                case 's': sb.Append(run >= 2 ? dt.Second.ToString("00", Inv) : dt.Second.ToString(Inv)); break;
                default: sb.Append(c, run); break;
            }
            i += run;
        }
        return sb.ToString();
    }

    /// <summary>تبدیل مقدار به عدد (برای جمع‌ها). رشته‌های عددی هم پذیرفته می‌شوند.</summary>
    public static decimal? ToNumber(object? value)
    {
        switch (value)
        {
            case null: return null;
            case decimal d: return d;
            case int i: return i;
            case long l: return l;
            case short s: return s;
            case byte b: return b;
            case double db: return double.IsFinite(db) ? (decimal)db : null;
            case float f: return float.IsFinite(f) ? (decimal)f : null;
            case uint ui: return ui;
            case ulong ul: return ul;
            case bool bo: return bo ? 1 : 0;
            case string str: return decimal.TryParse(str, NumberStyles.Number, Inv, out var r) ? r : null;
            case System.Text.Json.JsonElement je:
                if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetDecimal(out var jd)) return jd;
                if (je.ValueKind == System.Text.Json.JsonValueKind.String) return ToNumber(je.GetString());
                return null;
            default:
                try { return Convert.ToDecimal(value, Inv); } catch { return null; }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal? ToNumber<TValue>(TValue value)
    {
        if (typeof(TValue) == typeof(decimal)) return Unsafe.As<TValue, decimal>(ref value);
        if (typeof(TValue) == typeof(long)) return Unsafe.As<TValue, long>(ref value);
        if (typeof(TValue) == typeof(int)) return Unsafe.As<TValue, int>(ref value);
        if (typeof(TValue) == typeof(decimal?)) return Unsafe.As<TValue, decimal?>(ref value);
        if (typeof(TValue) == typeof(long?)) return Unsafe.As<TValue, long?>(ref value);
        if (typeof(TValue) == typeof(int?)) return Unsafe.As<TValue, int?>(ref value);
        return ToNumber((object?)value);
    }
}
