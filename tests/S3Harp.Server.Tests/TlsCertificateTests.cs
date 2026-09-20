using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class TlsCertificateTests : IDisposable
{
    private readonly TempDirectory directory = new("tls");

    [Fact]
    public void LoadsTheLeafWithItsKeyAndTheRestOfTheFileAsItsChain()
    {
        using var authority = TestCertificates.Authority();
        using var leaf = TestCertificates.Leaf(authority);
        var certPath = Write(
            "fullchain.pem",
            leaf.ExportCertificatePem() + "\n" + authority.ExportCertificatePem() + "\n"
        );
        var keyPath = Write("privkey.pem", TestCertificates.KeyPem(leaf));

        using var certificate = TlsCertificate.Load(certPath, keyPath);

        Assert.Equal(leaf.Thumbprint, certificate.Leaf.Thumbprint);
        Assert.True(certificate.Leaf.HasPrivateKey);
        Assert.Equal([authority.Thumbprint], certificate.Chain.Select(issuer => issuer.Thumbprint));
    }

    [Fact]
    public void LoadsALoneCertificateWithAnEmptyChain()
    {
        using var authority = TestCertificates.Authority();
        var certPath = Write("cert.pem", authority.ExportCertificatePem());
        var keyPath = Write("key.pem", TestCertificates.KeyPem(authority));

        using var certificate = TlsCertificate.Load(certPath, keyPath);

        Assert.Equal(authority.Thumbprint, certificate.Leaf.Thumbprint);
        Assert.Empty(certificate.Chain);
    }

    [Theory]
    [InlineData("tls_cert", "cert.pem")]
    [InlineData("tls_key", "key.pem")]
    public void RefusesAFileThatDoesNotExist(string setting, string missing)
    {
        using var authority = TestCertificates.Authority();
        var certPath = Write("cert.pem", authority.ExportCertificatePem());
        var keyPath = Write("key.pem", TestCertificates.KeyPem(authority));
        File.Delete(Path.Combine(directory.Path, missing));

        var exception = Assert.Throws<StartupException>(() =>
            TlsCertificate.Load(certPath, keyPath)
        );

        Assert.Equal(
            $"S3Harp cannot start: {setting} names {Path.Combine(directory.Path, missing)}, which does not exist.",
            exception.Message
        );
    }

    [Fact]
    public void RefusesAKeyThatDoesNotMatchTheCertificate()
    {
        using var authority = TestCertificates.Authority();
        using var other = TestCertificates.Authority();
        var certPath = Write("cert.pem", authority.ExportCertificatePem());
        var keyPath = Write("key.pem", TestCertificates.KeyPem(other));

        var exception = Assert.Throws<StartupException>(() =>
            TlsCertificate.Load(certPath, keyPath)
        );

        Assert.StartsWith(
            $"S3Harp cannot start: {certPath} and {keyPath} are not a PEM certificate and its key: ",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void RefusesAFileThatIsNotPem()
    {
        var certPath = Write("cert.pem", "not a certificate");
        var keyPath = Write("key.pem", "not a key");

        var exception = Assert.Throws<StartupException>(() =>
            TlsCertificate.Load(certPath, keyPath)
        );

        Assert.StartsWith(
            $"S3Harp cannot start: {certPath} and {keyPath} are not a PEM certificate and its key: ",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    public void Dispose() => directory.Dispose();

    private string Write(string name, string content)
    {
        var path = Path.Combine(directory.Path, name);
        File.WriteAllText(path, content);
        return path;
    }
}
