using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using FastPdfWriterStream.Fonts;
using FastPdfWriterStream.IO;
using FastPdfWriterStream.Text;

namespace FastPdfWriterStream;

/// <summary>
/// نویسندهٔ جریانی PDF: هر صفحه به محض تکمیل روی خروجی نوشته می‌شود؛ بنابراین مصرف حافظه
/// مستقل از تعداد صفحات/ردیف‌ها است. فونت‌ها در انتها به صورت subset نوشته می‌شوند.
/// </summary>
public sealed class PdfWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly FileSink _out;
    private readonly List<long> _xref = new() { 0 };
    private readonly int _catalogId, _pagesRootId, _resourcesId, _infoId;

    private readonly List<PdfFont> _fonts = new();
    private readonly List<PdfImage> _images = new();
    private readonly List<PdfSvg> _svgs = new();
    private readonly Dictionary<string, PdfImage> _imageCache = new(StringComparer.Ordinal);
    private readonly List<TotalPagesSlot> _totalSlots = new();
    private readonly Dictionary<int, string> _opacityStates = new();

    // درخت صفحات: هر ۲۵۶ صفحه یک گره میانی
    private const int PagesPerNode = 256;
    private readonly List<int> _pageNodeIds = new();
    private readonly List<List<int>> _pageNodeKids = new();

    private readonly Queue<PendingPage> _pending = new();
    private readonly Stack<ByteSink> _sinkPool = new();

    internal readonly TextShaper Shaper = new();
    internal readonly List<GlyphLine> LineScratch = new();

    private PdfPage? _current;
    private readonly int _sigFieldId, _sigValueId;
    private int _firstPageId;
    private long _sigByteRangePos, _sigContentsStart, _sigContentsEnd;
    private const int ByteRangeWidth = 48;
    private bool _closed;
    private int _pageCount;

    public PdfWriterOptions Options { get; }

    /// <summary>تعداد صفحات ایجادشده تاکنون.</summary>
    public int PageCount => _pageCount;

    public PdfPage? CurrentPage => _current;

    public IReadOnlyList<PdfFont> Fonts => _fonts;

    public PdfWriter(string path, PdfWriterOptions? options = null)
        : this(new FileStream(path, FileMode.Create,
            // امضای دیجیتال باید فایل نوشته‌شده را دوباره بخواند
            options?.Signature != null ? FileAccess.ReadWrite : FileAccess.Write,
            FileShare.None, 1, FileOptions.SequentialScan), false, options)
    {
    }

    public PdfWriter(Stream stream, bool leaveOpen = false, PdfWriterOptions? options = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        Options = options ?? new PdfWriterOptions();
        _out = new FileSink(stream);

        _out.Ascii("%PDF-1.7\n%");
        _out.Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 });
        _out.NewLine();

        _catalogId = Reserve();
        _pagesRootId = Reserve();
        _resourcesId = Reserve();
        _infoId = Reserve();

        if (Options.Signature != null)
        {
            if (!stream.CanSeek || !stream.CanRead)
                throw new ArgumentException("Digital signature needs a readable and seekable output stream (e.g. FileStream opened with ReadWrite, or MemoryStream).", nameof(stream));
            _sigFieldId = Reserve();
            _sigValueId = Reserve();
        }
    }

    // ── مدیریت اشیاء ────────────────────────────────────────────────

    internal int Reserve()
    {
        _xref.Add(-1);
        return _xref.Count - 1;
    }

    private void BeginObject(int id)
    {
        _xref[id] = _out.Position;
        _out.Int(id);
        _out.Ascii(" 0 obj\n");
    }

    private void EndObject() => _out.Ascii("\nendobj\n");

    private void Ref(int id)
    {
        _out.Int(id);
        _out.Ascii(" 0 R");
    }

    private void WriteStreamObject(int id, ReadOnlySpan<byte> data, string? extraDict, bool compressed)
    {
        BeginObject(id);
        _out.Ascii("<< /Length ");
        _out.Int(data.Length);
        if (compressed) _out.Ascii(" /Filter /FlateDecode");
        if (extraDict != null)
        {
            _out.Space();
            _out.Ascii(extraDict);
        }
        _out.Ascii(" >>\nstream\n");
        _out.Bytes(data);
        _out.Ascii("\nendstream");
        EndObject();
    }

    // ── فونت و تصویر ───────────────────────────────────────────────

    /// <summary>افزودن فونت TrueType از فایل.</summary>
    public PdfFont AddFont(string path, string? alias = null) => AddFont(TrueTypeFont.FromFile(path), alias);

    public PdfFont AddFont(byte[] data, string? alias = null) => AddFont(new TrueTypeFont(data), alias);

    /// <summary>
    /// افزودن فونت پارس‌شده. یک <see cref="TrueTypeFont"/> را می‌توان بین چند سند به اشتراک گذاشت
    /// (پارس فقط یک بار انجام می‌شود).
    /// </summary>
    public PdfFont AddFont(TrueTypeFont font, string? alias = null)
    {
        EnsureOpen();
        var f = new PdfFont(font, "F" + (_fonts.Count + 1), Reserve(), alias);
        _fonts.Add(f);
        return f;
    }

    /// <summary>جستجوی فونت بر اساس نام منطقی.</summary>
    public PdfFont? FindFont(string? name)
    {
        if (string.IsNullOrEmpty(name)) return _fonts.Count > 0 ? _fonts[0] : null;
        foreach (var f in _fonts)
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
        foreach (var f in _fonts)
            if (string.Equals(f.Ttf.FamilyName, name, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    public PdfImage AddImage(string path)
    {
        string key = "file:" + Path.GetFullPath(path);
        if (_imageCache.TryGetValue(key, out var img)) return img;
        img = AddImage(PdfImage.FromFile(path));
        _imageCache[key] = img;
        return img;
    }

    public PdfImage AddImage(byte[] data) => AddImage(PdfImage.FromBytes(data));

    /// <summary>تصویر بلافاصله در فایل نوشته می‌شود و حافظه‌اش قابل آزادسازی است.</summary>
    public PdfImage AddImage(PdfImage image)
    {
        EnsureOpen();
        if (image.ObjectId != 0) return image;
        image.ResourceName = "Im" + (_images.Count + 1);
        image.ObjectId = Reserve();
        if (image.AlphaData != null) image.SMaskObjectId = Reserve();
        _images.Add(image);

        var dict = new System.Text.StringBuilder();
        dict.Append("/Type /XObject /Subtype /Image /Width ").Append(image.Width).Append(" /Height ").Append(image.Height);
        if (image.ColorSpace == "Indexed" && image.Palette != null)
        {
            dict.Append(" /ColorSpace [/Indexed /DeviceRGB ").Append(image.Palette.Length / 3 - 1).Append(" <");
            foreach (var b in image.Palette) dict.Append(b.ToString("X2"));
            dict.Append(">]");
        }
        else dict.Append(" /ColorSpace /").Append(image.ColorSpace);
        dict.Append(" /BitsPerComponent ").Append(image.BitsPerComponent);
        if (image.InvertCmyk) dict.Append(" /Decode [1 0 1 0 1 0 1 0]");
        if (image.PngPredictor)
            dict.Append(" /DecodeParms << /Predictor 15 /Colors ").Append(image.Colors)
                .Append(" /BitsPerComponent ").Append(image.BitsPerComponent).Append(" /Columns ").Append(image.Width).Append(" >>");
        if (image.SMaskObjectId != 0) dict.Append(" /SMask ").Append(image.SMaskObjectId).Append(" 0 R");

        BeginObject(image.ObjectId);
        _out.Ascii("<< ");
        _out.Ascii(dict.ToString());
        _out.Ascii(" /Filter /");
        _out.Ascii(image.Filter);
        _out.Ascii(" /Length ");
        _out.Int(image.Data.Length);
        _out.Ascii(" >>\nstream\n");
        _out.Bytes(image.Data);
        _out.Ascii("\nendstream");
        EndObject();

        if (image.AlphaData != null)
        {
            WriteStreamObject(image.SMaskObjectId, image.AlphaData,
                $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8",
                true);
        }
        return image;
    }

    /// <summary>افزودن تصویر برداری SVG (یک بار نوشته می‌شود، در هر صفحه با Do رسم می‌شود).</summary>
    public PdfSvg AddSvg(PdfSvg svg)
    {
        EnsureOpen();
        if (svg.ObjectId != 0) return svg;
        svg.ResourceName = "Sv" + (_svgs.Count + 1);
        svg.ObjectId = Reserve();
        _svgs.Add(svg);
        var content = PdfImage.Deflate(svg.BuildContent());
        WriteStreamObject(svg.ObjectId, content,
            string.Create(CultureInfo.InvariantCulture, $"/Type /XObject /Subtype /Form /BBox [0 0 {svg.Width:0.###} {svg.Height:0.###}]"), true);
        return svg;
    }

    public PdfSvg AddSvg(string path) => AddSvg(PdfSvg.FromFile(path));

    internal string OpacityState(double opacity)
    {
        int key = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 1000);
        if (!_opacityStates.TryGetValue(key, out var name))
        {
            name = "GS" + (_opacityStates.Count + 1);
            _opacityStates[key] = name;
        }
        return name;
    }

    // ── صفحات ───────────────────────────────────────────────────────

    /// <summary>شروع صفحهٔ جدید (صفحهٔ قبلی به صورت خودکار بسته و نوشته می‌شود).</summary>
    public PdfPage NewPage(PageSize? size = null)
    {
        EnsureOpen();
        if (_current != null) FinishPage();
        var s = size ?? Options.PageSize;
        _pageCount++;
        _current = new PdfPage(this, _pageCount, s.Width, s.Height, RentSink());
        return _current;
    }

    internal ByteSink RentSink()
    {
        lock (_sinkPool)
            if (_sinkPool.Count > 0) return _sinkPool.Pop();
        return new ByteSink(32 * 1024);
    }

    private void ReturnSink(ByteSink sink)
    {
        sink.Clear();
        lock (_sinkPool) _sinkPool.Push(sink);
    }

    /// <summary>بستن صفحهٔ جاری.</summary>
    public void FinishPage()
    {
        if (_current == null) return;
        var page = _current;
        _current = null;
        page.Finish();

        int pageId = Reserve();
        int contentId = Reserve();
        int nodeIndex = (page.Number - 1) / PagesPerNode;
        if (nodeIndex >= _pageNodeIds.Count)
        {
            _pageNodeIds.Add(Reserve());
            _pageNodeKids.Add(new List<int>(PagesPerNode));
        }
        _pageNodeKids[nodeIndex].Add(pageId);

        var pending = new PendingPage(pageId, contentId, _pageNodeIds[nodeIndex], page.Width, page.Height, page.Content);
        if (Options.Compress && Options.ParallelCompression)
        {
            var level = Options.CompressionLevel;
            pending.Compressed = Task.Run(() => Compress(pending.Content, level));
            _pending.Enqueue(pending);
            DrainPending(Options.MaxPendingPages);
        }
        else
        {
            if (Options.Compress) pending.Compressed = Task.FromResult(Compress(pending.Content, Options.CompressionLevel));
            WritePage(pending);
        }
    }

    private void DrainPending(int maxPending)
    {
        while (_pending.Count > 0 && (_pending.Peek().Compressed!.IsCompleted || _pending.Count > maxPending))
            WritePage(_pending.Dequeue());
    }

    private static CompressedData Compress(ByteSink content, CompressionLevel level)
    {
        var ms = new PooledBufferStream(Math.Max(1024, content.Length / 3));
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(content.WrittenSpan);
        return new CompressedData(ms.Buffer, ms.WrittenLength);
    }

    private void WritePage(PendingPage p)
    {
        BeginObject(p.PageId);
        _out.Ascii("<< /Type /Page /Parent ");
        Ref(p.ParentId);
        _out.Ascii(" /MediaBox [0 0 ");
        _out.Real(p.Width);
        _out.Space();
        _out.Real(p.Height);
        _out.Ascii("] /Resources ");
        Ref(_resourcesId);
        _out.Ascii(" /Contents ");
        Ref(p.ContentId);
        if (_sigFieldId != 0 && _firstPageId == 0)
        {
            _firstPageId = p.PageId;
            _out.Ascii(" /Annots [");
            Ref(_sigFieldId);
            _out.Ascii("]");
        }
        _out.Ascii(" >>");
        EndObject();

        if (p.Compressed != null)
        {
            var c = p.Compressed.Result;
            WriteStreamObject(p.ContentId, c.Buffer.AsSpan(0, c.Length), null, true);
            ArrayPool<byte>.Shared.Return(c.Buffer);
        }
        else WriteStreamObject(p.ContentId, p.Content.WrittenSpan, null, false);
        ReturnSink(p.Content);
    }

    // ── تعداد کل صفحات (مقدار در پایان سند مشخص می‌شود) ─────────────

    internal TotalPagesSlot GetTotalPagesSlot(PdfFont font, double size, double slotWidth, bool alignRight, DigitStyle digits)
    {
        foreach (var s in _totalSlots)
            if (s.Font == font && Math.Abs(s.Size - size) < 0.001 && Math.Abs(s.SlotWidth - slotWidth) < 0.01 && s.AlignRight == alignRight && s.Digits == digits)
                return s;
        var slot = new TotalPagesSlot("TP" + (_totalSlots.Count + 1), Reserve(), font, size, slotWidth, alignRight, digits);
        _totalSlots.Add(slot);
        return slot;
    }

    // ── پایان سند ───────────────────────────────────────────────────

    /// <summary>نوشتن بخش‌های پایانی و بستن فایل.</summary>
    public void Close()
    {
        if (_closed) return;
        if (_current != null) FinishPage();
        if (_pageCount == 0) { NewPage(); FinishPage(); }
        DrainPending(0);

        WriteTotalSlots();
        foreach (var f in _fonts) WriteFont(f);
        WriteResources();
        WritePageTree();
        if (_sigFieldId != 0) WriteSignaturePlaceholder();
        WriteCatalogAndInfo();
        WriteXrefAndTrailer();
        _out.Flush();
        _out.ReturnBuffer();
        _stream.Flush();
        _closed = true;
        try
        {
            if (_sigFieldId != 0) SignDocument();
        }
        finally
        {
            if (!_leaveOpen) _stream.Dispose();
        }
    }

    // ── امضای دیجیتال (PKCS#7 detached، قابل بررسی در Adobe Reader) ──

    private void WriteSignaturePlaceholder()
    {
        var sig = Options.Signature!;
        BeginObject(_sigValueId);
        _out.Ascii("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange ");
        _sigByteRangePos = _out.Position;
        _out.Ascii(new string(' ', ByteRangeWidth));
        _out.Ascii(" /Contents ");
        _sigContentsStart = _out.Position;
        _out.Byte((byte)'<');
        var zeros = new byte[sig.ReservedBytes * 2];
        Array.Fill(zeros, (byte)'0');
        _out.Bytes(zeros);
        _out.Byte((byte)'>');
        _sigContentsEnd = _out.Position;
        _out.Ascii(" /M ");
        _out.TextString("D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z");
        if (!string.IsNullOrEmpty(sig.Reason)) { _out.Ascii(" /Reason "); _out.TextString(sig.Reason); }
        if (!string.IsNullOrEmpty(sig.Location)) { _out.Ascii(" /Location "); _out.TextString(sig.Location); }
        if (!string.IsNullOrEmpty(sig.ContactInfo)) { _out.Ascii(" /ContactInfo "); _out.TextString(sig.ContactInfo); }
        _out.Ascii(" /Name ");
        _out.TextString(sig.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false));
        _out.Ascii(" >>");
        EndObject();

        // فیلد امضای نامرئی روی صفحهٔ اول
        BeginObject(_sigFieldId);
        _out.Ascii("<< /Type /Annot /Subtype /Widget /FT /Sig /T ");
        _out.TextString(sig.FieldName);
        _out.Ascii(" /V ");
        Ref(_sigValueId);
        _out.Ascii(" /F 132 /Rect [0 0 0 0] /P ");
        Ref(_firstPageId);
        _out.Ascii(" >>");
        EndObject();
    }

    private void SignDocument()
    {
        var sig = Options.Signature!;
        long length = _stream.Length;
        long tail = length - _sigContentsEnd;
        string byteRange = string.Create(CultureInfo.InvariantCulture, $"[0 {_sigContentsStart} {_sigContentsEnd} {tail}]").PadRight(ByteRangeWidth);
        if (byteRange.Length > ByteRangeWidth) throw new InvalidOperationException("ByteRange placeholder too small.");
        _stream.Position = _sigByteRangePos;
        _stream.Write(System.Text.Encoding.ASCII.GetBytes(byteRange));

        // محتوای امضاشده: کل فایل به جز رشتهٔ Contents
        var data = new byte[_sigContentsStart + tail];
        _stream.Position = 0;
        _stream.ReadExactly(data, 0, (int)_sigContentsStart);
        _stream.Position = _sigContentsEnd;
        _stream.ReadExactly(data, (int)_sigContentsStart, (int)tail);

        byte[] pkcs7 = PdfSigner.Sign(data, sig);
        if (pkcs7.Length > sig.ReservedBytes)
            throw new InvalidOperationException($"Signature ({pkcs7.Length} bytes) is larger than ReservedBytes ({sig.ReservedBytes}).");
        var hex = new byte[pkcs7.Length * 2];
        const string digits = "0123456789ABCDEF";
        for (int i = 0; i < pkcs7.Length; i++)
        {
            hex[i * 2] = (byte)digits[pkcs7[i] >> 4];
            hex[i * 2 + 1] = (byte)digits[pkcs7[i] & 0xF];
        }
        _stream.Position = _sigContentsStart + 1;
        _stream.Write(hex);
        _stream.Flush();
    }

    /// <summary>بستن خروجی بدون نوشتن بخش‌های پایانی (در صورت خطا).</summary>
    public void Abort()
    {
        if (_closed) return;
        _closed = true;
        try { DrainPendingSafe(); } catch { /* ignore */ }
        _out.ReturnBuffer();
        if (!_leaveOpen) _stream.Dispose();
    }

    private void DrainPendingSafe()
    {
        while (_pending.Count > 0)
        {
            var p = _pending.Dequeue();
            try { p.Compressed?.Wait(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        if (!_closed)
        {
            try { Close(); }
            catch
            {
                if (!_leaveOpen) _stream.Dispose();
                throw;
            }
        }
    }

    private void EnsureOpen()
    {
        if (_closed) throw new ObjectDisposedException(nameof(PdfWriter));
    }

    private void WriteTotalSlots()
    {
        string total = _pageCount.ToString(CultureInfo.InvariantCulture);
        foreach (var s in _totalSlots)
        {
            var line = Shaper.ShapeLine(s.Font, total, TextDirection.LeftToRight, s.Digits, false);
            double w = line.Width * s.Font.Scale * s.Size;
            double x = s.AlignRight ? s.SlotWidth - w : 0;
            var sink = new ByteSink(256);
            sink.Ascii("BT ");
            sink.Real(x);
            sink.Ascii(" 0 Td ");
            var glyphs = line.Glyphs;
            var fonts = line.Fonts;
            for (int i = 0; i < glyphs.Length; i++)
            {
                var f = fonts.IsEmpty || fonts[i] == 0 ? s.Font : s.Font.FallbackAt(fonts[i]);
                sink.Byte((byte)'/');
                sink.Ascii(f.ResourceName);
                sink.Space();
                sink.Real(s.Size);
                sink.Ascii(" Tf ");
                PdfPage.WriteGlyphs(sink, glyphs.Slice(i, 1));
                sink.Ascii(" Tj ");
            }
            sink.Ascii("ET");
            double asc = s.Font.Ascent(s.Size), desc = s.Font.Descent(s.Size);
            string dict = string.Create(CultureInfo.InvariantCulture,
                $"/Type /XObject /Subtype /Form /BBox [{Math.Min(0, x) - 1:0.###} {-desc - 1:0.###} {Math.Max(s.SlotWidth, x + w) + 1:0.###} {asc + 1:0.###}] /Resources {_resourcesId} 0 R");
            WriteStreamObject(s.ObjectId, sink.WrittenSpan, dict, false);
            sink.ReturnBuffer();
        }
    }

    private void WriteFont(PdfFont f)
    {
        var ttf = f.Ttf;
        int cidId = Reserve(), descId = Reserve(), fileId = Reserve(), toUniId = Reserve();

        var used = new List<ushort>();
        for (int g = 0; g < f.Used.Length; g++)
            if (f.Used[g]) used.Add((ushort)g);

        string tag = SubsetTag(used);
        string baseFont = (Options.SubsetFonts && !ttf.IsCff ? tag + "+" : "") + ttf.PostScriptName;

        // Type0
        BeginObject(f.ObjectId);
        _out.Ascii("<< /Type /Font /Subtype /Type0 /BaseFont ");
        _out.Name(baseFont);
        _out.Ascii(" /Encoding /Identity-H /DescendantFonts [");
        Ref(cidId);
        _out.Ascii("] /ToUnicode ");
        Ref(toUniId);
        _out.Ascii(" >>");
        EndObject();

        // CIDFont + عرض‌ها
        double k = 1000.0 / ttf.UnitsPerEm;
        BeginObject(cidId);
        _out.Ascii(ttf.IsCff ? "<< /Type /Font /Subtype /CIDFontType0 /BaseFont " : "<< /Type /Font /Subtype /CIDFontType2 /BaseFont ");
        _out.Name(baseFont);
        _out.Ascii(" /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor ");
        Ref(descId);
        _out.Ascii(" /DW 1000 /W [");
        int i = 0;
        while (i < used.Count)
        {
            int start = used[i];
            _out.Int(start);
            _out.Ascii(" [");
            int prev = start - 1;
            while (i < used.Count && used[i] == prev + 1)
            {
                _out.Int((long)Math.Round(ttf.GetAdvance(used[i]) * k));
                _out.Space();
                prev = used[i];
                i++;
            }
            _out.Ascii("] ");
        }
        _out.Ascii("]");
        if (!ttf.IsCff) _out.Ascii(" /CIDToGIDMap /Identity");
        _out.Ascii(" >>");
        EndObject();

        // FontDescriptor
        int flags = 32 | (ttf.IsFixedPitch ? 1 : 0) | (ttf.IsItalic ? 64 : 0);
        BeginObject(descId);
        _out.Ascii("<< /Type /FontDescriptor /FontName ");
        _out.Name(baseFont);
        _out.Ascii(" /Flags ");
        _out.Int(flags);
        _out.Ascii(" /FontBBox [");
        _out.Real(ttf.XMin * k); _out.Space(); _out.Real(ttf.YMin * k); _out.Space();
        _out.Real(ttf.XMax * k); _out.Space(); _out.Real(ttf.YMax * k);
        _out.Ascii("] /ItalicAngle ");
        _out.Real(ttf.ItalicAngle);
        _out.Ascii(" /Ascent ");
        _out.Real(ttf.Ascender * k);
        _out.Ascii(" /Descent ");
        _out.Real(ttf.Descender * k);
        _out.Ascii(" /CapHeight ");
        _out.Real(ttf.CapHeight * k);
        _out.Ascii(" /StemV ");
        _out.Int(ttf.IsBold ? 120 : 80);
        _out.Ascii(ttf.IsCff ? " /FontFile3 " : " /FontFile2 ");
        Ref(fileId);
        _out.Ascii(" >>");
        EndObject();

        // برنامهٔ فونت
        byte[] program = !ttf.IsCff && Options.SubsetFonts ? FontSubsetter.Subset(ttf, used) : ttf.Data;
        var compressed = PdfImage.Deflate(program, CompressionLevel.Optimal);
        WriteStreamObject(fileId, compressed,
            ttf.IsCff ? "/Subtype /OpenType" : "/Length1 " + program.Length.ToString(CultureInfo.InvariantCulture), true);

        // ToUnicode
        WriteStreamObject(toUniId, PdfImage.Deflate(BuildToUnicode(f, used)), null, true);
    }

    private static string SubsetTag(List<ushort> used)
    {
        uint h = 2166136261;
        foreach (var g in used) h = (h ^ g) * 16777619;
        Span<char> tag = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + h % 26);
            h /= 26;
        }
        return new string(tag);
    }

    private static byte[] BuildToUnicode(PdfFont f, List<ushort> used)
    {
        var sink = new ByteSink(4096);
        sink.Ascii("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
                   "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
                   "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
                   "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = new List<(ushort G, string S)>();
        foreach (var g in used)
            if (f.ToUnicode[g] is { Length: > 0 } s) entries.Add((g, s));
        for (int i = 0; i < entries.Count; i += 100)
        {
            int n = Math.Min(100, entries.Count - i);
            sink.Int(n);
            sink.Ascii(" beginbfchar\n");
            for (int k = 0; k < n; k++)
            {
                var (g, s) = entries[i + k];
                sink.Byte((byte)'<');
                sink.Hex16(g);
                sink.Ascii("> <");
                foreach (var c in s) sink.Hex16(c);
                sink.Ascii(">\n");
            }
            sink.Ascii("endbfchar\n");
        }
        sink.Ascii("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
        var result = sink.WrittenSpan.ToArray();
        sink.ReturnBuffer();
        return result;
    }

    private void WriteResources()
    {
        BeginObject(_resourcesId);
        _out.Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI]");
        if (_fonts.Count > 0)
        {
            _out.Ascii(" /Font <<");
            foreach (var f in _fonts)
            {
                _out.Space();
                _out.Name(f.ResourceName);
                _out.Space();
                Ref(f.ObjectId);
            }
            _out.Ascii(" >>");
        }
        if (_images.Count > 0 || _totalSlots.Count > 0 || _svgs.Count > 0)
        {
            _out.Ascii(" /XObject <<");
            foreach (var img in _images)
            {
                _out.Space();
                _out.Name(img.ResourceName);
                _out.Space();
                Ref(img.ObjectId);
            }
            foreach (var s in _totalSlots)
            {
                _out.Space();
                _out.Name(s.Name);
                _out.Space();
                Ref(s.ObjectId);
            }
            foreach (var v in _svgs)
            {
                _out.Space();
                _out.Name(v.ResourceName);
                _out.Space();
                Ref(v.ObjectId);
            }
            _out.Ascii(" >>");
        }
        if (_opacityStates.Count > 0)
        {
            _out.Ascii(" /ExtGState <<");
            foreach (var kv in _opacityStates)
            {
                _out.Space();
                _out.Name(kv.Value);
                _out.Ascii(" << /Type /ExtGState /ca ");
                _out.Real(kv.Key / 1000.0);
                _out.Ascii(" /CA ");
                _out.Real(kv.Key / 1000.0);
                _out.Ascii(" >>");
            }
            _out.Ascii(" >>");
        }
        _out.Ascii(" >>");
        EndObject();
    }

    private void WritePageTree()
    {
        for (int n = 0; n < _pageNodeIds.Count; n++)
        {
            BeginObject(_pageNodeIds[n]);
            _out.Ascii("<< /Type /Pages /Parent ");
            Ref(_pagesRootId);
            _out.Ascii(" /Count ");
            _out.Int(_pageNodeKids[n].Count);
            _out.Ascii(" /Kids [");
            foreach (var kid in _pageNodeKids[n])
            {
                Ref(kid);
                _out.Space();
            }
            _out.Ascii("] >>");
            EndObject();
        }
        BeginObject(_pagesRootId);
        _out.Ascii("<< /Type /Pages /Count ");
        _out.Int(_pageCount);
        _out.Ascii(" /Kids [");
        foreach (var id in _pageNodeIds)
        {
            Ref(id);
            _out.Space();
        }
        _out.Ascii("] >>");
        EndObject();
    }

    private void WriteCatalogAndInfo()
    {
        BeginObject(_catalogId);
        _out.Ascii("<< /Type /Catalog /Pages ");
        Ref(_pagesRootId);
        if (Options.RightToLeft) _out.Ascii(" /ViewerPreferences << /Direction /R2L >>");
        if (_sigFieldId != 0)
        {
            _out.Ascii(" /AcroForm << /Fields [");
            Ref(_sigFieldId);
            _out.Ascii("] /SigFlags 3 >>");
        }
        _out.Ascii(" >>");
        EndObject();

        BeginObject(_infoId);
        _out.Ascii("<< /Producer ");
        _out.TextString("FastPdfWriterStream");
        void Entry(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            _out.Ascii(" /");
            _out.Ascii(key);
            _out.Space();
            _out.TextString(value);
        }
        Entry("Title", Options.Title);
        Entry("Author", Options.Author);
        Entry("Subject", Options.Subject);
        Entry("Keywords", Options.Keywords);
        Entry("Creator", Options.Creator);
        _out.Ascii(" /CreationDate ");
        _out.TextString("D:" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        _out.Ascii(" >>");
        EndObject();
    }

    private void WriteXrefAndTrailer()
    {
        long xrefPos = _out.Position;
        _out.Ascii("xref\n0 ");
        _out.Int(_xref.Count);
        _out.Ascii("\n0000000000 65535 f\r\n");
        Span<byte> entry = stackalloc byte[20];
        for (int i = 1; i < _xref.Count; i++)
        {
            long off = _xref[i];
            if (off < 0)
            {
                // شیء رزرو شده ولی استفاده نشده
                _out.Ascii("0000000000 65535 f\r\n");
                continue;
            }
            for (int d = 9; d >= 0; d--)
            {
                entry[d] = (byte)('0' + off % 10);
                off /= 10;
            }
            "00000 n\r\n"u8.CopyTo(entry[11..]);
            entry[10] = (byte)' ';
            _out.Bytes(entry);
        }
        var id = Guid.NewGuid().ToByteArray();
        _out.Ascii("trailer\n<< /Size ");
        _out.Int(_xref.Count);
        _out.Ascii(" /Root ");
        Ref(_catalogId);
        _out.Ascii(" /Info ");
        Ref(_infoId);
        _out.Ascii(" /ID [<");
        foreach (var b in id) _out.Hex8(b);
        _out.Ascii("> <");
        foreach (var b in id) _out.Hex8(b);
        _out.Ascii(">] >>\nstartxref\n");
        _out.Int(xrefPos);
        _out.Ascii("\n%%EOF\n");
    }

    private sealed class PendingPage
    {
        public readonly int PageId, ContentId, ParentId;
        public readonly double Width, Height;
        public readonly ByteSink Content;
        public Task<CompressedData>? Compressed;

        public PendingPage(int pageId, int contentId, int parentId, double w, double h, ByteSink content)
        {
            PageId = pageId; ContentId = contentId; ParentId = parentId; Width = w; Height = h; Content = content;
        }
    }

    private readonly record struct CompressedData(byte[] Buffer, int Length);
}

internal sealed record TotalPagesSlot(string Name, int ObjectId, PdfFont Font, double Size, double SlotWidth, bool AlignRight, DigitStyle Digits);

/// <summary>Stream نوشتنی روی آرایهٔ ArrayPool (برای فشرده‌سازی بدون تخصیص مکرر).</summary>
internal sealed class PooledBufferStream : Stream
{
    public byte[] Buffer;
    private int _length;

    public PooledBufferStream(int capacity) => Buffer = ArrayPool<byte>.Shared.Rent(capacity);

    public int WrittenLength => _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > Buffer.Length)
        {
            var nb = ArrayPool<byte>.Shared.Rent(Math.Max(Buffer.Length * 2, _length + data.Length));
            Buffer.AsSpan(0, _length).CopyTo(nb);
            ArrayPool<byte>.Shared.Return(Buffer);
            Buffer = nb;
        }
        data.CopyTo(Buffer.AsSpan(_length));
        _length += data.Length;
    }
}
