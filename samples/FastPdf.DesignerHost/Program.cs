using Microsoft.AspNetCore.WebUtilities;
using FastPdf.Samples;
using FastPdfWriterStream.Designer;
using FastPdfWriterStream.Templates;

// اجرای Designer:  dotnet run --project samples/FastPdf.DesignerHost  →  http://localhost:5080/pdf-designer/
var builder = WebApplication.CreateBuilder(args);
var root = FindRoot(builder.Environment.ContentRootPath);

builder.Services.AddFastPdfDesigner(o =>
{
    o.TemplatesDirectory = Path.Combine(root, "templates");
    o.FontsDirectory = Path.Combine(root, "fonts");
    // منبع دادهٔ نمونه برای پیش‌نمایش (در پروژهٔ اصلی: داده‌ی واقعی یا نمونه از دیتابیس)
    o.AddDataSource("BankStatement", n => SampleData.Rows(n), () => SampleData.Parameters());
});

// موتور گزارش برای تولید PDF واقعی در API
builder.Services.AddSingleton(new ReportEngine(new ReportEngineOptions
{
    FontsDirectory = Path.Combine(root, "fonts"),
    TemplatesDirectory = Path.Combine(root, "templates")
}));

var app = builder.Build();

// در محیط واقعی: .RequireAuthorization("ReportDesigner")
app.MapFastPdfDesigner("/pdf-designer");

// نمونهٔ endpoint تولید گزارش: PDF مستقیما روی Response نوشته می‌شود (بدون بافر کردن کل فایل)
app.MapGet("/reports/bank-statement", async (HttpContext ctx, ReportEngine engine, int? rows) =>
{
    var template = engine.LoadTemplate("bank-statement");
    ctx.Response.ContentType = "application/pdf";
    ctx.Response.Headers.ContentDisposition = "inline; filename=bank-statement.pdf";
    // نوشتن همگام روی Response نیاز به بافر دارد؛ با FileBufferingWriteStream یا فایل موقت هم می‌شود
    await using var buffer = new FileBufferingWriteStream();
    engine.Render(template, SampleData.Rows(rows ?? 1000), buffer, SampleData.Parameters());
    await buffer.DrainBufferAsync(ctx.Response.Body);
});

app.MapGet("/", () => Results.Redirect("/pdf-designer/"));
app.Run();

static string FindRoot(string start)
{
    var dir = start;
    while (dir != null && !Directory.Exists(Path.Combine(dir, "templates"))) dir = Path.GetDirectoryName(dir);
    return dir ?? start;
}
