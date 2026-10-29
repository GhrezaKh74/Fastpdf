using FastPdfWriterStream.Text;

namespace FastPdfWriterStream.Templates;

/// <summary>نتیجهٔ تولید گزارش.</summary>
public sealed record RenderResult(int PageCount, long RowCount, TimeSpan Elapsed);

/// <summary>
/// تولیدکنندهٔ جریانی گزارش از روی قالب: ردیف‌ها یکی‌یکی با <see cref="AddRow"/> اضافه می‌شوند و
/// هر صفحه به محض پر شدن روی خروجی نوشته می‌شود (حافظهٔ ثابت حتی برای میلیون‌ها ردیف).
/// </summary>
public sealed class ReportRenderer<T> : IDisposable, ICompileContext<T>, IEvalContext<T>
{
    private readonly ReportTemplate _t;
    private readonly PdfWriter _pdf;
    private readonly Dictionary<string, object?> _params;
    private readonly ReportEngine _engine;
    private readonly CancellationToken _ct;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    private readonly Dictionary<string, (PdfFont Regular, PdfFont? Bold)> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly PdfFont _defaultFont;
    private readonly StyleResolver _styles;
    private readonly Dictionary<string, PdfImage?> _images = new(StringComparer.Ordinal);

    // هندسه
    private readonly PageSize _size;
    private readonly double _left, _contentW, _top, _pageBottom;

    // باندها
    private readonly CompiledBand? _pageHeader, _reportHeader, _reportFooter, _pageFooter;

    // جدول
    private readonly TableDefinition? _table;
    private readonly Col[] _cols = Array.Empty<Col>();
    private readonly double _tableLeft, _tableRight;
    private readonly bool _hasWrap;
    private readonly int _lineCount = 1;      // تعداد سطر هر رکورد (برای ستون‌های زیاد)
    private readonly double[] _lineH = new double[1];
    private readonly ResolvedStyle? _headerStyle, _rowStyle, _totalsStyle, _groupStyle;
    private readonly PdfColor? _headerBg, _altBg, _totalsBg, _groupBg;
    private readonly PdfColor _borderColor;
    private readonly FieldGetter<T>? _groupGetter;
    private readonly TextTemplate<T>? _groupText;

    // جمع‌ها
    private readonly List<string> _aggFields = new();
    private readonly List<FieldGetter<T>?> _aggGetters = new();
    private AggAcc[] _aggReport = Array.Empty<AggAcc>(), _aggPage = Array.Empty<AggAcc>(), _aggGroup = Array.Empty<AggAcc>();

    // وضعیت
    private PdfPage? _page;
    private double _y;
    private long _rowNumber;
    private bool _started, _completed, _tableOpen, _rowsPhase = true;
    private double _tableTop, _segStart;
    private readonly List<(double Start, double End)> _segments = new();
    private readonly List<double> _hLines = new();
    private readonly List<(double X, double Y1, double Y2)> _extraV = new();
    private readonly List<double> _extraH = new();
    private char[] _groupKey = new char[64];
    private int _groupKeyLen = -1;
    private char[] _buf = new char[128];
    private bool _altRow;

    internal ReportRenderer(ReportEngine engine, ReportTemplate template, Stream output, object? parameters,
        long? expectedRows, CancellationToken ct, bool leaveOpen)
    {
        _engine = engine;
        _t = template;
        _ct = ct;
        _params = ParameterReader.Read(parameters);
        _size = template.Page.ToPageSize();

        var opt = engine.Options;
        _pdf = new PdfWriter(output, leaveOpen, new PdfWriterOptions
        {
            PageSize = _size,
            RightToLeft = template.RightToLeft,
            Digits = Parse.Digits(template.Digits) ?? DigitStyle.Latin,
            Compress = opt.Compress,
            CompressionLevel = opt.CompressionLevel,
            ParallelCompression = opt.ParallelCompression,
            Title = template.Name,
            Creator = "FastPdfWriterStream Report Engine"
        });

        try
        {
            // ── فونت‌ها ──
            foreach (var fd in template.Fonts)
            {
                if (string.IsNullOrWhiteSpace(fd.Name) || string.IsNullOrWhiteSpace(fd.Path)) continue;
                var regular = _pdf.AddFont(engine.GetFont(fd.Path), fd.Name);
                PdfFont? bold = null;
                if (!string.IsNullOrWhiteSpace(fd.BoldPath)) bold = _pdf.AddFont(engine.GetFont(fd.BoldPath), fd.Name + " Bold");
                _fonts[fd.Name] = (regular, bold);
            }
            if (_fonts.Count == 0)
            {
                if (string.IsNullOrEmpty(opt.DefaultFontPath))
                    throw new InvalidOperationException("Template has no fonts and ReportEngineOptions.DefaultFontPath is not set.");
                _fonts["Default"] = (_pdf.AddFont(engine.GetFont(opt.DefaultFontPath), "Default"),
                    string.IsNullOrEmpty(opt.DefaultBoldFontPath) ? null : _pdf.AddFont(engine.GetFont(opt.DefaultBoldFontPath), "Default Bold"));
            }
            foreach (var fd in template.Fonts)
            {
                if (string.IsNullOrWhiteSpace(fd.Fallback) || !_fonts.TryGetValue(fd.Name, out var f)) continue;
                if (!_fonts.TryGetValue(fd.Fallback, out var fb)) continue;
                f.Regular.AddFallback(fb.Regular);
                f.Bold?.AddFallback(fb.Bold ?? fb.Regular);
            }
            _defaultFont = (template.DefaultFont != null && _fonts.TryGetValue(template.DefaultFont, out var df) ? df : _fonts.Values.First()).Regular;
            _styles = new StyleResolver(template, LookupFont);

            // ── هندسه ──
            var pg = template.Page;
            _left = pg.MarginLeft;
            _contentW = _size.Width - pg.MarginLeft - pg.MarginRight;
            _top = pg.MarginTop;
            _pageBottom = _size.Height - pg.MarginBottom;

            // ── جدول ──
            _table = template.Table;
            if (_table != null)
            {
                _headerStyle = _styles.Build(_styles.Start(_table.HeaderStyle, a => { a.Bold = true; a.Align = "Center"; }));
                _rowStyle = _styles.Build(_styles.Start(_table.RowStyle));
                _totalsStyle = _styles.Build(_styles.Start(_table.TotalsStyle, a => { a.Bold = true; }));
                _groupStyle = _styles.Build(_styles.Start(_table.RowStyle, a => { a.Bold = true; }));
                _headerBg = Color(_table.HeaderBackground) ?? _headerStyle.Background;
                _altBg = Color(_table.AltRowBackground);
                _totalsBg = Color(_table.TotalsBackground) ?? _totalsStyle.Background;
                _groupBg = Color(_table.GroupHeaderBackground);
                _borderColor = Color(_table.BorderColor) ?? PdfColor.Gray;

                var visible = _table.Columns.Where(c => c.Visible).ToList();

                // تقسیم ستون‌ها به چند سطر (مانند گزارش پویا با بیش از ۱۲ ستون)
                var lineOf = new int[visible.Count];
                if (visible.Any(c => c.Line > 0))
                    for (int i = 0; i < visible.Count; i++) lineOf[i] = Math.Max(0, visible[i].Line ?? 0);
                else if (_table.MaxColumnsPerLine > 0 && visible.Count > _table.MaxColumnsPerLine)
                {
                    int lines = (int)Math.Ceiling(visible.Count / (double)_table.MaxColumnsPerLine);
                    int perLine = (int)Math.Ceiling(visible.Count / (double)lines);
                    for (int i = 0; i < visible.Count; i++) lineOf[i] = i / perLine;
                }
                _lineCount = visible.Count == 0 ? 1 : lineOf.Max() + 1;
                _lineH = new double[_lineCount];
                var sums = new double[_lineCount];
                for (int i = 0; i < visible.Count; i++) sums[lineOf[i]] += Math.Max(1, visible[i].Width);
                double maxSum = Math.Max(1, sums.Max());
                double tableW = _table.FitWidth ? _contentW : Math.Min(_contentW, maxSum);
                _tableLeft = template.RightToLeft ? _left + _contentW - tableW : _left;
                _tableRight = _tableLeft + tableW;

                _cols = new Col[visible.Count];
                var cursors = Enumerable.Repeat(template.RightToLeft ? _tableRight : _tableLeft, _lineCount).ToArray();
                for (int i = 0; i < visible.Count; i++)
                {
                    var cd = visible[i];
                    int line = lineOf[i];
                    double scale = _table.FitWidth ? tableW / Math.Max(1, sums[line]) : tableW / maxSum;
                    var col = new Col { Def = cd, Line = line, W = Math.Max(1, cd.Width) * scale };
                    if (template.RightToLeft) { cursors[line] -= col.W; col.X = cursors[line]; }
                    else { col.X = cursors[line]; cursors[line] += col.W; }

                    col.Binding = TextTemplate<T>.Parse(cd.Field, this, bareField: true, defaultFormat: cd.Format);
                    if (col.Binding.Segments.Length == 1)
                    {
                        var s = col.Binding.Segments[0];
                        if (s.Kind == SegmentKind.Field) { col.Getter = s.Getter; col.Spec = s.Spec; }
                        else if (s.Kind == SegmentKind.RowNumber) { col.IsRowNumber = true; col.Spec = s.Spec; }
                    }
                    col.Header = TextTemplate<T>.Parse(cd.Header, this);

                    var acc = _styles.Start(_table.RowStyle);
                    _styles.ApplyNamed(acc, cd.Style);
                    _styles.ApplyInline(acc, cd);
                    col.Style = _styles.Build(acc);
                    col.Wrap = col.Style.Text.Overflow == TextOverflow.Wrap;
                    if (PdfColor.TryParse(cd.NegativeColor, out var neg))
                        col.NegativeStyle = col.Style.Text.With(s => s.Color = neg);

                    var hacc = _styles.Start(_table.HeaderStyle, a => { a.Bold = true; a.Align = "Center"; });
                    if (cd.HeaderAlign != null) hacc.Align = cd.HeaderAlign;
                    hacc.Overflow ??= "Shrink";
                    col.HeaderStyle = _styles.Build(hacc).Text;

                    var tacc = _styles.Start(_table.TotalsStyle, a => { a.Bold = true; });
                    tacc.Align ??= cd.Align;
                    tacc.Overflow ??= "Shrink";
                    col.TotalsStyle = _styles.Build(tacc).Text;

                    col.Agg = Parse.Aggregate(cd.Aggregate);
                    if (col.Agg != AggregateKind.None)
                    {
                        string? aggField = col.Getter != null ? col.Binding.Segments[0].Text
                            : col.Binding.Segments.FirstOrDefault(s => s.Kind == SegmentKind.Field)?.Text;
                        col.AggIndex = RegisterAggregate(col.IsRowNumber ? "" : aggField ?? "");
                    }
                    _cols[i] = col;
                }
                _hasWrap = _cols.Any(c => c.Wrap);
                if (!string.IsNullOrWhiteSpace(_table.GroupBy))
                {
                    _groupGetter = FieldAccess.Create<T>(_table.GroupBy);
                    _groupText = TextTemplate<T>.Parse(_table.GroupHeaderText ?? "{" + _table.GroupBy + "}", this, bareField: false);
                }
            }

            _pageHeader = CompileBand(template.PageHeader);
            _reportHeader = CompileBand(template.ReportHeader);
            _reportFooter = CompileBand(template.ReportFooter);
            _pageFooter = CompileBand(template.PageFooter);

            _aggReport = new AggAcc[_aggFields.Count];
            _aggPage = new AggAcc[_aggFields.Count];
            _aggGroup = new AggAcc[_aggFields.Count];
            for (int i = 0; i < _aggFields.Count; i++) { _aggReport[i].Reset(); _aggPage[i].Reset(); _aggGroup[i].Reset(); }

            if (expectedRows is > 0 && _table != null)
            {
                double usable = ContentBottom - _top - (_pageHeader?.Height ?? 0) - _table.HeaderHeight * _lineCount;
                double perPage = Math.Max(1, Math.Floor(usable / Math.Max(1, _table.RowHeight * _lineCount)));
                long pages = (long)Math.Ceiling(expectedRows.Value / perPage) + 1;
                _pdf.Options.TotalPagesDigits = pages.ToString().Length;
            }
            else _pdf.Options.TotalPagesDigits = 3;
        }
        catch
        {
            _pdf.Abort();
            throw;
        }
    }

    private static PdfColor? Color(string? hex) => PdfColor.TryParse(hex, out var c) ? c : null;

    private (PdfFont, bool) LookupFont(string? name, bool bold)
    {
        var f = name != null && _fonts.TryGetValue(name, out var ff) ? ff
            : _fonts.TryGetValue(_t.DefaultFont ?? "", out var d) ? d : _fonts.Values.First();
        if (bold) return f.Bold != null ? (f.Bold, false) : (f.Regular, true);
        return (f.Regular, false);
    }

    // ── ICompileContext / IEvalContext ──────────────────────────────

    bool ICompileContext<T>.HasParameter(string name) => _params.ContainsKey(name);

    FieldGetter<T>? ICompileContext<T>.GetField(string name) =>
        typeof(T) == typeof(NoRow) ? null : FieldAccess.Create<T>(name);

    public int RegisterAggregate(string field)
    {
        for (int i = 0; i < _aggFields.Count; i++)
            if (string.Equals(_aggFields[i], field, StringComparison.OrdinalIgnoreCase)) return i;
        _aggFields.Add(field);
        _aggGetters.Add(field.Length == 0 || typeof(T) == typeof(NoRow) ? null : FieldAccess.Create<T>(field));
        return _aggFields.Count - 1;
    }

    int IEvalContext<T>.PageNumber => _page?.Number ?? 1;
    long IEvalContext<T>.RowNumber => _rowNumber;
    long IEvalContext<T>.RowCount => _rowNumber;
    string IEvalContext<T>.ReportName => _t.Name;

    object? IEvalContext<T>.GetParameter(string name, out bool found)
    {
        found = _params.TryGetValue(name, out var v);
        return v;
    }

    decimal? IEvalContext<T>.GetAggregate(int index, AggregateKind kind, AggregateScope scope)
    {
        if (index < 0 || index >= _aggReport.Length) return null;
        var arr = scope switch { AggregateScope.Page => _aggPage, AggregateScope.Group => _aggGroup, _ => _aggReport };
        return arr[index].Get(kind);
    }

    /// <summary>تعداد صفحات تولیدشده تاکنون.</summary>
    public int PageCount => _pdf.PageCount;

    /// <summary>تعداد ردیف‌های اضافه‌شده.</summary>
    public long RowCount => _rowNumber;

    private double ContentBottom => _pageBottom - (_pageFooter?.Height ?? 0);
    private double TotalsHeight => _table == null ? 0 : Math.Max(_table.RowHeight, 14);
    private double TableBottom => ContentBottom - (_table?.ShowPageTotals == true ? TotalsHeight : 0);

    // ── ردیف‌ها ────────────────────────────────────────────────────

    /// <summary>افزودن یک ردیف داده.</summary>
    public void AddRow(T row)
    {
        if (_completed) throw new InvalidOperationException("Report already completed.");
        if (!_started) StartReport();
        if ((_rowNumber & 255) == 0) _ct.ThrowIfCancellationRequested();
        if (_table == null) { _rowNumber++; Accumulate(row); return; }

        // گروه‌بندی
        if (_groupGetter != null)
        {
            int len = _groupGetter.Write(row, FormatSpec.Default, ref _buf);
            var key = _buf.AsSpan(0, len);
            if (_groupKeyLen < 0 || !key.SequenceEqual(_groupKey.AsSpan(0, _groupKeyLen)))
            {
                if (_groupKeyLen >= 0 && _table.ShowGroupTotals) DrawTotalsRow(_table.GroupTotalsLabel, AggregateScope.Group);
                for (int i = 0; i < _aggGroup.Length; i++) _aggGroup[i].Reset();
                if (_groupKey.Length < len) _groupKey = new char[len * 2];
                key.CopyTo(_groupKey);
                _groupKeyLen = len;
                var text = _groupText!.Evaluate(this, row, true, ref _buf);
                DrawFullRow(text, _groupStyle!.Text, _groupBg, Math.Max(_table.RowHeight, 14), keepWithNext: true);
            }
        }

        _rowNumber++;
        var cols = _cols;
        for (int i = 0; i < cols.Length; i++) FormatCell(cols[i], row);

        for (int l = 0; l < _lineCount; l++) _lineH[l] = _table.RowHeight;
        if (_hasWrap)
        {
            double maxLine = (TableBottom - _top - _table.HeaderHeight * _lineCount - 1) / _lineCount;
            foreach (var c in cols)
                if (c.Wrap && c.Len > 0)
                    _lineH[c.Line] = Math.Min(maxLine, Math.Max(_lineH[c.Line],
                        PdfPage.MeasureTextHeight(_pdf, c.Buf.AsSpan(0, c.Len), c.W, c.Style.Text)));
        }
        double h = 0;
        for (int l = 0; l < _lineCount; l++) h += _lineH[l];
        EnsureSpace(h);

        var page = _page!;
        if (_altBg is { } alt && _altRow) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, h, alt);
        _altRow = !_altRow;
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c.Len == 0) continue;
            double cy = _y, ch = _lineH[c.Line];
            for (int l = 0; l < c.Line; l++) cy += _lineH[l];
            var style = c.NegativeStyle != null && (c.Buf[0] == '-' || c.Buf[0] == '(') ? c.NegativeStyle : c.Style.Text;
            page.DrawText(c.Buf.AsSpan(0, c.Len), c.X, cy, c.W, ch, style);
        }
        if (_lineCount > 1) AddLineVerticals(_y, _lineH);
        _y += h;
        if (_table.HorizontalLines) _hLines.Add(_y);
        Accumulate(row);
    }

    private void FormatCell(Col c, T row)
    {
        if (c.Getter != null) c.Len = c.Getter.Write(row, c.Spec, ref c.Buf);
        else if (c.IsRowNumber) c.Len = ValueFormatter.WriteDecimal(_rowNumber, c.Spec, ref c.Buf);
        else if (c.Binding.IsConstant) { c.Len = 0; CopyTo(c, c.Binding.ConstantText); }
        else CopyTo(c, c.Binding.Evaluate(this, row, true, ref _buf));
    }

    private static void CopyTo(Col c, string? s)
    {
        if (string.IsNullOrEmpty(s)) { c.Len = 0; return; }
        if (c.Buf.Length < s.Length) c.Buf = new char[s.Length * 2];
        s.CopyTo(0, c.Buf, 0, s.Length);
        c.Len = s.Length;
    }

    private void Accumulate(T row)
    {
        for (int i = 0; i < _aggGetters.Count; i++)
        {
            var g = _aggGetters[i];
            decimal? v = g == null ? 1 : g.GetNumber(row);
            if (!v.HasValue) continue;
            _aggReport[i].Add(v.Value);
            _aggPage[i].Add(v.Value);
            _aggGroup[i].Add(v.Value);
        }
    }

    private void EnsureSpace(double h)
    {
        if (_y + h <= TableBottom + 0.01) return;
        // اگر صفحه تازه است و باز هم جا نمی‌شود، همین‌جا رسم می‌کنیم (جلوگیری از حلقهٔ بی‌نهایت)
        if (_tableOpen && Math.Abs(_y - _segStartAfterHeader) < 0.01) return;
        BreakPage();
    }

    private double _segStartAfterHeader;

    private void BreakPage()
    {
        CloseTablePage(final: false);
        EndPage();
        StartPage();
    }

    // ── صفحات ───────────────────────────────────────────────────────

    private void StartReport()
    {
        _started = true;
        StartPage();
    }

    private void StartPage()
    {
        _page = _pdf.NewPage(_size);
        for (int i = 0; i < _aggPage.Length; i++) _aggPage[i].Reset();
        _y = _top;
        _altRow = false;

        if (_t.Watermark is { Text.Length: > 0 } wm) DrawWatermark(wm);
        if (_pageHeader != null && !(_pageHeader.Def.SkipFirstPage && _page.Number == 1))
        {
            DrawBand(_pageHeader, _y);
            _y += _pageHeader.Height;
        }
        if (_page.Number == 1 && _reportHeader != null)
        {
            DrawBand(_reportHeader, _y);
            _y += _reportHeader.Height;
        }
        if (_table != null && _rowsPhase) OpenTable();
    }

    private void OpenTable()
    {
        var t = _table!;
        _tableOpen = true;
        _tableTop = _y;
        _segments.Clear();
        _hLines.Clear();
        _extraV.Clear();
        _extraH.Clear();
        bool firstTablePage = _rowNumber == 0;
        if (firstTablePage || t.RepeatHeader)
        {
            var page = _page!;
            double hh = t.HeaderHeight * _lineCount;
            if (_headerBg is { } hb) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, hh, hb);
            foreach (var c in _cols)
            {
                var text = c.Header.Evaluate(this, default, false, ref _buf);
                page.DrawText(text, c.X, _y + c.Line * t.HeaderHeight, c.W, t.HeaderHeight, c.HeaderStyle);
            }
            if (_lineCount > 1)
            {
                for (int l = 0; l < _lineCount; l++) _lineH[l] = t.HeaderHeight;
                AddLineVerticals(_y, _lineH);
                for (int l = 1; l < _lineCount; l++) _extraH.Add(_y + l * t.HeaderHeight);
            }
            _y += hh;
            _hLines.Add(_y);
        }
        _segStart = _tableTop;
        if (!firstTablePage && t.ShowCarryForward) DrawTotalsRow(t.CarryForwardLabel, AggregateScope.Report);
        _segStartAfterHeader = _y;
    }

    /// <summary>بستن بخش جدول در صفحهٔ جاری (ردیف جمع صفحه + خطوط جدول).</summary>
    private void CloseTablePage(bool final)
    {
        if (!_tableOpen) return;
        var t = _table!;
        if (t.ShowPageTotals) DrawTotalsRow(t.PageTotalsLabel, AggregateScope.Page);
        if (final && t.ShowGrandTotals) DrawTotalsRow(t.GrandTotalsLabel, AggregateScope.Report);
        _segments.Add((_segStart, _y));

        var page = _page!;
        page.BeginPath(_borderColor, t.BorderWidth);
        foreach (var hy in _hLines) page.PathLine(_tableLeft, hy, _tableRight, hy);
        page.PathLine(_tableLeft, _tableTop, _tableRight, _tableTop);
        page.PathLine(_tableLeft, _y, _tableRight, _y);
        page.PathLine(_tableLeft, _tableTop, _tableLeft, _y);
        page.PathLine(_tableRight, _tableTop, _tableRight, _y);
        foreach (var hy in _extraH) page.PathLine(_tableLeft, hy, _tableRight, hy);
        if (t.VerticalLines && _lineCount == 1)
        {
            foreach (var (s, e) in _segments)
            {
                if (e - s < 0.01) continue;
                for (int i = 0; i < _cols.Length; i++)
                {
                    double x = _t.RightToLeft ? _cols[i].X : _cols[i].X + _cols[i].W;
                    if (Math.Abs(x - _tableLeft) < 0.01 || Math.Abs(x - _tableRight) < 0.01) continue;
                    page.PathLine(x, s, x, e);
                }
            }
        }
        foreach (var (x, y1, y2) in _extraV) page.PathLine(x, y1, x, y2);
        page.StrokePath();
        _tableOpen = false;
    }

    private void EndPage()
    {
        if (_pageFooter != null) DrawBand(_pageFooter, _pageBottom - _pageFooter.Height);
        _pdf.FinishPage();
    }

    /// <summary>ردیف تمام‌عرض (عنوان گروه، متن «داده‌ای وجود ندارد»).</summary>
    private void DrawFullRow(string text, TextStyle style, PdfColor? bg, double h, bool keepWithNext = false)
    {
        EnsureSpace(h + (keepWithNext ? _table!.RowHeight * _lineCount : 0));
        _segments.Add((_segStart, _y));
        var page = _page!;
        if (bg is { } b) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, h, b);
        page.DrawText(text, _tableLeft, _y, _tableRight - _tableLeft, h, style);
        _y += h;
        _hLines.Add(_y);
        _segStart = _y;
    }

    /// <summary>ردیف جمع: برچسب در ستون‌های ابتدایی (تا اولین ستون دارای جمع) و مقادیر زیر ستون‌ها.</summary>
    private void DrawTotalsRow(string label, AggregateScope scope)
    {
        var t = _table!;
        double lh = TotalsHeight;
        double h = lh * _lineCount;
        if (scope != AggregateScope.Page && _y + h > TableBottom + 0.01) BreakPage();
        _segments.Add((_segStart, _y));
        var page = _page!;
        if (_totalsBg is { } bg) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, h, bg);
        var arr = scope switch { AggregateScope.Page => _aggPage, AggregateScope.Group => _aggGroup, _ => _aggReport };

        for (int line = 0; line < _lineCount; line++)
        {
            double y = _y + line * lh;
            var cols = _cols.Where(c => c.Line == line).ToList();
            int span = 0;
            while (span < cols.Count && cols[span].Agg == AggregateKind.None) span++;
            if (span > 0)
            {
                double x1 = double.MaxValue, x2 = double.MinValue;
                for (int i = 0; i < span; i++) { x1 = Math.Min(x1, cols[i].X); x2 = Math.Max(x2, cols[i].X + cols[i].W); }
                if (line == 0 && !string.IsNullOrEmpty(label))
                    page.DrawText(label, x1, y, x2 - x1, lh, _totalsStyle!.Text.With(s => s.Align = HAlign.Center));
                if (span < cols.Count) _extraV.Add((_t.RightToLeft ? x1 : x2, y, y + lh));
            }
            for (int i = span; i < cols.Count; i++)
            {
                var c = cols[i];
                if (t.VerticalLines && i < cols.Count - 1)
                    _extraV.Add((_t.RightToLeft ? c.X : c.X + c.W, y, y + lh));
                if (c.Agg == AggregateKind.None || c.AggIndex < 0) continue;
                var v = arr[c.AggIndex].Get(c.Agg);
                if (v == null) continue;
                var spec = c.Agg == AggregateKind.Count ? FormatSpec.Get("N0") : c.Spec;
                int n = ValueFormatter.WriteDecimal(v.Value, spec, ref _buf);
                page.DrawText(_buf.AsSpan(0, n), c.X, y, c.W, lh, c.TotalsStyle);
            }
        }
        _y += h;
        _hLines.Add(_y);
        _segStart = _y;
    }

    /// <summary>خطوط عمودی هر سطر از یک رکورد چندسطری.</summary>
    private void AddLineVerticals(double top, double[] lineHeights)
    {
        if (!_table!.VerticalLines) return;
        double y = top;
        for (int l = 0; l < _lineCount; l++)
        {
            foreach (var c in _cols)
            {
                if (c.Line != l) continue;
                double x = _t.RightToLeft ? c.X : c.X + c.W;
                if (Math.Abs(x - _tableLeft) < 0.01 || Math.Abs(x - _tableRight) < 0.01) continue;
                _extraV.Add((x, y, y + lineHeights[l]));
            }
            y += lineHeights[l];
        }
    }

    /// <summary>پایان گزارش: ردیف‌های جمع، باند پایانی، پاورقی و بستن فایل.</summary>
    public RenderResult Complete()
    {
        if (_completed) throw new InvalidOperationException("Report already completed.");
        if (!_started) StartReport();
        var t = _table;
        if (t != null)
        {
            if (_rowNumber == 0 && !string.IsNullOrEmpty(t.EmptyText))
                DrawFullRow(t.EmptyText, _rowStyle!.Text.With(s => s.Align = HAlign.Center), null, Math.Max(t.RowHeight, 20));
            if (_groupGetter != null && _rowNumber > 0 && t.ShowGroupTotals)
                DrawTotalsRow(t.GroupTotalsLabel, AggregateScope.Group);
            if (t.ShowGrandTotals && _y + TotalsHeight > TableBottom + 0.01) BreakPage();
            CloseTablePage(final: true);
        }
        _rowsPhase = false;
        if (_reportFooter != null)
        {
            if (_y + _reportFooter.Height > ContentBottom + 0.01)
            {
                EndPage();
                StartPage();
            }
            DrawBand(_reportFooter, _y);
            _y += _reportFooter.Height;
        }
        EndPage();
        _pdf.Close();
        _completed = true;
        return new RenderResult(_pdf.PageCount, _rowNumber, _sw.Elapsed);
    }

    public void Dispose()
    {
        if (!_completed) _pdf.Abort();
    }

    // ── باندها ──────────────────────────────────────────────────────

    private sealed class CompiledBand
    {
        public BandDefinition Def = null!;
        public double Height;
        public PdfColor? Background;
        public List<CompiledElement> Elements = new();
    }

    private sealed class CompiledElement
    {
        public ElementDefinition Def = null!;
        public string Kind = "text";
        public TextTemplate<T>? Text;
        public TextTemplate<T>? Visible;
        public ResolvedStyle Style = null!;
        public PdfColor LineColor;
    }

    private CompiledBand? CompileBand(BandDefinition? band)
    {
        if (band == null) return null;
        var cb = new CompiledBand { Def = band, Height = band.Height, Background = Color(band.Background) };
        foreach (var e in band.Elements)
        {
            var acc = _styles.Start(e.Style);
            _styles.ApplyInline(acc, e);
            var ce = new CompiledElement
            {
                Def = e,
                Kind = (e.Type ?? "Text").Trim().ToLowerInvariant(),
                Style = _styles.Build(acc),
                LineColor = Color(e.LineColor) ?? Color(e.Color) ?? PdfColor.Black
            };
            if (ce.Kind == "text" || ce.Kind == "field") ce.Text = TextTemplate<T>.Parse(e.Text, this);
            if (!string.IsNullOrWhiteSpace(e.VisibleWhen)) ce.Visible = TextTemplate<T>.Parse(e.VisibleWhen, this);
            cb.Elements.Add(ce);
        }
        return cb;
    }

    private void DrawBand(CompiledBand band, double top)
    {
        var page = _page!;
        if (band.Background is { } bg) page.FillRect(_left, top, _contentW, band.Height, bg);
        foreach (var e in band.Elements)
        {
            var d = e.Def;
            if (e.Visible != null)
            {
                var v = e.Visible.Evaluate(this, default, false, ref _buf).Trim();
                if (v.Length == 0 || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) continue;
            }
            double x = _left + d.X, y = top + d.Y, w = d.Width, h = d.Height;
            switch (e.Kind)
            {
                case "line":
                    page.DrawLine(x, y, x + w, y + h, e.LineColor, d.LineWidth ?? 0.75, d.Dashed);
                    break;
                case "rect":
                case "rectangle":
                    page.DrawRoundedRect(x, y, w, h, d.CornerRadius,
                        d.LineWidth is 0 ? null : e.LineColor, e.Style.Background, d.LineWidth ?? 0.75);
                    break;
                case "image":
                    var img = ResolveImage(d.Source);
                    if (img != null) page.DrawImage(img, x, y, w, h, d.KeepAspectRatio);
                    DrawBorder(page, e.Style, x, y, w, h);
                    break;
                default:
                    if (e.Style.Background is { } ebg) page.FillRect(x, y, w, h, ebg);
                    var text = e.Text!.Evaluate(this, default, false, ref _buf);
                    if (text.Length > 0) page.DrawText(text, x, y, w, h, e.Style.Text);
                    DrawBorder(page, e.Style, x, y, w, h);
                    break;
            }
        }
    }

    private static void DrawBorder(PdfPage page, ResolvedStyle s, double x, double y, double w, double h)
    {
        var b = s.Border;
        if (b == null || b.Width <= 0) return;
        var sides = string.IsNullOrWhiteSpace(b.Sides) ? "LTRB" : b.Sides.ToUpperInvariant();
        if (sides.Length >= 4 && sides.Contains('L') && sides.Contains('T') && sides.Contains('R') && sides.Contains('B'))
        {
            page.DrawRoundedRect(x, y, w, h, b.Radius, s.BorderColor, null, b.Width);
            return;
        }
        if (sides.Contains('T')) page.DrawLine(x, y, x + w, y, s.BorderColor, b.Width, b.Dashed);
        if (sides.Contains('B')) page.DrawLine(x, y + h, x + w, y + h, s.BorderColor, b.Width, b.Dashed);
        if (sides.Contains('L')) page.DrawLine(x, y, x, y + h, s.BorderColor, b.Width, b.Dashed);
        if (sides.Contains('R')) page.DrawLine(x + w, y, x + w, y + h, s.BorderColor, b.Width, b.Dashed);
    }

    private PdfImage? ResolveImage(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (_images.TryGetValue(source, out var cached)) return cached;
        PdfImage? img = null;
        try
        {
            byte[]? data = null;
            var s = source.Trim();
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = s.IndexOf(',');
                if (comma > 0) data = Convert.FromBase64String(s[(comma + 1)..]);
            }
            else if (s.StartsWith('{') && s.EndsWith('}'))
            {
                var v = _params.GetValueOrDefault(s[1..^1].Trim());
                data = v switch
                {
                    byte[] b => b,
                    string str when str.StartsWith("data:", StringComparison.OrdinalIgnoreCase) => Convert.FromBase64String(str[(str.IndexOf(',') + 1)..]),
                    string str when str.Length > 0 => File.Exists(str) ? File.ReadAllBytes(str) : Convert.FromBase64String(str),
                    _ => null
                };
            }
            else
            {
                var path = _engine.ResolveImagePath(s);
                if (path != null) data = File.ReadAllBytes(path);
            }
            if (data != null) img = _pdf.AddImage(data);
        }
        catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or InvalidDataException)
        {
            img = null; // تصویر نامعتبر نباید کل گزارش را خراب کند
        }
        _images[source] = img;
        return img;
    }

    private void DrawWatermark(WatermarkDefinition wm)
    {
        var (font, fake) = LookupFont(wm.Font, false);
        var color = Color(wm.Color) ?? PdfColor.Red;
        _page!.DrawRotatedText(wm.Text, _size.Width / 2, _size.Height / 2, wm.Angle, font, wm.FontSize, color, wm.Opacity, fake);
    }

    // ── انواع داخلی ─────────────────────────────────────────────────

    private sealed class Col
    {
        public ColumnDefinition Def = null!;
        public double X, W;
        public int Line;
        public TextTemplate<T> Binding = null!;
        public TextTemplate<T> Header = null!;
        public FieldGetter<T>? Getter;
        public FormatSpec Spec = FormatSpec.Default;
        public bool IsRowNumber, Wrap;
        public ResolvedStyle Style = null!;
        public TextStyle HeaderStyle = null!, TotalsStyle = null!;
        public TextStyle? NegativeStyle;
        public AggregateKind Agg;
        public int AggIndex = -1;
        public char[] Buf = new char[64];
        public int Len;
    }

    private struct AggAcc
    {
        public decimal Sum, Min, Max;
        public long Count;

        public void Reset()
        {
            Sum = 0; Count = 0; Min = decimal.MaxValue; Max = decimal.MinValue;
        }

        public void Add(decimal v)
        {
            Sum += v;
            Count++;
            if (v < Min) Min = v;
            if (v > Max) Max = v;
        }

        public readonly decimal? Get(AggregateKind k) => k switch
        {
            AggregateKind.Sum => Sum,
            AggregateKind.Count => Count,
            AggregateKind.Avg => Count == 0 ? null : Sum / Count,
            AggregateKind.Min => Count == 0 ? null : Min,
            AggregateKind.Max => Count == 0 ? null : Max,
            _ => null
        };
    }
}

/// <summary>نوع ردیف برای گزارش‌های بدون جدول/داده.</summary>
public sealed class NoRow
{
}
