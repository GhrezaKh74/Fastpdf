using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace FastPdfWriterStream.IO;

/// <summary>
/// بافر بایتی قابل رشد با متدهای سریع برای نوشتن توکن‌های PDF (عدد، نام، رشته).
/// برای محتوای صفحه (content stream) و همچنین برای نوشتن مستقیم روی فایل استفاده می‌شود.
/// </summary>
internal class ByteSink
{
    protected byte[] Buffer;
    protected int Pos;

    public ByteSink(int capacity = 64 * 1024)
    {
        Buffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    public int Length => Pos;
    public ReadOnlySpan<byte> WrittenSpan => Buffer.AsSpan(0, Pos);
    public ReadOnlyMemory<byte> WrittenMemory => Buffer.AsMemory(0, Pos);

    public void Clear() => Pos = 0;

    public void ReturnBuffer()
    {
        if (Buffer.Length > 0) ArrayPool<byte>.Shared.Return(Buffer);
        Buffer = Array.Empty<byte>();
        Pos = 0;
    }

    /// <summary>باقی‌ماندهٔ بافر را تضمین می‌کند. در کلاس فرزند ممکن است به جای رشد، flush کند.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void Ensure(int count)
    {
        if (Pos + count > Buffer.Length) Grow(count);
    }

    protected virtual void Grow(int count)
    {
        int newSize = Math.Max(Buffer.Length * 2, Pos + count);
        var nb = ArrayPool<byte>.Shared.Rent(newSize);
        Buffer.AsSpan(0, Pos).CopyTo(nb);
        if (Buffer.Length > 0) ArrayPool<byte>.Shared.Return(Buffer);
        Buffer = nb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Byte(byte b)
    {
        if (Pos >= Buffer.Length) Grow(1);
        Buffer[Pos++] = b;
    }

    public void Bytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > 4096 && this is FileSink)
        {
            ((FileSink)this).WriteLarge(data);
            return;
        }
        Ensure(data.Length);
        data.CopyTo(Buffer.AsSpan(Pos));
        Pos += data.Length;
    }

    /// <summary>نوشتن رشتهٔ ASCII (برای توکن‌های ثابت PDF).</summary>
    public void Ascii(string s)
    {
        Ensure(s.Length);
        var span = Buffer.AsSpan(Pos, s.Length);
        for (int i = 0; i < s.Length; i++) span[i] = (byte)s[i];
        Pos += s.Length;
    }

    public void Utf8(string s)
    {
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        Ensure(max);
        Pos += Encoding.UTF8.GetBytes(s, Buffer.AsSpan(Pos));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Space() => Byte((byte)' ');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NewLine() => Byte((byte)'\n');

    public void Int(long v)
    {
        Ensure(21);
        if (v < 0)
        {
            Buffer[Pos++] = (byte)'-';
            v = -v;
        }
        if (v < 10)
        {
            Buffer[Pos++] = (byte)('0' + v);
            return;
        }
        Span<byte> tmp = stackalloc byte[20];
        int n = 0;
        while (v > 0)
        {
            tmp[n++] = (byte)('0' + (v % 10));
            v /= 10;
        }
        while (n > 0) Buffer[Pos++] = tmp[--n];
    }

    /// <summary>
    /// نوشتن عدد اعشاری با حداکثر ۳ رقم اعشار (کافی برای مختصات PDF) بدون هیچ تخصیص حافظه.
    /// </summary>
    public void Real(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
        long scaled = (long)Math.Round(v * 1000.0, MidpointRounding.AwayFromZero);
        if (scaled == 0)
        {
            Byte((byte)'0');
            return;
        }
        Ensure(24);
        if (scaled < 0)
        {
            Buffer[Pos++] = (byte)'-';
            scaled = -scaled;
        }
        long ip = scaled / 1000;
        int frac = (int)(scaled % 1000);
        Int(ip);
        if (frac != 0)
        {
            Buffer[Pos++] = (byte)'.';
            int d1 = frac / 100, d2 = (frac / 10) % 10, d3 = frac % 10;
            Buffer[Pos++] = (byte)('0' + d1);
            if (d2 != 0 || d3 != 0)
            {
                Buffer[Pos++] = (byte)('0' + d2);
                if (d3 != 0) Buffer[Pos++] = (byte)('0' + d3);
            }
        }
    }

    /// <summary>نوشتن رشتهٔ متنی PDF به صورت literal با escape.</summary>
    public void LiteralString(ReadOnlySpan<byte> raw)
    {
        Ensure(raw.Length * 2 + 2);
        Buffer[Pos++] = (byte)'(';
        foreach (var b in raw)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    Buffer[Pos++] = (byte)'\\';
                    Buffer[Pos++] = b;
                    break;
                case (byte)'\r':
                    Buffer[Pos++] = (byte)'\\';
                    Buffer[Pos++] = (byte)'r';
                    break;
                default:
                    Buffer[Pos++] = b;
                    break;
            }
        }
        Buffer[Pos++] = (byte)')';
    }

    /// <summary>نوشتن رشتهٔ متنی (Unicode) به صورت UTF-16BE با BOM — برای اطلاعات سند.</summary>
    public void TextString(string s)
    {
        bool ascii = true;
        foreach (var c in s)
            if (c > 126 || c < 32) { ascii = false; break; }
        if (ascii)
        {
            Span<byte> tmp = s.Length <= 512 ? stackalloc byte[s.Length] : new byte[s.Length];
            for (int i = 0; i < s.Length; i++) tmp[i] = (byte)s[i];
            LiteralString(tmp);
            return;
        }
        Ensure(s.Length * 4 + 6);
        Buffer[Pos++] = (byte)'<';
        Buffer[Pos++] = (byte)'F'; Buffer[Pos++] = (byte)'E'; Buffer[Pos++] = (byte)'F'; Buffer[Pos++] = (byte)'F';
        foreach (var c in s) Hex16(c);
        Buffer[Pos++] = (byte)'>';
    }

    private static ReadOnlySpan<byte> HexDigits => "0123456789ABCDEF"u8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Hex16(int v)
    {
        Ensure(4);
        var h = HexDigits;
        Buffer[Pos++] = h[(v >> 12) & 0xF];
        Buffer[Pos++] = h[(v >> 8) & 0xF];
        Buffer[Pos++] = h[(v >> 4) & 0xF];
        Buffer[Pos++] = h[v & 0xF];
    }

    public void Hex8(int v)
    {
        Ensure(2);
        var h = HexDigits;
        Buffer[Pos++] = h[(v >> 4) & 0xF];
        Buffer[Pos++] = h[v & 0xF];
    }

    /// <summary>نام PDF با escape کاراکترهای خاص.</summary>
    public void Name(string name)
    {
        Ensure(name.Length * 3 + 1);
        Buffer[Pos++] = (byte)'/';
        foreach (var c in name)
        {
            if (c < 33 || c > 126 || c == '#' || c == '/' || c == '(' || c == ')' || c == '<' || c == '>' ||
                c == '[' || c == ']' || c == '{' || c == '}' || c == '%')
            {
                Buffer[Pos++] = (byte)'#';
                Hex8(c & 0xFF);
            }
            else Buffer[Pos++] = (byte)c;
        }
    }
}

/// <summary>
/// خروجی مستقیم روی Stream با بافر ثابت؛ موقعیت (offset) را برای جدول xref نگه می‌دارد.
/// </summary>
internal sealed class FileSink : ByteSink
{
    private readonly Stream _stream;
    private long _flushed;

    public FileSink(Stream stream, int bufferSize = 256 * 1024) : base(bufferSize)
    {
        _stream = stream;
    }

    public long Position => _flushed + Pos;

    protected override void Grow(int count)
    {
        Flush();
        if (count > Buffer.Length) base.Grow(count);
    }

    public void WriteLarge(ReadOnlySpan<byte> data)
    {
        Flush();
        _stream.Write(data);
        _flushed += data.Length;
    }

    public void Flush()
    {
        if (Pos == 0) return;
        _stream.Write(Buffer, 0, Pos);
        _flushed += Pos;
        Pos = 0;
    }
}
