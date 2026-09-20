using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace S3Harp.TestSupport;

/// <summary>Throwaway certificates for exercising TLS: a CA, a leaf it issues, or a self-signed server certificate.</summary>
public static class TestCertificates
{
    public static X509Certificate2 Authority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=S3Harp Test CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(Yesterday, Tomorrow);
    }

    public static X509Certificate2 Leaf(X509Certificate2 authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using var issued = request.Create(authority, Yesterday, Tomorrow, [1, 2, 3, 4]);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>A self-signed server certificate valid for localhost, every *.localhost name and the loopback address.</summary>
    public static X509Certificate2 Server()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName("*.localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)
        );
        return request.CreateSelfSigned(Yesterday, Tomorrow);
    }

    /// <summary>The certificate's private key as PKCS#8 PEM, the form key files hold.</summary>
    public static string KeyPem(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var key =
            certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The certificate has no RSA private key.");
        return key.ExportPkcs8PrivateKeyPem();
    }

    private static DateTimeOffset Yesterday => DateTimeOffset.UtcNow.AddDays(-1);

    private static DateTimeOffset Tomorrow => DateTimeOffset.UtcNow.AddDays(1);
}
