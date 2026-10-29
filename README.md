# FastPdfWriterStream

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-blue.svg)](https://dotnet.microsoft.com)

A fast, memory-efficient **streaming PDF writer and report engine** for .NET 8+.
It has full **Persian/Arabic** support (OpenType shaping, bidi, Persian digits and Persian dates), and a
**visual template designer** that runs inside any ASP.NET Core app.

It's the PDF sibling of [FastExcelWriterStream](https://github.com/GhrezaKh74/FastExcelWriterStream). Each page
is written to the output stream as soon as it's full, so memory stays flat whether the report has 100 rows or 10 million.

> **فارسی:** کتابخانهٔ تولید PDF جریانی و بسیار سریع با پشتیبانی کامل فارسی (اتصال حروف، راست‌به‌چپ،
> ارقام فارسی، تاریخ شمسی) + طراح بصری قالب. مستندات فارسی و راهنمای جایگزینی Stimulsoft در سامانهٔ
> صورتحساب: [docs/INTEGRATION-fa.md](docs/INTEGRATION-fa.md)

---

## ✨ Features

- 🚀 **Fast**: about 180,000 table rows/s (12 columns, Persian text) once the JIT is warm
- 💾 **Constant memory**: pages stream to disk. 1M rows → 30,304 pages with a peak working set of about 80 MB
- ⚡ **Parallel compression**: page content is compressed on worker threads while the next page is laid out
- 🔤 **Persian / Arabic**: GSUB shaping (init/medi/fina/isol), lam-alef ligatures, ZWNJ, Unicode bidi (UAX #9), mirrored brackets, Persian/Arabic digits
- 🅰️ **TrueType subsetting**: only the glyphs you use are embedded, with a ToUnicode map so text stays searchable and copyable
- 🔁 **Font fallback**: fonts such as *B Nazanin* that lack Latin glyphs fall back automatically
- 📄 **JSON templates**: page header/footer, report header/footer, paginated tables with repeated headers, page totals, grand totals, carry-forward, group-by, watermark, images, `Page X of Y`
- 🧮 **Formats**: `N0`, `#,0;(#,0);-`, `persian` (Shamsi date), `mask:####/##/##`, `div:100:#,0.00`, `abs:`, `bool:بله|خیر`
- 🎨 **Visual designer**: WYSIWYG, RTL, works offline, one line to host: `app.MapFastPdfDesigner("/pdf-designer")`
- 🔌 **Data sources**: `IEnumerable<T>` (compiled getters, no reflection per row), `IAsyncEnumerable<T>` (EF Core streaming), `DataTable`, `IDataReader`, dictionaries
- 🔏 **Digital signature**: PKCS#7 detached (SHA-256), invisible signature field
- 🧱 **Drawing API**: text boxes (shrink / wrap / clip / ellipsis), lines, rectangles, rounded rectangles, JPEG/PNG (with alpha), opacity, rotated text

---

## 📊 Performance

Bank statement template (A4 landscape, 12 columns, Persian descriptions, alternating rows, grid, totals). Measured on a 4-core Linux container with .NET 8:

| Rows | Pages | Time | Peak memory | File size |
|---:|---:|---:|---:|---:|
| 100,000 | 3,031 | 0.56 s (warm) | ~80 MB | 14.7 MB |
| 1,000,000 | 30,304 | ~10 s (incl. data generation) | ~81 MB | 148 MB |

Run it yourself:

```bash
dotnet run -c Release --project samples/FastPdf.Samples -- 1000000
```

---

## 📦 Projects

| Package | What |
|---|---|
| `FastPdfWriterStream` | Core: `PdfWriter`, `PdfPage` drawing API, fonts, shaping, `ReportEngine` + JSON templates |
| `FastPdfWriterStream.Designer` | ASP.NET Core endpoints + embedded designer UI (no CDN, works on intranets) |

Both target `net8.0` and `net10.0`.

---

## 🚀 Quick start: report from a template

```csharp
using FastPdfWriterStream.Templates;

// Register once (singleton). Fonts and templates are parsed once and cached.
var engine = new ReportEngine(new ReportEngineOptions
{
    FontsDirectory = "wwwroot/Fonts",
    TemplatesDirectory = "Reports/Templates"
});

var template = engine.LoadTemplate("bank-statement");   // Reports/Templates/bank-statement.json

engine.Render(template, transactions, "statement.pdf", new
{
    BankName = "بانک ملی ایران",
    AccountNumber = "0105123456001",
    FirstName = "علی",
    LastName = "رضایی",
    FromDate = "1403/01/01",
    ToDate = "1403/05/12"
});
```

### Streaming from the database (no list in memory)

```csharp
await engine.RenderAsync(template, db.Transactions.AsNoTracking().AsAsyncEnumerable(), stream, parameters, ct);

// or straight from a DataReader
using var reader = command.ExecuteReader();
engine.Render(template, reader, stream, parameters);

// or push rows yourself
using var r = engine.Begin<TrnRow>(template, stream, parameters);
foreach (var row in rows) r.AddRow(row);
var result = r.Complete();   // result.PageCount, result.RowCount, result.Elapsed
```

### In ASP.NET Core

`PdfWriter` writes synchronously, so buffer through a temp file (`FileBufferingWriteStream` spills to disk past 32 KB):

```csharp
app.MapGet("/reports/statement", async (HttpContext ctx, ReportEngine engine) =>
{
    var template = engine.LoadTemplate("bank-statement");
    ctx.Response.ContentType = "application/pdf";
    await using var buffer = new FileBufferingWriteStream();
    engine.Render(template, rows, buffer, parameters);
    await buffer.DrainBufferAsync(ctx.Response.Body);
});
```

### Change columns at runtime (e.g. access policy)

```csharp
var t = engine.LoadTemplate("bank-statement").Clone();     // don't modify the cached instance
foreach (var c in t.Table!.Columns) c.Visible = allowedFields.Contains(c.Field);
t.Table.FitWidth = true;                                    // remaining columns stretch to page width
// Currency with decimals: divide and format
t.Table.Columns.Single(c => c.Field == "TR0105").Format = "div:100:#,0.00";
```

---

## 🧩 Template format (JSON)

All sizes are in points (1/72 inch). Element `x`/`y` are relative to the band's top-left corner inside the page margins.

```jsonc
{
  "name": "bank-statement",
  "page": { "size": "A4", "landscape": true, "marginLeft": 20, "marginTop": 18, "marginRight": 20, "marginBottom": 18 },
  "rightToLeft": true,                 // first column on the right; Start = right
  "digits": "Persian",                 // Latin | Persian | Arabic
  "fonts": [ { "name": "Vazirmatn", "path": "Vazirmatn-Regular.ttf", "boldPath": "Vazirmatn-Bold.ttf" },
             { "name": "Nazanin", "path": "BNazanin.ttf", "fallback": "Vazirmatn" } ],
  "defaultFont": "Vazirmatn", "defaultFontSize": 8,
  "styles": { "title": { "fontSize": 13, "bold": true, "align": "Center" } },

  "pageHeader":   { "height": 22, "skipFirstPage": true, "elements": [ ... ] },
  "reportHeader": { "height": 136, "elements": [
      { "type": "Text", "x": 250, "y": 14, "width": 300, "height": 22, "text": "{BankName} - صورتحساب", "style": "title" },
      { "type": "Image", "x": 0, "y": 0, "width": 60, "height": 40, "source": "{Logo}" },
      { "type": "Rect", "x": 0, "y": 40, "width": 801, "height": 88, "cornerRadius": 4, "lineColor": "#9E9E9E" } ] },
  "table": {
    "headerHeight": 22, "rowHeight": 15, "repeatHeader": true, "fitWidth": true,
    "altRowBackground": "#F5F7FA", "showGrandTotals": true, "showPageTotals": false, "showCarryForward": false,
    "groupBy": null, "emptyText": "تراکنشی یافت نشد",
    "maxColumnsPerLine": 12,           // >12 visible columns → each record on 2 lines (like the dynamic Stimulsoft report)
    "columns": [
      { "header": "ردیف",  "field": "RowNumber", "width": 32 },
      { "header": "تاریخ", "field": "TR0108", "format": "mask:####/##/##", "width": 58 },
      { "header": "شرح",   "field": "desc_desc", "width": 150, "overflow": "Wrap" },
      { "header": "برداشت", "field": "TR0105", "format": "#,0;(#,0);-", "aggregate": "Sum", "width": 80 },
      { "header": "مانده", "field": "TR0110", "format": "#,0", "negativeColor": "#C00000", "width": 88 }
    ]
  },
  "reportFooter": { "height": 60, "elements": [ { "type": "Text", "text": "جمع برداشت: {Sum(TR0105):N0}", "width": 400, "height": 16 } ] },
  "pageFooter":   { "height": 20, "elements": [ { "type": "Text", "text": "صفحه {PageNumber} از {TotalPages}", "align": "Center", "width": 200, "height": 16 } ] },
  "watermark": { "text": "رونوشت", "opacity": 0.12, "angle": 45 }
}
```

The full example lives in [templates/bank-statement.json](templates/bank-statement.json).

### Expressions

| Expression | Meaning |
|---|---|
| `{Field}` / `{Param}` | Row field (in table cells) or report parameter (in bands) |
| `{Field:N0}` | With a format (everything after the first `:`) |
| `{RowNumber}` `{Count}` | Current row number / rows so far |
| `{PageNumber}` `{TotalPages}` | Page numbers (the total is filled in when the document closes) |
| `{Today}` `{Now}` `{Today:persian}` `{Now:persian:HH:mm}` | Dates |
| `{Sum(Amount)}` `{Avg(..)}` `{Min(..)}` `{Max(..)}` `{Count()}` | Report totals (running totals in page footers) |
| `{PageSum(Amount)}` `{GroupSum(Amount)}` | Page and group scope |
| `{Customer.Name}` | Nested property |

`visibleWhen: "{ShowLogo}"` hides an element when the value is empty, `0` or `false`.

---

## 🎨 Designer

```csharp
builder.Services.AddFastPdfDesigner(o =>
{
    o.TemplatesDirectory = "Reports/Templates";
    o.FontsDirectory = "wwwroot/Fonts";
    // optional: sample data for preview; fields + Persian titles come from [DisplayName]
    o.AddDataSource("BankStatement", n => SampleRows(n), () => new { BankName = "..." });
});

app.MapFastPdfDesigner("/pdf-designer").RequireAuthorization("ReportAdmin");
```

Open `/pdf-designer/` to get a Persian RTL designer with:

- Bands, drag-and-drop elements, resize handles and snapping
- A table column editor
- Field and parameter lists
- Named styles
- Fonts loaded from the server, so the canvas matches the PDF
- PDF preview, undo/redo, and JSON import/export

Try it locally:

```bash
dotnet run --project samples/FastPdf.DesignerHost      # → http://localhost:5080/pdf-designer/
```

**Security:** the designer writes template files on the server. Always protect the route group with
`RequireAuthorization(...)`, or set `o.ReadOnly = true`. Template names are validated against path traversal.

---

## 🖊️ Low-level drawing API

```csharp
using var pdf = new PdfWriter("out.pdf", new PdfWriterOptions { RightToLeft = true, Digits = DigitStyle.Persian });
var font = pdf.AddFont("fonts/Vazirmatn-Regular.ttf");
var nazanin = pdf.AddFont("BNazanin.ttf").AddFallback(font);   // Latin glyphs come from Vazirmatn

var page = pdf.NewPage();
var style = new TextStyle { Font = font, Size = 10, Align = HAlign.Start, Overflow = TextOverflow.Shrink };
page.DrawText("صورتحساب شماره 12345", 20, 20, 300, 18, style);      // box: x, y, width, height (top-left origin)
page.DrawLine(20, 40, 575, 40, PdfColor.Gray, 0.5);
page.DrawRoundedRect(20, 50, 200, 60, 6, PdfColor.Blue, PdfColor.FromHex("#EEF3FB"));
page.DrawImage(pdf.AddImage("logo.png"), 480, 50, 90, 60);
page.DrawText("صفحه ", 20, 800, 555, 16, style);             //  = total page count placeholder
```

---

## 🔏 Digital signature

PDFs can be signed with an X.509 certificate (PKCS#7 detached, SHA-256), and Adobe Reader can verify the signature.

```csharp
var cert = /* X509Certificate2 with private key, e.g. from LocalMachine\My */;
using var pdf = new PdfWriter("signed.pdf", new PdfWriterOptions
{
    Signature = new PdfSignatureOptions(cert) { Reason = "گزارش پایانه فروش" }
});
```

The output must be readable and seekable. A file path or a `MemoryStream` both work.

## Notes and limitations

- Fonts: TrueType (`.ttf`) is subset. OpenType/CFF (`.otf`) is embedded whole. GPOS mark positioning and kerning are not applied (rarely needed for report text).
- Bidi: explicit embedding controls (LRE/RLE/PDF/isolates) are ignored. Mixed text follows the document direction.
- `{TotalPages}` reserves room for the expected number of digits, estimated from the row count when it's known.

## License

MIT. The bundled Vazirmatn font is licensed under the SIL Open Font License ([fonts/OFL.txt](fonts/OFL.txt)).
