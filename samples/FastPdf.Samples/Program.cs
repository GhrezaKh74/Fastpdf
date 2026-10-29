using System.Diagnostics;
using FastPdf.Samples;
using FastPdfWriterStream.Templates;

// استفاده: dotnet run -c Release -- [rows=100000] [output=out/bank-statement.pdf]
int rows = args.Length > 0 ? int.Parse(args[0]) : 100_000;
string output = args.Length > 1 ? args[1] : Path.Combine("out", $"bank-statement-{rows}.pdf");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

var root = FindRoot();
var engine = new ReportEngine(new ReportEngineOptions
{
    FontsDirectory = Path.Combine(root, "fonts"),
    TemplatesDirectory = Path.Combine(root, "templates")
});
var template = engine.LoadTemplate("bank-statement");

// گرم کردن JIT (در سرور فقط یک بار اتفاق می‌افتد)
engine.RenderToBytes(template, SampleData.Rows(500), SampleData.Parameters());

GC.Collect();
long allocBefore = GC.GetTotalAllocatedBytes(true);
var sw = Stopwatch.StartNew();
var result = engine.Render(template, SampleData.Rows(rows), output, SampleData.Parameters());
sw.Stop();
long alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;

var size = new FileInfo(output).Length;
Console.WriteLine($"Rows:        {result.RowCount:N0}");
Console.WriteLine($"Pages:       {result.PageCount:N0}");
Console.WriteLine($"Time:        {sw.Elapsed.TotalSeconds:N2} s  ({result.RowCount / sw.Elapsed.TotalSeconds:N0} rows/s)");
Console.WriteLine($"File size:   {size / 1024.0 / 1024.0:N2} MB");
Console.WriteLine($"Peak WS:     {Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0:N0} MB");
Console.WriteLine($"Allocated:   {alloc / 1024.0 / 1024.0:N0} MB (total, GC-collected)");
Console.WriteLine($"Output:      {Path.GetFullPath(output)}");

static string FindRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir != null && !Directory.Exists(Path.Combine(dir, "templates"))) dir = Path.GetDirectoryName(dir);
    return dir ?? Directory.GetCurrentDirectory();
}
