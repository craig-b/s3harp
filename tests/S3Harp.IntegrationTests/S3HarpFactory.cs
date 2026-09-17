using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using S3Harp.Server;

namespace S3Harp.IntegrationTests;

/// <summary>
/// Hosts a fully wired S3Harp server on real Kestrel with test credentials and a
/// throwaway data directory, and hands out AWS SDK clients. The AWS SDK is the
/// compatibility oracle, so tests exercise real HTTP end to end. The fixture starts
/// the application itself on a dynamic port, keeping startup deterministic.
/// </summary>
public sealed class S3HarpFactory : IDisposable
{
    public const string AccessKeyId = "S3HARPTESTACCESSKEY";
    public const string SecretAccessKey = "s3harp-test-secret-access-key";

    private readonly string dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"s3harp-test-{Guid.NewGuid():N}"
    );

    private WebApplication? app;

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
        var url = new UriBuilder(EnsureServerStarted());
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
        return new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
    }

    public void Dispose()
    {
        if (app is not null)
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private string EnsureServerStarted()
    {
        if (app is null)
        {
            app = S3HarpApplication.Build(
                new Dictionary<string, string?>
                {
                    ["BIND"] = "127.0.0.1",
                    ["PORT"] = "0",
                    ["ACCESS_KEY_ID"] = AccessKeyId,
                    ["SECRET_ACCESS_KEY"] = SecretAccessKey,
                    ["DATA_DIR"] = dataDirectory,
                }
            );
            app.StartAsync().GetAwaiter().GetResult();
        }

        return app.Urls.First();
    }
}
