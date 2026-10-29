using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace FastPdfWriterStream.Tests;

public class SignatureTests
{
    internal static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NewBilling.bsi.ir", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(700)]
    public void SignedPdf_HasValidDetachedSignature(int pages)
    {
        using var cert = SelfSigned();
        var path = Path.Combine(Path.GetTempPath(), $"signed-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var pdf = new PdfWriter(path, new PdfWriterOptions { Signature = new PdfSignatureOptions(cert) { Reason = "تست" } }))
            {
                var font = pdf.AddFont(TestFonts.RegularFont);
                for (int i = 0; i < pages; i++)
                    pdf.NewPage().DrawText("صفحه " + i, 20, 20, 300, 20, new TextStyle { Font = font });
            }
            var bytes = File.ReadAllBytes(path);
            var r = new PdfTestReader(bytes); // xref و ساختار
            Assert.Equal(pages, r.PageCount);

            var latin = Encoding.Latin1.GetString(bytes);
            var m = Regex.Match(latin, @"/ByteRange \[0 (\d+) (\d+) (\d+)\]\s*/Contents <([0-9A-F]+)>");
            Assert.True(m.Success);
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value), c = int.Parse(m.Groups[3].Value);
            Assert.Equal(bytes.Length, b + c);
            Assert.Equal((byte)'<', bytes[a]);
            Assert.Equal((byte)'>', bytes[b - 1]);

            var signed = bytes.Take(a).Concat(bytes.Skip(b)).ToArray();
            var hex = m.Groups[4].Value.TrimEnd('0');
            if (hex.Length % 2 == 1) hex += "0";
            var cms = new SignedCms(new ContentInfo(signed), detached: true);
            cms.Decode(Convert.FromHexString(hex));
            cms.CheckSignature(verifySignatureOnly: true); // اگر محتوا تغییر کرده باشد استثنا می‌دهد
            Assert.Equal(cert.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);

            // دستکاری یک بایت ← امضا نامعتبر
            signed[signed.Length / 2] ^= 1;
            var tampered = new SignedCms(new ContentInfo(signed), detached: true);
            tampered.Decode(Convert.FromHexString(hex));
            Assert.ThrowsAny<CryptographicException>(() => tampered.CheckSignature(verifySignatureOnly: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReportEngine_SignsTemplateReports()
    {
        using var cert = SelfSigned();
        var engine = new FastPdfWriterStream.Templates.ReportEngine(new() { FontsDirectory = TestFonts.FontsDir, TemplatesDirectory = TestFonts.TemplatesDir });
        var t = engine.LoadTemplate("bank-statement");
        var path = Path.Combine(Path.GetTempPath(), $"signed-report-{Guid.NewGuid():N}.pdf");
        try
        {
            var rows = Enumerable.Range(0, 50).Select(i => new Dictionary<string, object?> { ["TR0108"] = "14030512", ["TR0105"] = 1000m * i });
            engine.Render(t, rows.ToList(), path, new { BankName = "بانک" }, signature: new PdfSignatureOptions(cert));
            var bytes = File.ReadAllBytes(path);
            new PdfTestReader(bytes);
            Assert.Contains("/SubFilter /adbe.pkcs7.detached", Encoding.Latin1.GetString(bytes));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Signature_RequiresSeekableReadableStream()
    {
        using var cert = SelfSigned();
        Assert.Throws<ArgumentException>(() => new PdfWriter(new WriteOnlyStream(), false, new PdfWriterOptions { Signature = new PdfSignatureOptions(cert) }));
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
