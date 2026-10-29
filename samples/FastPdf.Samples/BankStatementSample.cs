namespace FastPdf.Samples;

/// <summary>هم‌شکل با TrnResultForExport در سامانه صورتحساب.</summary>
public sealed class TrnRow
{
    public string TR0108 { get; set; } = "";   // تاریخ yyyyMMdd
    public string TR0121 { get; set; } = "";   // ساعت HHmmss
    public string desc_desc { get; set; } = ""; // شرح
    public short? TR0116 { get; set; }         // کد شعبه
    public string UserCode { get; set; } = "";
    public string TR0119 { get; set; } = "";   // فیش/حواله
    public decimal TR0105 { get; set; }        // برداشت
    public decimal TR0106 { get; set; }        // واریز
    public decimal? TR0110 { get; set; }       // مانده
    public string TR0124 { get; set; } = "";   // اطلاعات اضافی
    public string Info { get; set; } = "";     // PSP
}

public static class SampleData
{
    private static readonly string[] Descriptions =
    {
        "انتقال وجه پایا", "برداشت از خودپرداز", "واریز حقوق", "خرید کارتی - فروشگاه رفاه",
        "انتقال کارت به کارت", "پرداخت قبض برق", "سود سپرده", "کارمزد خدمات بانکی",
        "انتقال ساتنا به IR120170000000123456789012", "چک برگشتی شماره 123456", "خرید اینترنتی (شاپرک)"
    };

    private static readonly string[] Psp = { "شاپرک", "سداد", "به‌پرداخت", "آسان پرداخت", "" };

    /// <summary>تولید ردیف‌ها به صورت lazy (بدون نگهداری در حافظه).</summary>
    public static IEnumerable<TrnRow> Rows(int count, int seed = 42)
    {
        var rnd = new Random(seed);
        var pc = new System.Globalization.PersianCalendar();
        decimal balance = 1_250_000_000;
        var date = new DateTime(2024, 3, 20);
        string dateText = "";
        for (int i = 0; i < count; i++)
        {
            if (i % 50 == 0)
            {
                date = date.AddDays(1);
                dateText = $"{pc.GetYear(date):0000}{pc.GetMonth(date):00}{pc.GetDayOfMonth(date):00}";
            }
            bool debit = rnd.Next(3) != 0;
            decimal amount = rnd.Next(1, 5000) * 10_000m;
            balance += debit ? -amount : amount;
            yield return new TrnRow
            {
                TR0108 = dateText,
                TR0121 = $"{rnd.Next(24):00}{rnd.Next(60):00}{rnd.Next(60):00}",
                desc_desc = Descriptions[rnd.Next(Descriptions.Length)],
                TR0116 = (short)rnd.Next(1000, 9999),
                UserCode = "U" + rnd.Next(1000, 9999),
                TR0119 = rnd.Next(100000, 999999).ToString(),
                TR0105 = debit ? amount : 0,
                TR0106 = debit ? 0 : amount,
                TR0110 = balance,
                TR0124 = rnd.Next(4) == 0 ? "IR" + rnd.NextInt64(100000000000, 999999999999) : "",
                Info = Psp[rnd.Next(Psp.Length)]
            };
        }
    }

    public static Dictionary<string, object?> Parameters() => new()
    {
        ["BankName"] = "بانک نمونه",
        ["ReportDate"] = "1403/05/12",
        ["ReportTime"] = "14:25",
        ["AccountNumber"] = "0105123456001",
        ["FromDate"] = "1403/01/01",
        ["ToDate"] = "1403/05/12",
        ["Currency"] = "ریال",
        ["FirstName"] = "علی",
        ["LastName"] = "رضایی",
        ["NationalCode"] = "0012345678",
        ["CustBranch"] = "مرکزی (1001)",
        ["Branch"] = "ونک (1234)",
        ["OpenDate"] = "1398/02/15",
        ["CustNumber"] = "9876543",
        ["BranchCodeReq"] = "1001",
        ["Filter"] = "همه تراکنش‌ها"
    };
}
