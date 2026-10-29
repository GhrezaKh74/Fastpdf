using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using FastPdfWriterStream.Templates;

namespace FastPdfWriterStream.Designer;

/// <summary>تنظیمات Designer.</summary>
public sealed class PdfDesignerOptions
{
    /// <summary>پوشهٔ ذخیرهٔ قالب‌ها (*.json).</summary>
    public string TemplatesDirectory { get; set; } = "Templates";

    /// <summary>پوشهٔ فونت‌ها (*.ttf).</summary>
    public string FontsDirectory { get; set; } = "Fonts";

    /// <summary>پوشهٔ تصاویر (اختیاری).</summary>
    public string? ImagesDirectory { get; set; }

    /// <summary>فقط مشاهده/پیش‌نمایش (ذخیره و حذف غیرفعال).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>حداکثر تعداد ردیف در پیش‌نمایش.</summary>
    public int MaxPreviewRows { get; set; } = 20_000;

    /// <summary>حداکثر حجم قالب ارسالی (بایت).</summary>
    public long MaxTemplateBytes { get; set; } = 10 * 1024 * 1024;

    internal readonly Dictionary<string, DesignerDataSource> DataSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ثبت منبع دادهٔ نمونه برای پیش‌نمایش. فیلدها از روی propertyهای <typeparamref name="T"/> (و عنوان از
    /// [DisplayName] یا [Display(Name)]) استخراج می‌شوند.
    /// </summary>
    /// <param name="name">نام منبع داده</param>
    /// <param name="rows">تابع تولید n ردیف نمونه</param>
    /// <param name="parameters">پارامترهای نمونه (anonymous object یا Dictionary)</param>
    public PdfDesignerOptions AddDataSource<T>(string name, Func<int, IEnumerable<T>> rows, Func<object?>? parameters = null)
    {
        var p = parameters?.Invoke();
        var paramInfos = new List<FieldDefinition>();
        if (p != null)
        {
            foreach (var kv in ReadParameters(p))
                paramInfos.Add(new FieldDefinition
                {
                    Name = kv.Key,
                    Type = TypeName(kv.Value?.GetType()),
                    Sample = kv.Value is byte[] ? null : ValueFormatter.Format(kv.Value, FormatSpec.Default)
                });
        }

        T? first = default;
        bool hasFirst = false;
        foreach (var r in rows(1)) { first = r; hasFirst = true; break; }

        var fields = new List<FieldDefinition>();
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            var title = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName
                        ?? prop.GetCustomAttribute<DisplayAttribute>()?.GetName();
            string? sample = null;
            if (hasFirst)
            {
                try { sample = ValueFormatter.Format(prop.GetValue(first), FormatSpec.Default); } catch { sample = null; }
            }
            fields.Add(new FieldDefinition { Name = prop.Name, Title = title, Type = TypeName(prop.PropertyType), Sample = sample });
        }

        DataSources[name] = new DesignerDataSource(name, fields, paramInfos,
            (engine, template, count, output) => engine.Render(template, rows(count), output, parameters?.Invoke()));
        return this;
    }

    private static Dictionary<string, object?> ReadParameters(object p)
    {
        if (p is IDictionary<string, object?> d) return new Dictionary<string, object?>(d);
        var result = new Dictionary<string, object?>();
        foreach (var prop in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (prop.GetIndexParameters().Length == 0) result[prop.Name] = prop.GetValue(p);
        return result;
    }

    internal static string TypeName(Type? t)
    {
        if (t == null) return "string";
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(byte[])) return "image";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return "date";
        if (t.IsPrimitive || t == typeof(decimal)) return "number";
        return "string";
    }
}

/// <summary>منبع دادهٔ ثبت‌شده برای پیش‌نمایش.</summary>
public sealed record DesignerDataSource(
    string Name,
    List<FieldDefinition> Fields,
    List<FieldDefinition> Parameters,
    Func<ReportEngine, ReportTemplate, int, Stream, RenderResult> Render);
