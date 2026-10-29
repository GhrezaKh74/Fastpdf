# راهنمای استفاده در سامانهٔ صورتحساب (جایگزینی Stimulsoft)

این راهنما نشان می‌دهد چطور `FastPdfWriterStream` را در پروژهٔ `NewBilling` (ریپوی `sor`) کنار
`PdfStimulSoftReporter` قرار دهید و بعد از تست، جایگزینش کنید.

## ۱. افزودن پکیج

تا زمان انتشار روی NuGet، می‌توانید پروژه را مستقیماً reference کنید یا پکیج محلی بسازید:

```bash
./build.sh                                     # خروجی در ./nupkg
dotnet add backEnd/Services package FastPdfWriterStream --source ../Fastpdf/nupkg
dotnet add backEnd/Billing.Api package FastPdfWriterStream.Designer --source ../Fastpdf/nupkg
```

## ۲. فونت‌ها و قالب

- فونت‌های فعلی در `Billing.Api/wwwroot/Fonts` هستند (`BNazanin.ttf`، `IRANSans.ttf`، `arial.ttf`، ...) و همان‌ها
  قابل استفاده‌اند.
- فونت‌های قدیمی B (مثل B Nazanin) حروف لاتین ندارند؛ برای آن‌ها `fallback` تعریف کنید:

```json
"fonts": [
  { "name": "IRANSans", "path": "IRANSans.ttf" },
  { "name": "Nazanin",  "path": "BNazanin.ttf", "fallback": "IRANSans" }
]
```

- قالب نمونهٔ صورتحساب (هم‌شکل با `Reports/Bill/Report-pdf.mrt`) در `templates/bank-statement.json` است. آن را در
  `Billing.Api/Reports/Bill/bank-statement.json` کپی کنید و با Designer ویرایش کنید.

## ۳. ثبت در DI (Program/Startup)

```csharp
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new ReportEngine(new ReportEngineOptions
    {
        FontsDirectory     = Path.Combine(env.ContentRootPath, "wwwroot", "Fonts"),
        TemplatesDirectory = Path.Combine(env.ContentRootPath, "Reports", "Bill"),
        ImagesDirectory    = Path.Combine(env.ContentRootPath, "wwwroot", "images")
    });
});

// Designer — فقط برای ادمین گزارش‌ها
builder.Services.AddFastPdfDesigner(o =>
{
    o.TemplatesDirectory = Path.Combine(builder.Environment.ContentRootPath, "Reports", "Bill");
    o.FontsDirectory     = Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "Fonts");
    // فیلدها و عنوان فارسی‌شان از [DisplayName] های TrnResultForExport خوانده می‌شود
    o.AddDataSource<TrnResultForExport>("Bill", n => SampleTransactions(n), () => SampleHeader());
});
...
app.MapFastPdfDesigner("/pdf-designer").RequireAuthorization("<نام policy ادمین>");
```

## ۴. Reporter جدید (کنار PdfStimulSoftReporter)

اسکلت زیر هم‌شکل با `PdfStimulSoftReporter.RenderReport` است و همان `ExportResultModel` را برمی‌گرداند:

```csharp
public class FastPdfReporter : ExportReporter<ExportModel, ExporReporttModel>,
                               IPdfStimulExportReporter<ExportModel, ExporReporttModel>, IScopedDependency
{
    private readonly ReportEngine _engine;
    private readonly IExportStorage _storage;
    private readonly IPolicyExportServices _policy;

    public FastPdfReporter(ReportEngine engine, IExportStorage storage, IPolicyExportServices policy)
    {
        _engine = engine; _storage = storage; _policy = policy;
    }

    public override Task<ExportResultModel> RenderReport(ExportModel model, ExporReporttModel datas, CancellationToken ct)
    {
        var template = _engine.LoadTemplate("bank-statement").Clone();

        // ستون‌های مجاز بر اساس Policy (معادل DynamicExsistReportTable)
        var allowed = _policy.GetPolicyByClaimAndModel(model).AccessAccount.LimitAccount.Columns
                             .Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var col in template.Table!.Columns)
            if (col.Field != "RowNumber") col.Visible = allowed.Contains(col.Field);

        // بیش از ۱۲ ستون ← هر رکورد در دو سطر (معادل جدول ۴ سطری گزارش پویا)
        template.Table.MaxColumnsPerLine = 12;

        // ارز با اعشار (معادل ArzTypeModel.DecimalPoint / DivisionValue)
        if (datas.ArzTypeModel.DecimalPoint != 0)
        {
            var fmt = $"div:{datas.ArzTypeModel.DivisionValue}:#,0.{new string('0', datas.ArzTypeModel.DecimalPoint)}";
            foreach (var c in template.Table.Columns.Where(c => c.Field is "TR0105" or "TR0106" or "TR0110"))
                c.Format = fmt;
        }

        var cust = datas.Customer;
        var parameters = new Dictionary<string, object?>
        {
            ["BankName"]      = "بانک ملی ایران",
            ["AccountNumber"] = model.Trn.AccountNum,
            ["FirstName"]     = cust.FirstName?.Trim(),
            ["LastName"]      = cust.LastName?.Trim(),
            ["FromDate"]      = model.Trn.DateFrom.ToString().ToValidDate(),
            ["ToDate"]        = model.Trn.DateTo.ToString().ToValidDate(),
            ["CustNumber"]    = datas.CustNumber,
            ["Branch"]        = $"{cust.BranchName}({cust.Branch})",
            ["CustBranch"]    = $"{cust.CustBranchName}({cust.CustBranch})",
            ["OpenDate"]      = cust.OpenDate.ToString().ToValidDate(),
            ["NationalCode"]  = cust.NationalCode,
            ["Currency"]      = datas.ArzTypeModel.Desc,
            ["BranchCodeReq"] = model.BranchCodeReq?.ToString() ?? "توسط مشتری",
            ["ReportDate"]    = DateTime.Now.ToString("yyyy/MM/dd", new System.Globalization.CultureInfo("fa-IR")),
            ["ReportTime"]    = DateTime.Now.ToString("HH:mm")
        };

        var path = Path.Combine(_storage.Directory(ExportFolder.BillPdf), Guid.NewGuid() + ".pdf");
        // datas.TrnResult یک IEnumerable است: ردیف‌ها جریانی نوشته می‌شوند و کل داده در حافظه کپی نمی‌شود
        var result = _engine.Render(template, datas.TrnResult, path, parameters, ct);

        return Task.FromResult(new ExportResultModel(path: path, pageCount: result.PageCount,
            itemCount: (int)result.RowCount, date: /* همان LastDateInResult فعلی */ 0));
    }
}
```

> اگر داده از دیتابیس با `IAsyncEnumerable` یا `IDataReader` قابل خواندن است، از `RenderAsync` یا
> `Render(template, reader, ...)` استفاده کنید تا حتی لیست تراکنش‌ها هم در حافظه ساخته نشود.

## ۵. معادل‌ها در Stimulsoft

| Stimulsoft | FastPdfWriterStream |
|---|---|
| `{Substring(pdf.TR0108,0,4)}/{...}` | `"format": "mask:####/##/##"` |
| `{Substring(pdf.TR0121,0,2)}:{...}` | `"format": "mask:##:##:##"` |
| `{Line}` | `"field": "RowNumber"` یا `{RowNumber}` |
| `{TotalPageCount} / {PageNumber}` | `{TotalPages} / {PageNumber}` |
| `{SumDebit}` | `"aggregate": "Sum"` روی ستون، یا `{Sum(TR0105)}` در باند |
| `ShrinkFontToFit` | `"overflow": "Shrink"` (پیش‌فرض) |
| `StiOptions.Export.Pdf.ConvertDigitsToArabic` | `"digits": "Persian"` |
| `Report-NoData.mrt` | `"emptyText"` در جدول |
| `StiTextFormat` ارز | `"format": "div:100:#,0.00"` |

## ۶. مقایسهٔ سرعت

برای مقایسه با گزارش فعلی، یک حساب پرتراکنش را با هر دو reporter خروجی بگیرید. نمونهٔ benchmark این ریپو:

```bash
dotnet run -c Release --project samples/FastPdf.Samples -- 1000000
```
