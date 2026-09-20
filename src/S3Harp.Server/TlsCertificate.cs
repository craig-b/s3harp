using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace S3Harp.Server;

/// <summary>
/// The certificate the server presents, loaded from the PEM files <c>tls_cert</c>
/// and <c>tls_key</c> name: the first certificate in the file with its key, and
/// the rest of the file as the chain sent alongside it. A file that is missing or
/// not what it should be stops the server with a <see cref="StartupException"/>.
/// </summary>
internal sealed class TlsCertificate(X509Certificate2 leaf, X509Certificate2Collection chain)
    : IDisposable
{
    public X509Certificate2 Leaf { get; } = leaf;

    public X509Certificate2Collection Chain { get; } = chain;

    public static TlsCertificate Load(string certPath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(certPath);
        ArgumentNullException.ThrowIfNull(keyPath);
        var fullCertPath = Existing("tls_cert", certPath);
        var fullKeyPath = Existing("tls_key", keyPath);
        try
        {
            var leaf = X509Certificate2.CreateFromPemFile(fullCertPath, fullKeyPath);
            var file = new X509Certificate2Collection();
            file.ImportFromPemFile(fullCertPath);
            var chain = new X509Certificate2Collection();
            foreach (var certificate in file)
            {
                if (certificate.Thumbprint == leaf.Thumbprint)
                {
                    certificate.Dispose();
                }
                else
                {
                    chain.Add(certificate);
                }
            }

            return new TlsCertificate(leaf, chain);
        }
        catch (CryptographicException exception)
        {
            throw new StartupException(
                $"S3Harp cannot start: {fullCertPath} and {fullKeyPath} are not a PEM certificate and its key: {exception.Message}",
                exception
            );
        }
    }

    public void Dispose()
    {
        Leaf.Dispose();
        foreach (var issuer in Chain)
        {
            issuer.Dispose();
        }
    }

    private static string Existing(string setting, string path)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new StartupException(
                $"S3Harp cannot start: {setting} names {fullPath}, which does not exist."
            );
    }
}
