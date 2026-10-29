using System.Buffers.Binary;
using System.IO.Compression;

namespace FastPdfWriterStream;

/// <summary>تصویر ثبت‌شده در سند (JPEG یا PNG). هر تصویر فقط یک بار در فایل نوشته می‌شود.</summary>
public sealed class PdfImage
{
    internal string ResourceName = "";
    internal int ObjectId;
    internal int SMaskObjectId;

    public int Width { get; }
    public int Height { get; }

    internal readonly string Filter;          // DCTDecode یا FlateDecode
    internal readonly byte[] Data;
    internal readonly string ColorSpace;      // DeviceRGB / DeviceGray / DeviceCMYK / Indexed
    internal readonly int BitsPerComponent;
    internal readonly int Colors;
    internal readonly bool PngPredictor;
    internal readonly byte[]? Palette;
    internal readonly byte[]? AlphaData;      // Flate (بدون predictor)
    internal readonly bool InvertCmyk;

    private PdfImage(int w, int h, string filter, byte[] data, string colorSpace, int bpc, int colors, bool predictor,
        byte[]? palette = null, byte[]? alpha = null, bool invertCmyk = false)
    {
        Width = w; Height = h; Filter = filter; Data = data; ColorSpace = colorSpace; BitsPerComponent = bpc;
        Colors = colors; PngPredictor = predictor; Palette = palette; AlphaData = alpha; InvertCmyk = invertCmyk;
    }

    /// <summary>تشخیص نوع فایل و ساخت تصویر.</summary>
    public static PdfImage FromBytes(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return FromJpeg(data);
        if (data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G') return FromPng(data);
        throw new NotSupportedException("Only JPEG and PNG images are supported.");
    }

    public static PdfImage FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    private static PdfImage FromJpeg(byte[] d)
    {
        int p = 2;
        while (p + 9 < d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            byte marker = d[p + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
            bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (sof)
            {
                int bpc = d[p + 4];
                int h = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 5));
                int w = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 7));
                int comps = d[p + 9];
                string cs = comps switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" };
                bool adobe = d.AsSpan().IndexOf("Adobe"u8) >= 0;
                return new PdfImage(w, h, "DCTDecode", d, cs, bpc, comps, false, invertCmyk: comps == 4 && adobe);
            }
            p += 2 + len;
        }
        throw new InvalidDataException("Invalid JPEG file.");
    }

    private static PdfImage FromPng(byte[] d)
    {
        int p = 8;
        int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (p + 8 <= d.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            int data = p + 8;
            switch (type)
            {
                case "IHDR":
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(data));
                    h = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(data + 4));
                    bitDepth = d[data + 8]; colorType = d[data + 9]; interlace = d[data + 12];
                    break;
                case "PLTE": palette = d.AsSpan(data, len).ToArray(); break;
                case "tRNS": trns = d.AsSpan(data, len).ToArray(); break;
                case "IDAT": idat.Write(d, data, len); break;
                case "IEND": p = d.Length; continue;
            }
            p = data + len + 4;
        }
        if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not supported.");
        var zdata = idat.ToArray();

        int colors = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("PNG color type") };

        // بدون آلفا: مستقیما داده‌ی zlib را با predictor به PDF می‌دهیم (بدون decode)
        if (colorType == 0 || colorType == 2 || (colorType == 3 && trns == null))
        {
            string cs = colorType == 0 ? "DeviceGray" : colorType == 2 ? "DeviceRGB" : "Indexed";
            return new PdfImage(w, h, "FlateDecode", zdata, cs, bitDepth, colors, true, palette);
        }

        // دارای آلفا: باز کردن، حذف فیلتر و جداسازی کانال‌ها
        if (bitDepth != 8) throw new NotSupportedException("PNG with alpha must be 8-bit.");
        int bpp = colorType == 3 ? 1 : colors;
        int stride = w * bpp;
        var raw = Inflate(zdata, (stride + 1) * h);
        Unfilter(raw, stride, h, bpp);

        int outColors = colorType == 4 ? 1 : 3;
        var color = new byte[w * h * outColors];
        var alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * (stride + 1) + 1;
            for (int x = 0; x < w; x++)
            {
                int px = y * w + x;
                switch (colorType)
                {
                    case 6:
                        color[px * 3] = raw[row + x * 4];
                        color[px * 3 + 1] = raw[row + x * 4 + 1];
                        color[px * 3 + 2] = raw[row + x * 4 + 2];
                        alpha[px] = raw[row + x * 4 + 3];
                        break;
                    case 4:
                        color[px] = raw[row + x * 2];
                        alpha[px] = raw[row + x * 2 + 1];
                        break;
                    case 3:
                        int idx = raw[row + x];
                        color[px * 3] = palette![idx * 3];
                        color[px * 3 + 1] = palette[idx * 3 + 1];
                        color[px * 3 + 2] = palette[idx * 3 + 2];
                        alpha[px] = idx < trns!.Length ? trns[idx] : (byte)255;
                        break;
                }
            }
        }
        return new PdfImage(w, h, "FlateDecode", Deflate(color), outColors == 1 ? "DeviceGray" : "DeviceRGB", 8, outColors,
            false, null, Deflate(alpha));
    }

    private static byte[] Inflate(byte[] z, int expected)
    {
        using var input = new MemoryStream(z);
        using var zs = new ZLibStream(input, CompressionMode.Decompress);
        var result = new byte[expected];
        int read = 0;
        while (read < expected)
        {
            int r = zs.Read(result, read, expected - read);
            if (r == 0) break;
            read += r;
        }
        return result;
    }

    internal static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, level, true)) zs.Write(data);
        return ms.ToArray();
    }

    private static void Unfilter(byte[] raw, int stride, int h, int bpp)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * (stride + 1);
            int filter = raw[row];
            int cur = row + 1, prev = row - stride; // prev = شروع داده‌ی سطر قبل
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? raw[cur + x - bpp] : 0;
                int b = y > 0 ? raw[prev + x] : 0;
                int c = x >= bpp && y > 0 ? raw[prev + x - bpp] : 0;
                int v = raw[cur + x];
                v = filter switch
                {
                    1 => v + a,
                    2 => v + b,
                    3 => v + ((a + b) >> 1),
                    4 => v + Paeth(a, b, c),
                    _ => v
                };
                raw[cur + x] = (byte)v;
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
