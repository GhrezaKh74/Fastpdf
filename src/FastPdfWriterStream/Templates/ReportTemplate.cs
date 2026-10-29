using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastPdfWriterStream.Templates;

/// <summary>
/// قالب گزارش (قابل ذخیره به صورت JSON و قابل ویرایش در Designer).
/// همهٔ اندازه‌ها بر حسب point (۱/۷۲ اینچ) هستند. مختصات عناصر نسبت به گوشهٔ بالا-چپ هر باند است.
/// </summary>
public sealed class ReportTemplate
{
    public string Name { get; set; } = "Report";
    public string? Description { get; set; }
    public int Version { get; set; } = 1;

    public PageSettings Page { get; set; } = new();

    /// <summary>جهت سند (برای جدول: ستون اول در سمت راست).</summary>
    public bool RightToLeft { get; set; } = true;

    /// <summary>Latin | Persian | Arabic</summary>
    public string Digits { get; set; } = "Persian";

    public List<FontDefinition> Fonts { get; set; } = new();
    public string? DefaultFont { get; set; }
    public double DefaultFontSize { get; set; } = 9;

    /// <summary>سبک‌های نام‌دار که عناصر و ستون‌ها می‌توانند به آن ارجاع دهند.</summary>
    public Dictionary<string, StyleDefinition> Styles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>فقط در صفحهٔ اول، بالای جدول.</summary>
    public BandDefinition? ReportHeader { get; set; }

    /// <summary>بالای همهٔ صفحات.</summary>
    public BandDefinition? PageHeader { get; set; }

    public TableDefinition? Table { get; set; }

    /// <summary>بعد از پایان جدول (خلاصه، امضا و ...).</summary>
    public BandDefinition? ReportFooter { get; set; }

    /// <summary>پایین همهٔ صفحات.</summary>
    public BandDefinition? PageFooter { get; set; }

    public WatermarkDefinition? Watermark { get; set; }

    /// <summary>پارامترهای گزارش (برای Designer و داده‌ی نمونه).</summary>
    public List<FieldDefinition> Parameters { get; set; } = new();

    /// <summary>فیلدهای هر ردیف داده (برای Designer و داده‌ی نمونه).</summary>
    public List<FieldDefinition> Fields { get; set; } = new();

    // ── JSON ────────────────────────────────────────────────────────

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        return o;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReportTemplate FromJson(string json) =>
        JsonSerializer.Deserialize<ReportTemplate>(json, JsonOptions) ?? throw new InvalidDataException("Invalid template JSON.");

    public static ReportTemplate Load(string path) => FromJson(File.ReadAllText(path));

    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>کپی عمیق (برای تغییر ستون‌ها در زمان اجرا بدون تغییر قالب کش‌شده).</summary>
    public ReportTemplate Clone() => FromJson(ToJson());
}

public sealed class PageSettings
{
    /// <summary>A3 | A4 | A5 | Letter | Legal | Custom</summary>
    public string Size { get; set; } = "A4";
    public bool Landscape { get; set; }
    /// <summary>فقط برای Size = Custom.</summary>
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double MarginLeft { get; set; } = 20;
    public double MarginTop { get; set; } = 20;
    public double MarginRight { get; set; } = 20;
    public double MarginBottom { get; set; } = 20;

    public PageSize ToPageSize()
    {
        if (string.Equals(Size, "Custom", StringComparison.OrdinalIgnoreCase) && Width > 0 && Height > 0)
            return new PageSize(Width.Value, Height.Value);
        return PageSize.FromName(Size, Landscape);
    }
}

public sealed class FontDefinition
{
    /// <summary>نام منطقی که در سبک‌ها استفاده می‌شود.</summary>
    public string Name { get; set; } = "";
    /// <summary>مسیر فایل ttf (نسبی به پوشهٔ فونت‌ها یا مطلق).</summary>
    public string Path { get; set; } = "";
    /// <summary>مسیر نسخهٔ Bold (اختیاری). اگر نباشد Bold به صورت مصنوعی ساخته می‌شود.</summary>
    public string? BoldPath { get; set; }
    /// <summary>نام فونت جایگزین برای کاراکترهای ناموجود (مثلا لاتین در B Nazanin).</summary>
    public string? Fallback { get; set; }
}

/// <summary>سبک قابل ارث‌بری؛ مقادیر null از سبک والد گرفته می‌شوند.</summary>
public sealed class StyleDefinition
{
    /// <summary>نام سبک پایه.</summary>
    public string? BasedOn { get; set; }
    public string? Font { get; set; }
    public double? FontSize { get; set; }
    public bool? Bold { get; set; }
    public bool? Underline { get; set; }
    /// <summary>#RRGGBB</summary>
    public string? Color { get; set; }
    public string? Background { get; set; }
    /// <summary>Start | End | Left | Center | Right</summary>
    public string? Align { get; set; }
    /// <summary>Top | Middle | Bottom</summary>
    public string? VAlign { get; set; }
    /// <summary>Shrink | Wrap | Clip | Ellipsis | Visible</summary>
    public string? Overflow { get; set; }
    /// <summary>Auto | LeftToRight | RightToLeft</summary>
    public string? Direction { get; set; }
    /// <summary>Latin | Persian | Arabic</summary>
    public string? Digits { get; set; }
    public double? Padding { get; set; }
    public double? MinFontSize { get; set; }
    public double? LineSpacing { get; set; }
    public BorderDefinition? Border { get; set; }
}

public sealed class BorderDefinition
{
    public string Color { get; set; } = "#000000";
    public double Width { get; set; } = 0.5;
    /// <summary>ترکیبی از L T R B (مثلا "LTRB" یا "B")؛ خالی یعنی همه.</summary>
    public string? Sides { get; set; }
    public bool Dashed { get; set; }
    public double Radius { get; set; }
}

public sealed class BandDefinition
{
    public double Height { get; set; } = 40;
    public string? Background { get; set; }
    /// <summary>برای PageHeader: در صفحهٔ اول چاپ نشود (وقتی ReportHeader دارید).</summary>
    public bool SkipFirstPage { get; set; }
    public List<ElementDefinition> Elements { get; set; } = new();
}

public sealed class ElementDefinition
{
    /// <summary>Text | Line | Rect | Image</summary>
    public string Type { get; set; } = "Text";
    public string? Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 16;

    /// <summary>متن با عبارت‌های {Field} ، {Field:format} ، {PageNumber} ، {TotalPages} ، {Sum(Field)} ...</summary>
    public string? Text { get; set; }

    /// <summary>نام سبک نام‌دار.</summary>
    public string? Style { get; set; }

    // سبک درون‌خطی (بر سبک نام‌دار اولویت دارد)
    public string? Font { get; set; }
    public double? FontSize { get; set; }
    public bool? Bold { get; set; }
    public bool? Underline { get; set; }
    public string? Color { get; set; }
    public string? Background { get; set; }
    public string? Align { get; set; }
    public string? VAlign { get; set; }
    public string? Overflow { get; set; }
    public string? Direction { get; set; }
    public string? Digits { get; set; }
    public double? Padding { get; set; }
    public BorderDefinition? Border { get; set; }

    /// <summary>برای Line/Rect.</summary>
    public string? LineColor { get; set; }
    public double? LineWidth { get; set; }
    public bool Dashed { get; set; }
    public double CornerRadius { get; set; }

    /// <summary>برای Image: مسیر فایل، data URI یا {Parameter} (byte[] یا base64).</summary>
    public string? Source { get; set; }
    public bool KeepAspectRatio { get; set; } = true;

    /// <summary>اگر عبارت خالی/false/0 شود عنصر رسم نمی‌شود. مثال: "{ShowLogo}"</summary>
    public string? VisibleWhen { get; set; }

    /// <summary>
    /// وقتی عنصر با VisibleWhen پنهان شود، این مقدار از ارتفاع باند کم و عناصر پایین‌تر به همین اندازه بالا می‌روند
    /// (مثلا کادر «فیلتر اعمال‌شده» که فقط گاهی وجود دارد).
    /// </summary>
    public double CollapseHeight { get; set; }
}

public sealed class TableDefinition
{
    public List<ColumnDefinition> Columns { get; set; } = new();

    public double HeaderHeight { get; set; } = 20;
    /// <summary>ارتفاع (حداقل) هر ردیف.</summary>
    public double RowHeight { get; set; } = 16;

    /// <summary>ارتفاع سطرهای دوم به بعد سرجدول در جدول چندسطری؛ ۰ یعنی پنهان (مثلا برای سطر جزئیات). null یعنی HeaderHeight.</summary>
    public double? SubHeaderHeight { get; set; }

    /// <summary>ارتفاع سطرهای دوم به بعد یک رکورد چندسطری (مثلا سطر جزئیات با فونت ریزتر). null یعنی RowHeight.</summary>
    public double? SubLineHeight { get; set; }
    public bool RepeatHeader { get; set; } = true;
    /// <summary>کش آمدن ستون‌ها تا پهنای کامل صفحه.</summary>
    public bool FitWidth { get; set; } = true;

    /// <summary>
    /// اگر تعداد ستون‌های قابل‌مشاهده بیشتر از این عدد باشد، هر رکورد در چند سطر چیده می‌شود
    /// (مثلا ۱۲ ← با ۲۰ ستون، دو سطر ۱۰ ستونی). صفر یعنی همیشه یک سطر.
    /// </summary>
    public int MaxColumnsPerLine { get; set; }

    /// <summary>
    /// ستون‌هایی که در کل داده‌ی این گزارش خالی‌اند حذف شوند و بقیه پهن‌تر شوند.
    /// فقط وقتی داده از قبل در حافظه است (List/Array/ICollection) اعمال می‌شود، چون یک بار پیمایش اضافه لازم دارد.
    /// ستون‌های ردیف، جمع‌دار و عبارت‌ها هیچ‌وقت حذف نمی‌شوند.
    /// </summary>
    public bool HideEmptyColumns { get; set; }

    public string? HeaderStyle { get; set; }
    public string? RowStyle { get; set; }
    public string? TotalsStyle { get; set; }

    public string? HeaderBackground { get; set; } = "#D9E1F2";
    public string? AltRowBackground { get; set; } = "#F5F7FA";
    public string? TotalsBackground { get; set; } = "#E7E6E6";

    public string BorderColor { get; set; } = "#9E9E9E";
    public double BorderWidth { get; set; } = 0.5;
    public bool VerticalLines { get; set; } = true;
    public bool HorizontalLines { get; set; } = true;

    /// <summary>ردیف «جمع صفحه» در پایین هر صفحه.</summary>
    public bool ShowPageTotals { get; set; }
    public string PageTotalsLabel { get; set; } = "جمع صفحه";

    /// <summary>ردیف «جمع کل» در انتهای جدول.</summary>
    public bool ShowGrandTotals { get; set; }
    public string GrandTotalsLabel { get; set; } = "جمع کل";

    /// <summary>ردیف «نقل از صفحه قبل» در بالای صفحات دوم به بعد.</summary>
    public bool ShowCarryForward { get; set; }
    public string CarryForwardLabel { get; set; } = "نقل از صفحه قبل";

    /// <summary>متن نمایش داده شده وقتی داده‌ای وجود ندارد.</summary>
    public string? EmptyText { get; set; } = "داده‌ای برای نمایش وجود ندارد";

    /// <summary>
    /// نام فیلد گروه‌بندی (اختیاری). با تغییر مقدار، یک ردیف عنوان گروه رسم می‌شود
    /// (داده باید بر اساس این فیلد مرتب باشد).
    /// </summary>
    public string? GroupBy { get; set; }
    /// <summary>متن عنوان گروه، مثلا "شعبه: {BranchName}"</summary>
    public string? GroupHeaderText { get; set; }
    public string? GroupHeaderBackground { get; set; } = "#EDEDED";
    public bool ShowGroupTotals { get; set; }
    public string GroupTotalsLabel { get; set; } = "جمع گروه";
}

public sealed class ColumnDefinition
{
    public string Header { get; set; } = "";
    /// <summary>نام فیلد یا عبارت، مثل "Amount" یا "{Date:mask:####/##/##}" یا "{RowNumber}".</summary>
    public string Field { get; set; } = "";
    public double Width { get; set; } = 60;
    /// <summary>فرمت مقدار: N0 ، N2 ، #,0.## ، yyyy/MM/dd ، persian ، mask:####/##/## ...</summary>
    public string? Format { get; set; }
    public string? Align { get; set; }
    public string? HeaderAlign { get; set; }
    /// <summary>None | Sum | Count | Avg | Min | Max</summary>
    public string? Aggregate { get; set; }
    public string? Style { get; set; }
    public string? Font { get; set; }
    public double? FontSize { get; set; }
    public bool? Bold { get; set; }
    public string? Color { get; set; }
    /// <summary>رنگ متن برای اعداد منفی.</summary>
    public string? NegativeColor { get; set; }
    public string? Overflow { get; set; }
    public string? Direction { get; set; }
    public bool Visible { get; set; } = true;
    /// <summary>شمارهٔ سطر (۰، ۱، ...) در جدول‌های چندسطری؛ null یعنی خودکار.</summary>
    public int? Line { get; set; }
    /// <summary>
    /// پهنای ثابت (point): با fitWidth کش نمی‌آید و فضای اضافه به ستون‌های دیگر می‌رسد.
    /// مناسب ستون‌هایی با طول ثابت مثل تاریخ، ساعت و ردیف.
    /// </summary>
    public bool FixedWidth { get; set; }
    /// <summary>در جدول چندسطری، این ستون تمام ارتفاع رکورد را می‌گیرد (مثلا «ردیف» یا «تاریخ»).</summary>
    public bool SpanLines { get; set; }
    /// <summary>
    /// ستون «جزئیات»: فقط فیلدهای غیرخالی به صورت «عنوان: مقدار» کنار هم نوشته و در صورت نیاز شکسته می‌شوند.
    /// برای فیلدهای کم‌مقدار (شبا، کارت، پیگیری، ...) به جای ده‌ها ستون خالی.
    /// </summary>
    public List<DetailField>? Details { get; set; }
    /// <summary>جداکنندهٔ آیتم‌های ستون جزئیات.</summary>
    public string? DetailSeparator { get; set; }
}

/// <summary>یک آیتم ستون جزئیات.</summary>
public sealed class DetailField
{
    public string Label { get; set; } = "";
    public string Field { get; set; } = "";
    public string? Format { get; set; }
}

public sealed class WatermarkDefinition
{
    public string Text { get; set; } = "";
    public string? Font { get; set; }
    public double FontSize { get; set; } = 60;
    public string Color { get; set; } = "#C00000";
    public double Opacity { get; set; } = 0.12;
    public double Angle { get; set; } = 45;
}

/// <summary>توضیح یک فیلد یا پارامتر برای Designer.</summary>
public sealed class FieldDefinition
{
    public string Name { get; set; } = "";
    /// <summary>عنوان فارسی</summary>
    public string? Title { get; set; }
    /// <summary>string | number | date | bool | image</summary>
    public string Type { get; set; } = "string";
    /// <summary>مقدار نمونه برای پیش‌نمایش.</summary>
    public string? Sample { get; set; }
}
