// داده‌ی یکسان برای هر دو موتور (هم‌شکل با TrnResultForExport سامانه صورتحساب)
public class TrnResultForExport
{
    public string TR0108 { get; set; }
    public string TR0121 { get; set; }
    public string desc_desc { get; set; }
    public short? TR0116 { get; set; }
    public string UserCode { get; set; }
    public string TR0119 { get; set; }
    public decimal TR0105 { get; set; }
    public decimal TR0106 { get; set; }
    public decimal? TR0110 { get; set; }
    public string TR0124 { get; set; }
    public string Info { get; set; }
    public string Name { get; set; }
    public string Sheba { get; set; }
    public string PaymentCode { get; set; }
    public string SourceCardNo { get; set; }
    public string DestinationCardNo { get; set; }
    public string TrackingNo { get; set; }
    public string MachinSerialNo { get; set; }
    public string RefNo { get; set; }
    public string PaymentId { get; set; }
    public string InfoBranchCode { get; set; }
    public string InfoBankCode { get; set; }
    public string PSPNo { get; set; }
    public string AccountNo_S_D { get; set; }
}

public static class BenchData
{
    static readonly string[] Desc = { "انتقال وجه پایا", "برداشت از خودپرداز", "واریز حقوق", "خرید کارتی - فروشگاه رفاه",
        "انتقال کارت به کارت", "پرداخت قبض برق", "سود سپرده", "کارمزد خدمات بانکی", "چک برگشتی شماره 123456", "خرید اینترنتی (شاپرک)" };
    static readonly string[] Psp = { "شاپرک", "سداد", "به‌پرداخت", "آسان پرداخت", "" };

    /// <summary>نیمی از تراکنش‌ها اطلاعات تکمیلی (شبا، کارت، پیگیری ...) دارند.</summary>
    public static List<TrnResultForExport> Rows(int count)
    {
        var rnd = new Random(42);
        var pc = new System.Globalization.PersianCalendar();
        var date = new DateTime(2024, 3, 20);
        string dateText = "";
        decimal balance = 1_250_000_000;
        var list = new List<TrnResultForExport>(count);
        for (int i = 0; i < count; i++)
        {
            if (i % 30 == 0) { date = date.AddDays(1); dateText = $"{pc.GetYear(date):0000}{pc.GetMonth(date):00}{pc.GetDayOfMonth(date):00}"; }
            bool debit = rnd.Next(3) != 0;
            decimal amount = rnd.Next(1, 5000) * 10_000m;
            balance += debit ? -amount : amount;
            bool has = rnd.Next(2) == 0;
            list.Add(new TrnResultForExport
            {
                TR0108 = dateText,
                TR0121 = $"{rnd.Next(24):00}{rnd.Next(60):00}{rnd.Next(60):00}",
                desc_desc = Desc[rnd.Next(Desc.Length)],
                TR0116 = (short)rnd.Next(1000, 9999),
                UserCode = "U" + rnd.Next(1000, 9999),
                TR0119 = rnd.Next(100000, 999999).ToString(),
                TR0105 = debit ? amount : 0,
                TR0106 = debit ? 0 : amount,
                TR0110 = balance,
                TR0124 = has && rnd.Next(3) == 0 ? "IR" + rnd.NextInt64(100000000000, 999999999999) : "",
                Info = Psp[rnd.Next(Psp.Length)],
                Name = has ? "شرکت نمونه" : "",
                Sheba = has ? "IR" + rnd.NextInt64(100000000000, 999999999999) : "",
                PaymentCode = "",
                SourceCardNo = has ? "6037" + rnd.Next(10000000, 99999999) : "",
                DestinationCardNo = "",
                TrackingNo = has ? rnd.Next(100000, 999999).ToString() : "",
                MachinSerialNo = "",
                RefNo = has && rnd.Next(2) == 0 ? rnd.Next(1000000, 9999999).ToString() : "",
                PaymentId = "",
                InfoBranchCode = "",
                InfoBankCode = has ? "017" : "",
                PSPNo = "",
                AccountNo_S_D = has && rnd.Next(3) == 0 ? "0105" + rnd.Next(10000000, 99999999) : ""
            });
        }
        return list;
    }
}
