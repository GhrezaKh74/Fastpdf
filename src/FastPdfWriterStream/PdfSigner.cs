using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace FastPdfWriterStream;

/// <summary>ساخت امضای PKCS#7 detached (SHA-256) برای PDF.</summary>
internal static class PdfSigner
{
    public static byte[] Sign(byte[] data, PdfSignatureOptions options)
    {
        var cert = options.Certificate;
        if (!cert.HasPrivateKey) throw new InvalidOperationException("Signing certificate has no private key.");
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = options.IncludeChain ? X509IncludeOption.WholeChain : X509IncludeOption.EndCertOnly
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        try
        {
            cms.ComputeSignature(signer, silent: true);
        }
        catch (CryptographicException) when (options.IncludeChain)
        {
            // زنجیره قابل ساخت نیست (مثلا گواهی خودامضا یا CA داخلی ناشناخته): فقط خود گواهی
            signer.IncludeOption = X509IncludeOption.EndCertOnly;
            cms.ComputeSignature(signer, silent: true);
        }
        return cms.Encode();
    }
}
