using System.Data;
using FastPdfWriterStream.Designer;
using FastPdfWriterStream.Templates;
using Microsoft.Extensions.Options;

namespace FastPdfWriterStream.Tests;

public class TemplateTests
{
    public sealed class Row
    {
        public string Desc { get; set; } = "";
        public decimal Amount { get; set; }
        public string Date { get; set; } = "";
        public string Branch { get; set; } = "";
        public Customer? Customer { get; set; }
    }

    public sealed class Customer
    {
        public string Name { get; set; } = "";
    }

    private static ReportEngine Engine() => new(new ReportEngineOptions
    {
        FontsDirectory = TestFonts.FontsDir,
        TemplatesDirectory = TestFonts.TemplatesDir,
        ParallelCompression = false
    });

    private static ReportTemplate SimpleTemplate(Action<ReportTemplate>? change = null)
    {
        var t = new ReportTemplate
        {
            Name = "test",
            Digits = "Latin",
            Fonts = { new FontDefinition { Name = "Vazir", Path = "Vazirmatn-Regular.ttf", BoldPath = "Vazirmatn-Bold.ttf" } },
            DefaultFont = "Vazir",
            ReportHeader = new BandDefinition
            {
                Height = 30,
                Elements = { new ElementDefinition { Text = "گزارش {Title}", Width = 400, Height = 20 } }
            },
            Table = new TableDefinition
            {
                RowHeight = 15,
                Columns =
                {
                    new ColumnDefinition { Header = "ردیف", Field = "RowNumber", Width = 30 },
                    new ColumnDefinition { Header = "تاریخ", Field = "Date", Format = "mask:####/##/##", Width = 70 },
                    new ColumnDefinition { Header = "شرح", Field = "Desc", Width = 200 },
                    new ColumnDefinition { Header = "مبلغ", Field = "Amount", Format = "N0", Aggregate = "Sum", Width = 90 }
                },
                ShowGrandTotals = true
            },
            ReportFooter = new BandDefinition
            {
                Height = 20,
                Elements = { new ElementDefinition { Text = "TOTAL={Sum(Amount):N0} COUNT={Count}", Width = 400, Height = 16, Direction = "LeftToRight" } }
            },
            PageFooter = new BandDefinition
            {
                Height = 20,
                Elements = { new ElementDefinition { Text = "Page {PageNumber} of {TotalPages}", Width = 200, Height = 16 } }
            }
        };
        change?.Invoke(t);
        return t;
    }

    private static IEnumerable<Row> Rows(int n) => Enumerable.Range(1, n).Select(i => new Row
    {
        Desc = i % 2 == 0 ? "انتقال وجه" : "واریز",
        Amount = i * 1000,
        Date = "14030512",
        Branch = "B" + (i / 10),
        Customer = i % 3 == 0 ? null : new Customer { Name = "C" + i }
    });

    private static PdfTestReader Render<T>(ReportTemplate t, IEnumerable<T> rows, object? parameters = null)
    {
        var bytes = Engine().RenderToBytes(t, rows, parameters ?? new { Title = "آزمایشی" });
        return new PdfTestReader(bytes);
    }

    [Fact]
    public void Render_ComputesTotals_AndPaginates()
    {
        var r = Render(SimpleTemplate(), Rows(500));
        Assert.True(r.PageCount > 10);
        var text = r.AllText();
        Assert.Contains("TOTAL=125,250,000 COUNT=500", text);
        Assert.Contains("1403/05/12", text);
        Assert.Contains("125,250,000", r.PageText(r.PageCount - 1));
    }

    [Fact]
    public void Render_EmptyData_ShowsEmptyText()
    {
        var r = Render(SimpleTemplate(), Array.Empty<Row>());
        Assert.Equal(1, r.PageCount);
        Assert.Contains(PdfTestReader.Reverse("وجود ندارد"), r.PageText(0));
    }

    [Fact]
    public void Render_PageTotals_CarryForward_Groups()
    {
        var t = SimpleTemplate(t =>
        {
            t.Table!.ShowPageTotals = true;
            t.Table.ShowCarryForward = true;
            t.Table.GroupBy = "Branch";
            t.Table.GroupHeaderText = "شعبه {Branch}";
            t.Table.ShowGroupTotals = true;
        });
        var r = Render(t, Rows(300));
        Assert.True(r.PageCount > 5);
        Assert.Contains("TOTAL=45,150,000", r.AllText());
    }

    [Fact]
    public void Render_WrapColumns_Watermark_Bold_Image()
    {
        var png = WriterTests.CreatePng(8, 8, withAlpha: false);
        var t = SimpleTemplate(t =>
        {
            t.Table!.Columns[2].Overflow = "Wrap";
            t.Watermark = new WatermarkDefinition { Text = "رونوشت" };
            t.ReportHeader!.Elements.Add(new ElementDefinition { Type = "Image", Source = "data:image/png;base64," + Convert.ToBase64String(png), Width = 30, Height = 30 });
            t.ReportHeader.Elements.Add(new ElementDefinition { Text = "Bold", Bold = true, Y = 20, Border = new BorderDefinition { Sides = "B" } });
        });
        var rows = Rows(50).Select(r => { r.Desc = string.Join(" ", Enumerable.Repeat(r.Desc, 15)); return r; });
        var r = Render(t, rows);
        Assert.True(r.PageCount >= 2);
    }

    [Fact]
    public void Render_MultiLineRecords_WhenManyColumns()
    {
        var t = SimpleTemplate(t =>
        {
            for (int i = 0; i < 12; i++) t.Table!.Columns.Add(new ColumnDefinition { Header = "C" + i, Field = "Branch", Width = 40 });
            t.Table!.MaxColumnsPerLine = 8;
            t.Table.ShowPageTotals = true;
        });
        var single = Render(SimpleTemplate(), Rows(300)).PageCount;
        var r = Render(t, Rows(300));
        Assert.True(r.PageCount > single, "two lines per record need more pages");
        Assert.Contains("TOTAL=45,150,000", r.AllText());
    }

    [Fact]
    public void Render_DetailsLine_PrintsOnlyFilledFields_AndCollapsesEmptyLine()
    {
        var t = SimpleTemplate(t =>
        {
            t.Table!.Columns[0].SpanLines = true;
            t.Table.Columns.Add(new ColumnDefinition
            {
                Header = "جزئیات", Line = 1,
                Details = new() { new DetailField { Label = "B", Field = "Branch" }, new DetailField { Label = "N", Field = "Customer.Name" } }
            });
        });
        var rows = Rows(40).ToList();
        foreach (var r in rows.Where((_, i) => i % 2 == 0)) { r.Branch = ""; r.Customer = null; }
        var text = Render(t, rows).AllText();
        Assert.Contains("C2", text);              // پرشده چاپ می‌شود
        Assert.DoesNotContain("N: C5", text);     // ردیف ۵ (بدون جزئیات) چیزی ندارد
        // سطر خالی جمع می‌شود: نسبت به جدول بدون جزئیات صفحهٔ بسیار بیشتری لازم نیست
        Assert.True(Render(t, rows).PageCount <= Render(SimpleTemplate(), Rows(40)).PageCount * 2);
    }

    [Fact]
    public void BankStatementWideTemplate_Renders()
    {
        var engine = Engine();
        var t = engine.LoadTemplate("bank-statement-wide");
        var rows = Enumerable.Range(0, 100).Select(i => new Dictionary<string, object?>
        {
            ["TR0108"] = "14030512", ["desc_desc"] = "انتقال وجه", ["TR0105"] = 1000m * i,
            ["Sheba"] = i % 3 == 0 ? "IR120170000000123456789012" : "", ["SourceCardNo"] = i % 2 == 0 ? "6037991234567890" : null
        });
        var r = new PdfTestReader(engine.RenderToBytes(t, rows, new { BankName = "بانک" }));
        Assert.Contains("IR120170000000123456789012", r.AllText());
    }

    [Fact]
    public void Render_FromDictionaryRows_AndNestedFields()
    {
        var t = SimpleTemplate(t => t.Table!.Columns.Add(new ColumnDefinition { Header = "مشتری", Field = "Customer.Name" }));
        Assert.Contains("C1", Render(t, Rows(5)).AllText());

        var dictRows = Rows(5).Select(x => new Dictionary<string, object?> { ["Desc"] = x.Desc, ["Amount"] = x.Amount, ["Date"] = x.Date });
        Assert.Contains("TOTAL=15,000", Render(SimpleTemplate(), dictRows).AllText());
    }

    [Fact]
    public void Render_FromDataTable_AndDataReader()
    {
        var dt = new DataTable();
        dt.Columns.Add("Desc", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        dt.Columns.Add("Date", typeof(string));
        for (int i = 1; i <= 10; i++) dt.Rows.Add("x", i, "14030101");

        var engine = Engine();
        using (var ms = new MemoryStream())
        {
            engine.Render(SimpleTemplate(), dt, ms, new { Title = "t" });
            Assert.Contains("TOTAL=55", new PdfTestReader(ms.ToArray()).AllText());
        }
        using (var ms = new MemoryStream())
        {
            engine.Render(SimpleTemplate(), dt.CreateDataReader(), ms, new { Title = "t" });
            Assert.Contains("TOTAL=55", new PdfTestReader(ms.ToArray()).AllText());
        }
    }

    [Fact]
    public async Task Render_AsyncEnumerable()
    {
        static async IAsyncEnumerable<Row> Source()
        {
            foreach (var r in Rows(100)) { await Task.Yield(); yield return r; }
        }
        using var ms = new MemoryStream();
        var result = await Engine().RenderAsync(SimpleTemplate(), Source(), ms, new { Title = "a" });
        Assert.Equal(100, result.RowCount);
        Assert.Contains("TOTAL=5,050,000", new PdfTestReader(ms.ToArray()).AllText());
    }

    [Fact]
    public void Render_Cancellation_StopsWithoutCorruptingCaller()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var ms = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => Engine().Render(SimpleTemplate(), Rows(1000), ms, null, cts.Token));
    }

    [Fact]
    public void FormOnly_Report()
    {
        var t = SimpleTemplate(t => t.Table = null);
        using var ms = new MemoryStream();
        Engine().RenderForm(t, ms, new { Title = "فرم" });
        Assert.Equal(1, new PdfTestReader(ms.ToArray()).PageCount);
    }

    [Fact]
    public void BankStatementTemplate_RendersFromRepository()
    {
        var engine = Engine();
        var t = engine.LoadTemplate("bank-statement");
        var rows = Enumerable.Range(0, 200).Select(i => new Dictionary<string, object?>
        {
            ["TR0108"] = "14030512", ["TR0121"] = "142530", ["desc_desc"] = "انتقال وجه", ["TR0105"] = 1000m * i,
            ["TR0106"] = 0m, ["TR0110"] = -5000m + i, ["TR0116"] = (short)1234
        });
        var r = new PdfTestReader(engine.RenderToBytes(t, rows, new { BankName = "بانک", AccountNumber = "123" }));
        Assert.True(r.PageCount >= 6);
    }

    [Fact]
    public void Template_JsonRoundTrip()
    {
        var t = SimpleTemplate();
        var back = ReportTemplate.FromJson(t.ToJson());
        Assert.Equal(t.Table!.Columns.Count, back.Table!.Columns.Count);
        Assert.Equal("mask:####/##/##", back.Table.Columns[1].Format);
        Assert.Contains("\"rightToLeft\": true", t.ToJson());
    }

    [Theory]
    [InlineData("14030512", "mask:####/##/##", "1403/05/12")]
    [InlineData("142530", "mask:##:##:##", "14:25:30")]
    [InlineData(1234567.891, "N0", "1,234,568")]
    [InlineData(-1500, "abs:N0", "1,500")]
    [InlineData(-1500, "#,0;(#,0);-", "(1,500)")]
    [InlineData(0, "#,0;(#,0);-", "-")]
    [InlineData(true, "bool:بله|خیر", "بله")]
    [InlineData(123456, "div:100:#,0.00", "1,234.56")]
    [InlineData(-123456, "abs:div:1000:N1", "123.5")]
    public void ValueFormatter_Formats(object value, string format, string expected)
    {
        if (value is double d) value = (decimal)d;
        if (value is int i) value = (decimal)i;
        Assert.Equal(expected, ValueFormatter.Format(value, FormatSpec.Get(format)));
    }

    [Fact]
    public void ValueFormatter_PersianDate()
    {
        Assert.Equal("1403/05/12", ValueFormatter.Format(new DateTime(2024, 8, 2), FormatSpec.Get("persian")));
        Assert.Equal("03-5-12 14:05", ValueFormatter.Format(new DateTime(2024, 8, 2, 14, 5, 0), FormatSpec.Get("persian:yy-M-dd HH:mm")));
    }

    [Theory]
    [InlineData("bank-statement", true)]
    [InlineData("قالب فارسی", true)]
    [InlineData("../secret", false)]
    [InlineData("..\\secret", false)]
    [InlineData("a/b", false)]
    [InlineData(".hidden", false)]
    [InlineData("", false)]
    public void Designer_TemplatePath_BlocksTraversal(string name, bool ok)
    {
        var svc = new DesignerService(Options.Create(new PdfDesignerOptions { TemplatesDirectory = Path.GetTempPath() }));
        Assert.Equal(ok, svc.TemplatePath(name) != null);
    }

    [Fact]
    public void Designer_PreviewWithTemplateSamples()
    {
        var svc = new DesignerService(Options.Create(new PdfDesignerOptions
        {
            TemplatesDirectory = TestFonts.TemplatesDir,
            FontsDirectory = TestFonts.FontsDir
        }));
        var t = ReportTemplate.Load(Path.Combine(TestFonts.TemplatesDir, "bank-statement.json"));
        var pdf = svc.Preview(t, null, 100);
        Assert.True(new PdfTestReader(pdf).PageCount >= 3);
    }
}
