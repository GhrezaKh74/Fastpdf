using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FastPdfWriterStream.Fonts;
using FastPdfWriterStream.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FastPdfWriterStream.Designer;

public static class FastPdfDesignerExtensions
{
    /// <summary>
    /// ثبت سرویس‌های Designer.
    /// <code>
    /// builder.Services.AddFastPdfDesigner(o =>
    /// {
    ///     o.TemplatesDirectory = "Reports/Templates";
    ///     o.FontsDirectory = "wwwroot/Fonts";
    ///     o.AddDataSource("BankStatement", n => SampleRows(n), () => new { BankName = "..." });
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddFastPdfDesigner(this IServiceCollection services, Action<PdfDesignerOptions>? configure = null)
    {
        services.AddOptions<PdfDesignerOptions>();
        if (configure != null) services.Configure(configure);
        services.AddSingleton<DesignerService>();
        return services;
    }

    /// <summary>
    /// نگاشت صفحه و API های Designer. خروجی RouteGroupBuilder است تا بتوانید مجوز اضافه کنید:
    /// <code>app.MapFastPdfDesigner("/pdf-designer").RequireAuthorization("ReportAdmin");</code>
    /// </summary>
    public static RouteGroupBuilder MapFastPdfDesigner(this IEndpointRouteBuilder endpoints, string pattern = "/pdf-designer")
    {
        // پیشوند خالی: وقتی Designer در شاخهٔ app.Map("/pdf-designer", ...) نصب می‌شود
        var trimmed = (pattern ?? "").Trim('/');
        var prefix = trimmed.Length == 0 ? "" : "/" + trimmed;
        var group = endpoints.MapGroup(prefix.Length == 0 ? "/" : prefix);

        // صفحه (با / انتهایی تا مسیرهای نسبی api/... درست کار کنند)
        group.MapGet("", (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.Value;
            if (string.IsNullOrEmpty(path) || !path.EndsWith('/'))
                return Results.Redirect(ctx.Request.PathBase + prefix + "/" + ctx.Request.QueryString);
            return Asset("index.html", "text/html; charset=utf-8");
        });
        group.MapGet("/designer.js", () => Asset("designer.js", "text/javascript; charset=utf-8"));
        group.MapGet("/designer.css", () => Asset("designer.css", "text/css; charset=utf-8"));

        var api = group.MapGroup("/api");
        api.MapGet("/info", (DesignerService s) => Results.Json(new { version = typeof(ReportEngine).Assembly.GetName().Version?.ToString(3), readOnly = s.Options.ReadOnly }));

        api.MapGet("/templates", (DesignerService s) => Results.Json(s.ListTemplates()));

        api.MapGet("/templates/{name}", (string name, DesignerService s) =>
        {
            var path = s.TemplatePath(name);
            if (path == null) return Results.BadRequest("نام قالب نامعتبر است.");
            if (!File.Exists(path)) return Results.NotFound();
            return Results.Text(File.ReadAllText(path), "application/json; charset=utf-8");
        });

        api.MapPut("/templates/{name}", async (string name, HttpRequest req, DesignerService s) =>
        {
            if (s.Options.ReadOnly) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var path = s.TemplatePath(name);
            if (path == null) return Results.BadRequest("نام قالب نامعتبر است.");
            var (template, error) = await s.ReadTemplate(req);
            if (template == null) return Results.BadRequest(error);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, template.ToJson());
            return Results.NoContent();
        });

        api.MapDelete("/templates/{name}", (string name, DesignerService s) =>
        {
            if (s.Options.ReadOnly) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var path = s.TemplatePath(name);
            if (path == null) return Results.BadRequest("نام قالب نامعتبر است.");
            if (File.Exists(path)) File.Delete(path);
            return Results.NoContent();
        });

        api.MapGet("/fonts", (DesignerService s) => Results.Json(s.ListFonts()));

        api.MapGet("/fonts/{file}", (string file, DesignerService s) =>
        {
            var path = s.FontPath(file);
            if (path == null) return Results.NotFound();
            var type = Path.GetExtension(path).ToLowerInvariant() == ".otf" ? "font/otf" : "font/ttf";
            return Results.File(path, type, enableRangeProcessing: false);
        });

        api.MapGet("/datasources", (DesignerService s) => Results.Json(
            s.Options.DataSources.Values.Select(d => new { name = d.Name, fields = d.Fields, parameters = d.Parameters }),
            ReportTemplate.JsonOptions));

        api.MapPost("/preview", async (HttpRequest req, DesignerService s, string? dataSource, int? rows) =>
        {
            var (template, error) = await s.ReadTemplate(req);
            if (template == null) return Results.BadRequest(error);
            try
            {
                var bytes = s.Preview(template, dataSource, rows ?? 100);
                return Results.File(bytes, "application/pdf");
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or InvalidDataException or ArgumentException or NotSupportedException or FormatException)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        return group;
    }

    private static IResult Asset(string name, string contentType)
    {
        var bytes = DesignerAssets.Get(name);
        return bytes == null ? Results.NotFound() : Results.Bytes(bytes, contentType);
    }
}

internal static class DesignerAssets
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new();

    public static byte[]? Get(string name) => Cache.GetOrAdd(name, n =>
    {
        using var s = typeof(DesignerAssets).Assembly.GetManifestResourceStream("FastPdfDesigner." + n);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });
}

/// <summary>منطق Designer (فایل‌ها، فونت‌ها، پیش‌نمایش).</summary>
public sealed class DesignerService
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, string Family)> _fontFamilies = new();
    private readonly ReportEngine _engine;

    public PdfDesignerOptions Options { get; }

    public DesignerService(IOptions<PdfDesignerOptions> options)
    {
        Options = options.Value;
        _engine = new ReportEngine(new ReportEngineOptions
        {
            FontsDirectory = Path.GetFullPath(Options.FontsDirectory),
            ImagesDirectory = Options.ImagesDirectory == null ? null : Path.GetFullPath(Options.ImagesDirectory),
            TemplatesDirectory = Path.GetFullPath(Options.TemplatesDirectory),
            ParallelCompression = false
        });
    }

    /// <summary>مسیر امن فایل قالب (جلوگیری از path traversal).</summary>
    public string? TemplatePath(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        if (name.Length == 0 || name.Length > 120 || name.StartsWith('.') || name.Contains("..") ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            return null;
        var dir = Path.GetFullPath(Options.TemplatesDirectory);
        var full = Path.GetFullPath(Path.Combine(dir, name + ".json"));
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public IEnumerable<string> ListTemplates()
    {
        var dir = Path.GetFullPath(Options.TemplatesDirectory);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .OrderBy(n => n, StringComparer.CurrentCulture).ToList();
    }

    public string? FontPath(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains("..") || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
        var dir = Path.GetFullPath(Options.FontsDirectory);
        var full = Path.GetFullPath(Path.Combine(dir, file));
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    public IEnumerable<object> ListFonts()
    {
        var dir = Path.GetFullPath(Options.FontsDirectory);
        if (!Directory.Exists(dir)) return Array.Empty<object>();
        var list = new List<object>();
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".ttf" && ext != ".otf") continue;
            var stamp = File.GetLastWriteTimeUtc(path);
            string family;
            if (_fontFamilies.TryGetValue(path, out var c) && c.Stamp == stamp) family = c.Family;
            else
            {
                try { family = TrueTypeFont.FromFile(path).FamilyName; }
                catch { continue; }
                _fontFamilies[path] = (stamp, family);
            }
            list.Add(new { file = Path.GetFileName(path), family });
        }
        return list;
    }

    internal async Task<(ReportTemplate? Template, string? Error)> ReadTemplate(HttpRequest req)
    {
        if (req.ContentLength > Options.MaxTemplateBytes) return (null, "قالب بیش از حد بزرگ است.");
        try
        {
            var t = await JsonSerializer.DeserializeAsync<ReportTemplate>(req.Body, ReportTemplate.JsonOptions);
            return t == null ? (null, "قالب خالی است.") : (t, null);
        }
        catch (JsonException ex)
        {
            return (null, "JSON نامعتبر: " + ex.Message);
        }
    }

    /// <summary>تولید PDF پیش‌نمایش با منبع داده ثبت‌شده یا داده‌ی نمونهٔ خود قالب.</summary>
    public byte[] Preview(ReportTemplate template, string? dataSource, int rows)
    {
        rows = Math.Clamp(rows, 0, Options.MaxPreviewRows);
        using var ms = new MemoryStream();
        if (!string.IsNullOrEmpty(dataSource) && Options.DataSources.TryGetValue(dataSource, out var ds))
        {
            ds.Render(_engine, template, rows, ms);
        }
        else
        {
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in template.Parameters)
                if (!string.IsNullOrEmpty(p.Name)) parameters[p.Name] = SampleValue(p, 0);
            _engine.Render(template, SampleRows(template, rows), ms, parameters);
        }
        return ms.ToArray();
    }

    /// <summary>ردیف‌های نمونه از روی تعریف فیلدهای قالب.</summary>
    private static IEnumerable<Dictionary<string, object?>> SampleRows(ReportTemplate t, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in t.Fields)
                if (!string.IsNullOrEmpty(f.Name)) row[f.Name] = SampleValue(f, i);
            // ستون‌هایی که فیلدشان تعریف نشده: نام فیلد به عنوان مقدار
            if (t.Table != null)
                foreach (var c in t.Table.Columns)
                    if (!string.IsNullOrEmpty(c.Field) && c.Field.IndexOf('{') < 0 && !row.ContainsKey(c.Field) &&
                        !c.Field.Equals("RowNumber", StringComparison.OrdinalIgnoreCase))
                        row[c.Field] = c.Field;
            yield return row;
        }
    }

    private static object? SampleValue(FieldDefinition f, int i)
    {
        var s = f.Sample;
        switch (f.Type?.ToLowerInvariant())
        {
            case "number":
                if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    return i == 0 ? d : d + (i * 7919 % 1000) * (d == 0 ? 1 : Math.Max(1, Math.Abs(d) / 1000));
                return (decimal)(i + 1) * 1000;
            case "date":
                return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.AddDays(i) : DateTime.Today.AddDays(i);
            case "bool":
                return bool.TryParse(s, out var b) ? b : i % 2 == 0;
            case "image":
                return string.IsNullOrEmpty(s) ? null : s;
            default:
                return s ?? f.Name;
        }
    }
}
