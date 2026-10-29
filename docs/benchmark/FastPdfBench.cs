using System.Diagnostics;
using FastPdfWriterStream.Templates;

int n = args.Length > 0 ? int.Parse(args[0]) : 10208;
string tpl = args.Length > 1 ? args[1] : "bank-statement-portrait-full";
var rows = BenchData.Rows(n);
var parameters = new Dictionary<string, object> {
    ["BankName"] = "بانک نمونه", ["AccountNumber"] = "0105123456001", ["FirstName"] = "علی", ["LastName"] = "رضایی",
    ["FromDate"] = "1403/01/01", ["ToDate"] = "1403/12/29", ["CustNumber"] = "9876543", ["Branch"] = "ونک(1234)",
    ["CustBranch"] = "مرکزی(1001)", ["OpenDate"] = "1398/02/15", ["NationalCode"] = "0012345678", ["Currency"] = "ریال",
    ["BranchCodeReq"] = "توسط مشتری", ["ReportDate"] = "1403/12/29", ["ReportTime"] = "14:25", ["Filter"] = "همه تراکنش‌ها" };

var sw = Stopwatch.StartNew();
var engine = new ReportEngine(new ReportEngineOptions { FontsDirectory = "/home/user/Fastpdf/fonts", TemplatesDirectory = "/home/user/Fastpdf/templates" });
var template = engine.LoadTemplate(tpl);
var cold = engine.Render(template, rows, $"fastpdf-cold-{n}.pdf", parameters);
Console.WriteLine($"FASTPDF cold (first report in process, incl. JIT + font load): {sw.ElapsedMilliseconds}ms pages={cold.PageCount}");

for (int i = 0; i < 3; i++) engine.RenderToBytes(template, rows.Take(2000).ToList(), parameters);
GC.Collect();
long before = GC.GetTotalAllocatedBytes(true);
sw.Restart();
var r = engine.Render(template, rows, $"fastpdf-{n}.pdf", parameters);
sw.Stop();
long alloc = GC.GetTotalAllocatedBytes(true) - before;
Console.WriteLine($"FASTPDF rows={n} pages={r.PageCount} total={sw.ElapsedMilliseconds}ms size={new FileInfo($"fastpdf-{n}.pdf").Length / 1024.0 / 1024.0:N2}MB peakWS={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB alloc={alloc / 1048576}MB");
