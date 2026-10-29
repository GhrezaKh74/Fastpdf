using System.Diagnostics;
using Stimulsoft.Base;
using Stimulsoft.Report;
using Stimulsoft.Report.Export;

// همان مسیر کد سامانه: RenderPartCommonReport + PdfStimulSoftReporter (Report-pdf.mrt)
int n = args.Length > 0 ? int.Parse(args[0]) : 10208;
string mrt = "/home/user/sor/backEnd/Billing.Api/Reports/Bill/Report-pdf.mrt";
// مانند DynamicPdfStimulSoftReporter سامانه: بدون کامپایل اسکریپت (حالت تفسیری)
StiOptions.Engine.FullTrust = false;
StiOptions.Export.Pdf.ConvertDigitsToArabic = true;
StiFontCollection.AddFontFile("/home/user/sor/backEnd/Billing.Api/wwwroot/Fonts/arial.ttf");

var rows = BenchData.Rows(n);

RenderResult Run(List<TrnResultForExport> data, string path)
{
    var sw = Stopwatch.StartNew();
    var report = new StiReport();
    report.RegBusinessObject("pdf", data);
    report.Load(mrt);
    report.CalculationMode = StiCalculationMode.Interpretation;
    report["accountNumber"] = 105123456001L;
    report["Name"] = "علی"; report["Family"] = "رضایی";
    report["FromDate"] = "1403/01/01"; report["ToDate"] = "1403/12/29";
    report["CustNumber"] = 9876543L; report["Branch"] = "ونک(1234)"; report["CustBranch"] = "مرکزی(1001)";
    report["OpenDate"] = "1398/02/15"; report["NationalCode"] = "0012345678"; report["Currency"] = "ریال";
    report["SumDebit"] = data.Sum(s => s.TR0105); report["SumCredit"] = data.Sum(s => s.TR0106);
    report["BranchCodeReq"] = "توسط مشتری";
    report["SumRemainder"] = data.Select(s => s.TR0110).LastOrDefault();
    report.Dictionary.Synchronize();
    report.Render(false);
    var renderMs = sw.ElapsedMilliseconds;
    var config = new StiPdfExportSettings { EmbeddedFonts = true };
    report.ExportDocument(StiExportFormat.Pdf, path, config);
    sw.Stop();
    return new RenderResult(report.RenderedPages.Count, renderMs, sw.ElapsedMilliseconds);
}

// گرم کردن (مثل سرور که قبلا گزارش ساخته)
Run(rows.Take(300).ToList(), "warm.pdf");
GC.Collect();
long before = GC.GetTotalAllocatedBytes(true);
var r = Run(rows, $"stimulsoft-{n}.pdf");
long alloc = GC.GetTotalAllocatedBytes(true) - before;
Console.WriteLine($"STIMULSOFT rows={n} pages={r.Pages} render={r.RenderMs}ms total={r.TotalMs}ms size={new FileInfo($"stimulsoft-{n}.pdf").Length / 1024.0 / 1024.0:N2}MB peakWS={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MB alloc={alloc / 1048576}MB");

record RenderResult(int Pages, long RenderMs, long TotalMs);
