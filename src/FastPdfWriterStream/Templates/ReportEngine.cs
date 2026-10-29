using System.Collections.Concurrent;
using System.Data;
using System.IO.Compression;
using FastPdfWriterStream.Fonts;

namespace FastPdfWriterStream.Templates;

/// <summary>تنظیمات موتور گزارش.</summary>
public sealed class ReportEngineOptions
{
    /// <summary>پوشهٔ فونت‌ها (مسیرهای نسبی فونت در قالب نسبت به این پوشه هستند).</summary>
    public string? FontsDirectory { get; set; }

    /// <summary>پوشهٔ قالب‌ها (برای <see cref="ReportEngine.LoadTemplate"/>).</summary>
    public string? TemplatesDirectory { get; set; }

    /// <summary>پوشهٔ تصاویر (لوگو و ...).</summary>
    public string? ImagesDirectory { get; set; }

    /// <summary>فونت پیش‌فرض اگر قالب فونتی تعریف نکرده باشد.</summary>
    public string? DefaultFontPath { get; set; }
    public string? DefaultBoldFontPath { get; set; }

    public bool Compress { get; set; } = true;
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;
    public bool ParallelCompression { get; set; } = true;
}

/// <summary>
/// موتور تولید گزارش PDF از روی قالب JSON. فونت‌ها و قالب‌ها یک بار بارگذاری و کش می‌شوند؛
/// یک نمونه را به صورت Singleton ثبت کنید. متدها thread-safe هستند (هر Render مستقل است).
/// </summary>
public sealed class ReportEngine
{
    private readonly ConcurrentDictionary<string, TrueTypeFont> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Stamp, ReportTemplate Template)> _templates = new(StringComparer.OrdinalIgnoreCase);

    public ReportEngineOptions Options { get; }

    public ReportEngine(ReportEngineOptions? options = null)
    {
        Options = options ?? new ReportEngineOptions();
    }

    // ── منابع ──────────────────────────────────────────────────────

    /// <summary>فونت (پارس‌شده و کش‌شده).</summary>
    public TrueTypeFont GetFont(string path)
    {
        var full = ResolvePath(path, Options.FontsDirectory)
                   ?? throw new FileNotFoundException($"Font file not found: '{path}' (FontsDirectory: '{Options.FontsDirectory}').", path);
        return _fonts.GetOrAdd(full, p => TrueTypeFont.FromFile(p));
    }

    /// <summary>ثبت فونت از حافظه (مثلا embedded resource) با یک نام مسیر مجازی.</summary>
    public void RegisterFont(string path, byte[] data) => _fonts[path] = new TrueTypeFont(data);

    internal string? ResolveImagePath(string path) => ResolvePath(path, Options.ImagesDirectory);

    private string? ResolvePath(string path, string? baseDir)
    {
        if (_fonts.ContainsKey(path)) return path;
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        if (baseDir != null)
        {
            var p = Path.GetFullPath(Path.Combine(baseDir, path));
            if (File.Exists(p)) return p;
            var byName = Path.Combine(baseDir, Path.GetFileName(path));
            if (File.Exists(byName)) return Path.GetFullPath(byName);
        }
        var local = Path.GetFullPath(path);
        return File.Exists(local) ? local : null;
    }

    /// <summary>بارگذاری قالب با نام (از TemplatesDirectory) یا مسیر؛ با تغییر فایل خودکار بازخوانی می‌شود.</summary>
    public ReportTemplate LoadTemplate(string nameOrPath)
    {
        string path = nameOrPath;
        if (!File.Exists(path) && Options.TemplatesDirectory != null)
        {
            path = Path.Combine(Options.TemplatesDirectory, nameOrPath);
            if (!File.Exists(path)) path += ".json";
        }
        if (!File.Exists(path)) throw new FileNotFoundException($"Template not found: '{nameOrPath}'.", nameOrPath);
        var stamp = File.GetLastWriteTimeUtc(path);
        var entry = _templates.AddOrUpdate(Path.GetFullPath(path),
            p => (stamp, ReportTemplate.Load(p)),
            (p, old) => old.Stamp == stamp ? old : (stamp, ReportTemplate.Load(p)));
        return entry.Template;
    }

    // ── تولید گزارش ─────────────────────────────────────────────────

    /// <summary>شروع تولید جریانی: ردیف‌ها را با AddRow اضافه و در پایان Complete را صدا بزنید.</summary>
    /// <param name="signature">امضای دیجیتال (اختیاری)؛ خروجی باید قابل Seek و Read باشد.</param>
    public ReportRenderer<T> Begin<T>(ReportTemplate template, Stream output, object? parameters = null,
        long? expectedRows = null, bool leaveOpen = true, CancellationToken cancellationToken = default,
        PdfSignatureOptions? signature = null) =>
        new(this, template, output, parameters, expectedRows, cancellationToken, leaveOpen, signature);

    public RenderResult Render<T>(ReportTemplate template, IEnumerable<T> rows, Stream output, object? parameters = null,
        CancellationToken cancellationToken = default, PdfSignatureOptions? signature = null)
    {
        template = HideEmptyColumns(template, rows);
        using var r = Begin<T>(template, output, parameters, CountOf(rows), true, cancellationToken, signature);
        foreach (var row in rows) r.AddRow(row);
        return r.Complete();
    }

    public RenderResult Render<T>(ReportTemplate template, IEnumerable<T> rows, string outputPath, object? parameters = null,
        CancellationToken cancellationToken = default, PdfSignatureOptions? signature = null)
    {
        using var fs = OpenOutput(outputPath, signature);
        return Render(template, rows, fs, parameters, cancellationToken, signature);
    }

    private static FileStream OpenOutput(string path, PdfSignatureOptions? signature) =>
        new(path, FileMode.Create, signature != null ? FileAccess.ReadWrite : FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);

    public byte[] RenderToBytes<T>(ReportTemplate template, IEnumerable<T> rows, object? parameters = null)
    {
        using var ms = new MemoryStream();
        Render(template, rows, ms, parameters);
        return ms.ToArray();
    }

    /// <summary>تولید گزارش از IAsyncEnumerable (مثلا EF Core AsAsyncEnumerable) بدون بارگذاری کل داده در حافظه.</summary>
    public async Task<RenderResult> RenderAsync<T>(ReportTemplate template, IAsyncEnumerable<T> rows, Stream output,
        object? parameters = null, CancellationToken cancellationToken = default)
    {
        using var r = Begin<T>(template, output, parameters, null, true, cancellationToken);
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false)) r.AddRow(row);
        return r.Complete();
    }

    /// <summary>تولید گزارش از DataTable.</summary>
    public RenderResult Render(ReportTemplate template, DataTable table, Stream output, object? parameters = null) =>
        Render(template, table.Rows.Cast<DataRow>().ToList(), output, parameters);

    /// <summary>تولید گزارش مستقیم از IDataReader (جریانی، بدون DataTable).</summary>
    public RenderResult Render(ReportTemplate template, IDataReader reader, Stream output, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        using var r = Begin<IDataRecord>(template, output, parameters, null, true, cancellationToken);
        while (reader.Read()) r.AddRow(reader);
        return r.Complete();
    }

    /// <summary>گزارش بدون جدول داده (فرم، نامه، فاکتور تک‌صفحه‌ای).</summary>
    public RenderResult RenderForm(ReportTemplate template, Stream output, object? parameters = null, PdfSignatureOptions? signature = null)
    {
        using var r = Begin<NoRow>(template, output, parameters, 0, true, default, signature);
        return r.Complete();
    }

    public RenderResult RenderForm(ReportTemplate template, string outputPath, object? parameters = null, PdfSignatureOptions? signature = null)
    {
        using var fs = OpenOutput(outputPath, signature);
        return RenderForm(template, fs, parameters, signature);
    }

    /// <summary>
    /// اگر <see cref="TableDefinition.HideEmptyColumns"/> فعال باشد و داده در حافظه باشد، ستون‌های کاملا خالی را
    /// در یک کپی از قالب مخفی می‌کند (قالب کش‌شده تغییر نمی‌کند).
    /// </summary>
    public static ReportTemplate HideEmptyColumns<T>(ReportTemplate template, IEnumerable<T> rows)
    {
        var table = template.Table;
        if (table is not { HideEmptyColumns: true } || CountOf(rows) is null) return template;

        var candidates = new List<(int Index, FieldGetter<T>[] Getters)>();
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var c = table.Columns[i];
            if (!c.Visible || c.SpanLines || (c.Aggregate != null && !c.Aggregate.Equals("None", StringComparison.OrdinalIgnoreCase))) continue;
            var fields = c.Details is { Count: > 0 }
                ? c.Details.Select(d => d.Field).ToList()
                : new List<string> { c.Field?.Trim() ?? "" };
            if (fields.Any(f => f.Length == 0 || f.Contains('{') || f.Equals("RowNumber", StringComparison.OrdinalIgnoreCase) || f == "#"))
                continue;
            var getters = fields.Select(f => FieldAccess.Create<T>(f)).ToArray();
            if (getters.Any(g => g == null)) continue;
            candidates.Add((i, getters.Select(g => g!).ToArray()));
        }
        if (candidates.Count == 0) return template;

        var filled = new bool[candidates.Count];
        int remaining = candidates.Count;
        var buf = new char[64];
        foreach (var row in rows)
        {
            for (int k = 0; k < candidates.Count; k++)
            {
                if (filled[k]) continue;
                foreach (var g in candidates[k].Getters)
                {
                    int n = g.Write(row, FormatSpec.Default, ref buf);
                    if (n > 0 && !buf.AsSpan(0, n).IsWhiteSpace()) { filled[k] = true; remaining--; break; }
                }
            }
            if (remaining == 0) return template;
        }

        var copy = template.Clone();
        for (int k = 0; k < candidates.Count; k++)
            if (!filled[k]) copy.Table!.Columns[candidates[k].Index].Visible = false;
        return copy;
    }

    private static long? CountOf<T>(IEnumerable<T> rows) => rows switch
    {
        ICollection<T> c => c.Count,
        IReadOnlyCollection<T> rc => rc.Count,
        System.Collections.ICollection nc => nc.Count,
        _ => null
    };
}
