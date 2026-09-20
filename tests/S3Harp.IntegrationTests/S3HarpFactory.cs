using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using S3Harp.Server;
using S3Harp.TestSupport;

namespace S3Harp.IntegrationTests;

/// <summary>
/// Hosts a fully wired S3Harp server on real Kestrel with test credentials and a
/// throwaway data directory, and hands out AWS SDK clients. The AWS SDK is the
/// compatibility oracle, so tests exercise real HTTP end to end. The fixture starts
/// the application itself on a dynamic port, keeping startup deterministic.
/// </summary>
public sealed class S3HarpFactory : IAsyncDisposable
{
    public const string AccessKeyId = "S3HARPTESTACCESSKEY";
    public const string SecretAccessKey = "s3harp-test-secret-access-key";

    private readonly TempDirectory dataDirectory = new("integration");

    private WebApplication? app;
    private X509Certificate2? certificate;

    /// <summary>
    /// Starts the server on a free loopback port; with <paramref name="tls"/>, behind a
    /// self-signed certificate written to the data directory, which the clients this
    /// fixture creates trust and nothing else does.
    /// </summary>
    public async ValueTask StartAsync(bool tls = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["bind"] = "127.0.0.1",
            ["port"] = "0",
            ["access_key_id"] = AccessKeyId,
            ["secret_access_key"] = SecretAccessKey,
            ["data_dir"] = dataDirectory.Path,
        };
        if (tls)
        {
            certificate = TestCertificates.Server();
            settings["tls_cert"] = Write("cert.pem", certificate.ExportCertificatePem());
            settings["tls_key"] = Write("key.pem", TestCertificates.KeyPem(certificate));
        }

        app = S3HarpApplication.Build(settings);
        await app.StartAsync();
    }

    /// <summary>The address the started server listens on.</summary>
    public Uri BaseAddress => new(Started().Urls.First());

    /// <summary>
    /// An SDK client for the server. Path style is the default; a virtual-hosted
    /// client addresses the server as <c>localhost</c> so buckets become
    /// <c>bucket.localhost</c> hosts, which resolve to loopback without setup.
    /// </summary>
    public AmazonS3Client CreateS3Client(
        string accessKeyId = AccessKeyId,
        string secretAccessKey = SecretAccessKey,
        bool virtualHosted = false
    )
    {
        var url = new UriBuilder(BaseAddress);
        if (virtualHosted)
        {
            url.Host = "localhost";
        }

        var config = new AmazonS3Config
        {
            ServiceURL = url.Uri.ToString(),
            ForcePathStyle = !virtualHosted,
            MaxErrorRetry = 0,
        };
        if (certificate is { } trusted)
        {
            config.HttpClientFactory = new TrustingHttpClientFactory(trusted);
        }

        return new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
    }

    /// <summary>A plain HTTP client that trusts the server's certificate when it has one.</summary>
    public HttpClient CreateHttpClient() =>
        certificate is { } trusted ? Trusting(trusted) : new HttpClient();

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        certificate?.Dispose();
        dataDirectory.Dispose();
    }

    private WebApplication Started() =>
        app ?? throw new InvalidOperationException("The server has not been started.");

    private string Write(string name, string content)
    {
        var path = Path.Combine(dataDirectory.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A client that accepts exactly the fixture's certificate, whatever the system trusts.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The client owns the handler and disposes it with itself."
    )]
    private static HttpClient Trusting(X509Certificate2 trusted) =>
        new(
            new HttpClientHandler
            {
                CheckCertificateRevocationList = true,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    presented is not null && presented.Thumbprint == trusted.Thumbprint,
            },
            disposeHandler: true
        );

    private sealed class TrustingHttpClientFactory(X509Certificate2 trusted) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
            Trusting(trusted);
    }
}
