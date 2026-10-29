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
    private readonly double[] _pendingLineH = new double[1];
    private decimal?[] _pendingAgg = Array.Empty<decimal?>();
    private bool _hasPending;
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
    private readonly List<(double X1, double X2, double Y)> _extraH = new();
    private readonly List<(double X1, double X2, double Y)> _subLines = new();
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
                var spans = visible.Select(c => c.SpanLines).ToArray();
                if (visible.Any(c => c.Line > 0))
                    for (int i = 0; i < visible.Count; i++) lineOf[i] = spans[i] ? 0 : Math.Max(0, visible[i].Line ?? 0);
                else if (_table.MaxColumnsPerLine > 0 && visible.Count > _table.MaxColumnsPerLine)
                {
                    var flow = Enumerable.Range(0, visible.Count).Where(i => !spans[i]).ToList();
                    int nSpan = visible.Count - flow.Count;
                    int lines = (int)Math.Ceiling(flow.Count / (double)Math.Max(1, _table.MaxColumnsPerLine - nSpan));
                    int perLine = (int)Math.Ceiling(flow.Count / (double)Math.Max(1, lines));
                    for (int k = 0; k < flow.Count; k++) lineOf[flow[k]] = k / perLine;
                }
                _lineCount = visible.Count == 0 ? 1 : lineOf.Max() + 1;
                if (_lineCount == 1) Array.Clear(spans);
                _lineH = new double[_lineCount];
                _pendingLineH = new double[_lineCount];

                // شبکهٔ خانه‌ها (slot): در جدول چندسطری، ستون‌های هر سطر دقیقا زیر ستون‌های سطر قبل قرار می‌گیرند
                var spanIdx = Enumerable.Range(0, visible.Count).Where(i => spans[i]).ToList();
                var perLineIdx = Enumerable.Range(0, _lineCount)
                    .Select(l => Enumerable.Range(0, visible.Count).Where(i => !spans[i] && lineOf[i] == l).ToList()).ToList();
                int flowSlots = perLineIdx.Max(x => x.Count);
                var slotW = new double[spanIdx.Count + flowSlots];
                for (int k = 0; k < spanIdx.Count; k++) slotW[k] = Math.Max(1, visible[spanIdx[k]].Width);
                for (int k = 0; k < flowSlots; k++)
                    slotW[spanIdx.Count + k] = perLineIdx.Where(x => k < x.Count).Max(x => Math.Max(1, visible[x[k]].Width));
                double sum = Math.Max(1, slotW.Sum());
                double tableW = _table.FitWidth ? _contentW : Math.Min(_contentW, sum);
                double scale = tableW / sum;
                _tableLeft = template.RightToLeft ? _left + _contentW - tableW : _left;
                _tableRight = _tableLeft + tableW;
                var slotX = new double[slotW.Length];
                double cur = template.RightToLeft ? _tableRight : _tableLeft;
                for (int k = 0; k < slotW.Length; k++)
                {
                    slotW[k] *= scale;
                    if (template.RightToLeft) { cur -= slotW[k]; slotX[k] = cur; }
                    else { slotX[k] = cur; cur += slotW[k]; }
                }
                // (شروع اسلات, تعداد اسلات) برای هر ستون — آخرین ستون یک سطر کوتاه تا انتها کش می‌آید
                var geo = new (int Slot, int Count)[visible.Count];
                for (int k = 0; k < spanIdx.Count; k++) geo[spanIdx[k]] = (k, 1);
                foreach (var idx in perLineIdx)
                    for (int k = 0; k < idx.Count; k++)
                        geo[idx[k]] = (spanIdx.Count + k, k == idx.Count - 1 ? flowSlots - k : 1);

                _cols = new Col[visible.Count];
                for (int i = 0; i < visible.Count; i++)
                {
                    var cd = visible[i];
                    var (slot, count) = geo[i];
                    double x1 = double.MaxValue, x2 = double.MinValue;
                    for (int k = slot; k < slot + count; k++) { x1 = Math.Min(x1, slotX[k]); x2 = Math.Max(x2, slotX[k] + slotW[k]); }
                    var col = new Col { Def = cd, Line = lineOf[i], Span = spans[i], X = x1, W = x2 - x1 };

                    col.Binding = TextTemplate<T>.Parse(cd.Field, this, bareField: true, defaultFormat: cd.Format);
                    if (col.Binding.Segments.Length == 1)
                    {
                        var s = col.Binding.Segments[0];
                        if (s.Kind == SegmentKind.Field) { col.Getter = s.Getter; col.Spec = s.Spec; }
                        else if (s.Kind == SegmentKind.RowNumber) { col.IsRowNumber = true; col.Spec = s.Spec; }
                    }
                    col.Header = TextTemplate<T>.Parse(cd.Header, this);
                    if (cd.Details is { Count: > 0 })
                    {
                        col.Details = cd.Details.Where(d => !string.IsNullOrWhiteSpace(d.Field))
                            .Select(d => (d.Label, FieldAccess.Create<T>(d.Field), FormatSpec.Get(d.Format))).ToArray();
                        col.Getter = null;
                        col.IsRowNumber = false;
                        cd.Overflow ??= "Wrap";
                    }

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
            _pendingAgg = new decimal?[_aggFields.Count];
            for (int i = 0; i < _aggFields.Count; i++) { _aggReport[i].Reset(); _aggPage[i].Reset(); _aggGroup[i].Reset(); }

            if (expectedRows is > 0 && _table != null)
            {
                double usable = ContentBottom - _top - (_pageHeader?.Height ?? 0) - _table.HeaderHeight - (_table.SubHeaderHeight ?? _table.HeaderHeight) * (_lineCount - 1);
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
        FlushPending(0);

        // گروه‌بندی
        if (_groupGetter != null)
        {
            int len = _groupGetter.Write(row, FormatSpec.Default, ref _buf);
            var key = _buf.AsSpan(0, len);
            if (_groupKeyLen < 0 || !key.SequenceEqual(_groupKey.AsSpan(0, _groupKeyLen)))
            {
                // آخرین ردیف گروه با «جمع گروه» در یک صفحه بماند
                FlushPending(_groupKeyLen >= 0 && _table.ShowGroupTotals ? TotalsRowHeight() : 0);
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

        for (int l = 0; l < _lineCount; l++) _lineH[l] = l == 0 ? _table.RowHeight : _table.SubLineHeight ?? _table.RowHeight;
        if (_hasWrap)
        {
            double maxLine = (TableBottom - _top - _table.HeaderHeight - (_table.SubHeaderHeight ?? _table.HeaderHeight) * (_lineCount - 1) - 1) / _lineCount;
            foreach (var c in cols)
                if (c.Wrap && c.Len > 0 && !c.Span)
                    _lineH[c.Line] = Math.Min(maxLine, Math.Max(_lineH[c.Line],
                        PdfPage.MeasureTextHeight(_pdf, c.Buf.AsSpan(0, c.Len), c.W, c.Style.Text)));
        }
        // سطرهای اضافهٔ کاملا خالی (مثلا سطر جزئیات بدون مقدار) جمع می‌شوند
        for (int l = 1; l < _lineCount; l++)
        {
            bool empty = true;
            foreach (var c in cols)
                if (c.Line == l && !c.Span && c.Len > 0) { empty = false; break; }
            if (empty) _lineH[l] = 0;
        }
        // ردیف رسم نمی‌شود تا ردیف بعدی (یا پایان گزارش) برسد؛ این‌طور آخرین ردیف می‌تواند
        // همراه «جمع کل» به صفحهٔ بعد برود و ردیف جمع تنها در یک صفحه نماند.
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            (c.Buf, c.PBuf) = (c.PBuf, c.Buf);
            c.PLen = c.Len;
        }
        Array.Copy(_lineH, _pendingLineH, _lineCount);
        for (int i = 0; i < _aggGetters.Count; i++)
        {
            var g = _aggGetters[i];
            _pendingAgg[i] = g == null ? 1 : g.GetNumber(row);
        }
        _hasPending = true;
    }

    /// <summary>رسم ردیف معلق؛ اگر <paramref name="keepWith"/> &gt; 0 باشد، آن مقدار فضا هم بعد از ردیف تضمین می‌شود.</summary>
    private void FlushPending(double keepWith)
    {
        if (!_hasPending) return;
        _hasPending = false;
        var cols = _cols;
        double h = 0;
        for (int l = 0; l < _lineCount; l++) h += _pendingLineH[l];
        EnsureSpace(h + keepWith);

        var page = _page!;
        if (_altBg is { } alt && _altRow) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, h, alt);
        _altRow = !_altRow;
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c.PLen == 0) continue;
            double cy = _y, ch = c.Span ? h : _pendingLineH[c.Line];
            if (!c.Span) for (int l = 0; l < c.Line; l++) cy += _pendingLineH[l];
            var style = c.NegativeStyle != null && (c.PBuf[0] == '-' || c.PBuf[0] == '(') ? c.NegativeStyle : c.Style.Text;
            page.DrawText(c.PBuf.AsSpan(0, c.PLen), c.X, cy, c.W, ch, style);
        }
        if (_lineCount > 1) AddLineVerticals(_y, _pendingLineH, separators: true);
        _y += h;
        if (_table!.HorizontalLines) _hLines.Add(_y);
        for (int i = 0; i < _pendingAgg.Length; i++)
        {
            var v = _pendingAgg[i];
            if (!v.HasValue) continue;
            _aggReport[i].Add(v.Value);
            _aggPage[i].Add(v.Value);
            _aggGroup[i].Add(v.Value);
        }
    }

    /// <summary>ارتفاع یک ردیف جمع (فقط سطرهایی که برچسب یا ستون جمع دارند).</summary>
    private double TotalsRowHeight()
    {
        int lines = 0;
        for (int l = 0; l < _lineCount; l++)
            if (l == 0 || _cols.Any(c => c.Line == l && !c.Span && c.Agg != AggregateKind.None)) lines++;
        return TotalsHeight * lines;
    }

    private void FormatCell(Col c, T row)
    {
        if (c.Details != null) { FormatDetails(c, row); return; }
        if (c.Getter != null) c.Len = c.Getter.Write(row, c.Spec, ref c.Buf);
        else if (c.IsRowNumber) c.Len = ValueFormatter.WriteDecimal(_rowNumber, c.Spec, ref c.Buf);
        else if (c.Binding.IsConstant) { c.Len = 0; CopyTo(c, c.Binding.ConstantText); }
        else CopyTo(c, c.Binding.Evaluate(this, row, true, ref _buf));
    }

    private readonly System.Text.StringBuilder _sb = new();

    private void FormatDetails(Col c, T row)
    {
        _sb.Clear();
        string sep = c.Def.DetailSeparator ?? " • ";
        foreach (var (label, getter, spec) in c.Details!)
        {
            if (getter == null) continue;
            int n = getter.Write(row, spec, ref _buf);
            if (n == 0 || _buf.AsSpan(0, n).IsWhiteSpace()) continue;
            if (_sb.Length > 0) _sb.Append(sep);
            // هر «عنوان: مقدار» یک واحد بدون شکستن است (فاصله‌های داخلش NBSP می‌شوند)
            if (label.Length > 0) _sb.Append(label.Replace(' ', '\u00A0')).Append(":\u00A0");
            for (int k = 0; k < n; k++) _sb.Append(_buf[k] == ' ' ? '\u00A0' : _buf[k]);
        }
        CopyTo(c, _sb.ToString());
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
        _subLines.Clear();
        bool firstTablePage = _rowNumber == 0;
        if (firstTablePage || t.RepeatHeader)
        {
            var page = _page!;
            // ارتفاع هر سطر سرجدول؛ سطرهای دوم به بعد می‌توانند کوتاه‌تر یا پنهان (۰) باشند
            var headerLines = new double[_lineCount];
            for (int l = 0; l < _lineCount; l++) headerLines[l] = l == 0 ? t.HeaderHeight : Math.Max(0, t.SubHeaderHeight ?? t.HeaderHeight);
            double hh = headerLines.Sum();
            if (_headerBg is { } hb) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, hh, hb);
            foreach (var c in _cols)
            {
                double ch = c.Span ? hh : headerLines[c.Line];
                if (ch <= 0) continue;
                double cy = _y;
                if (!c.Span) for (int l = 0; l < c.Line; l++) cy += headerLines[l];
                var text = c.Header.Evaluate(this, default, false, ref _buf);
                page.DrawText(text, c.X, cy, c.W, ch, c.HeaderStyle);
            }
            if (_lineCount > 1)
            {
                AddLineVerticals(_y, headerLines, separators: false);
                var (fx1, fx2) = FlowRange();
                double hy = _y;
                for (int l = 1; l < _lineCount; l++)
                {
                    hy += headerLines[l - 1];
                    if (headerLines[l] > 0) _extraH.Add((fx1, fx2, hy));
                }
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
        foreach (var (hx1, hx2, hy) in _extraH) page.PathLine(hx1, hy, hx2, hy);
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
        if (_subLines.Count > 0)
        {
            // جداکنندهٔ کم‌رنگ بین سطرهای یک رکورد
            var light = new PdfColor((byte)((_borderColor.R + 2 * 255) / 3), (byte)((_borderColor.G + 2 * 255) / 3), (byte)((_borderColor.B + 2 * 255) / 3));
            page.BeginPath(light, Math.Max(0.25, t.BorderWidth * 0.6), dashed: true);
            foreach (var (sx1, sx2, sy) in _subLines) page.PathLine(sx1, sy, sx2, sy);
            page.StrokePath();
            page.BeginPath(_borderColor, t.BorderWidth); // بازگرداندن خط ممتد برای ادامه
        }
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
        // فقط سطرهایی که برچسب یا ستون جمع دارند (سطر جزئیات خالی رسم نمی‌شود)
        var usedLines = Enumerable.Range(0, _lineCount)
            .Where(l => l == 0 || _cols.Any(c => c.Line == l && !c.Span && c.Agg != AggregateKind.None)).ToArray();
        double h = lh * usedLines.Length;
        if (scope != AggregateScope.Page && _y + h > TableBottom + 0.01) BreakPage();
        _segments.Add((_segStart, _y));
        var page = _page!;
        if (_totalsBg is { } bg) page.FillRect(_tableLeft, _y, _tableRight - _tableLeft, h, bg);
        var arr = scope switch { AggregateScope.Page => _aggPage, AggregateScope.Group => _aggGroup, _ => _aggReport };

        for (int li = 0; li < usedLines.Length; li++)
        {
            int line = usedLines[li];
            double y = _y + li * lh;
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

    /// <summary>خطوط عمودی هر سطر از یک رکورد چندسطری (+ جداکنندهٔ کم‌رنگ بین سطرها).</summary>
    private void AddLineVerticals(double top, double[] lineHeights, bool separators)
    {
        double total = 0;
        for (int l = 0; l < _lineCount; l++) total += lineHeights[l];
        if (_table!.VerticalLines)
        {
            double y = top;
            for (int l = 0; l < _lineCount; l++)
            {
                foreach (var c in _cols)
                {
                    if (c.Span ? l > 0 : c.Line != l) continue;
                    if (!c.Span && lineHeights[l] <= 0) continue;
                    double x = _t.RightToLeft ? c.X : c.X + c.W;
                    if (Math.Abs(x - _tableLeft) < 0.01 || Math.Abs(x - _tableRight) < 0.01) continue;
                    _extraV.Add((x, y, y + (c.Span ? total : lineHeights[l])));
                }
                y += lineHeights[l];
            }
        }
        if (separators)
        {
            var (x1, x2) = FlowRange();
            double y = top;
            for (int l = 0; l < _lineCount - 1; l++)
            {
                y += lineHeights[l];
                if (lineHeights[l + 1] > 0) _subLines.Add((x1, x2, y));
            }
        }
    }

    /// <summary>بازهٔ افقی ستون‌های غیر Span (برای خطوط بین سطرها).</summary>
    private (double X1, double X2) FlowRange()
    {
        double x1 = _tableLeft, x2 = _tableRight;
        foreach (var c in _cols)
        {
            if (!c.Span) continue;
            if (_t.RightToLeft) x2 = Math.Min(x2, c.X); else x1 = Math.Max(x1, c.X + c.W);
        }
        return (x1, x2);
    }

    /// <summary>پایان گزارش: ردیف‌های جمع، باند پایانی، پاورقی و بستن فایل.</summary>
    public RenderResult Complete()
    {
        if (_completed) throw new InvalidOperationException("Report already completed.");
        if (!_started) StartReport();
        var t = _table;
        if (t != null)
        {
            double keep = 0;
            if (_groupGetter != null && t.ShowGroupTotals) keep += TotalsRowHeight();
            if (t.ShowGrandTotals) keep += TotalsRowHeight();
            FlushPending(keep);
            if (_rowNumber == 0 && !string.IsNullOrEmpty(t.EmptyText))
                DrawFullRow(t.EmptyText, _rowStyle!.Text.With(s => s.Align = HAlign.Center), null, Math.Max(t.RowHeight, 20));
            if (_groupGetter != null && _rowNumber > 0 && t.ShowGroupTotals)
                DrawTotalsRow(t.GroupTotalsLabel, AggregateScope.Group);
            if (t.ShowGrandTotals && _y + TotalsRowHeight() > TableBottom + 0.01) BreakPage();
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
                    if (ResolveSvg(d.Source) is { } svg)
                        page.DrawSvg(svg, x, y, w, h, Color(d.Color), d.KeepAspectRatio);
                    else if (ResolveImage(d.Source) is { } img)
                        page.DrawImage(img, x, y, w, h, d.KeepAspectRatio);
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

    private readonly Dictionary<string, PdfSvg?> _svgImages = new(StringComparer.Ordinal);

    /// <summary>منبع SVG: فایل .svg، data:image/svg+xml یا پارامتری که متن SVG دارد.</summary>
    private PdfSvg? ResolveSvg(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (_svgImages.TryGetValue(source, out var cached)) return cached;
        PdfSvg? svg = null;
        try
        {
            var s = source.Trim();
            string? text = null;
            if (s.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                int comma = s.IndexOf(',');
                var payload = s[(comma + 1)..];
                text = s[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                    : Uri.UnescapeDataString(payload);
            }
            else if (s.StartsWith('{') && s.EndsWith('}'))
            {
                if (_params.GetValueOrDefault(s[1..^1].Trim()) is string str && str.TrimStart().StartsWith("<", StringComparison.Ordinal)) text = str;
            }
            else if (s.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var path = _engine.ResolveImagePath(s);
                if (path != null) text = File.ReadAllText(path);
            }
            if (text != null) svg = _pdf.AddSvg(PdfSvg.Parse(text));
        }
        catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or InvalidDataException or System.Xml.XmlException)
        {
            svg = null;
        }
        _svgImages[source] = svg;
        return svg;
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
        public bool Span;
        public (string Label, FieldGetter<T>? Getter, FormatSpec Spec)[]? Details;
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
        public char[] PBuf = new char[64]; // ردیف معلق (هنوز رسم‌نشده)
        public int PLen;
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
