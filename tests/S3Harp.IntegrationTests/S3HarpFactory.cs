using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace S3Harp.IntegrationTests;

/// <summary>
/// Hosts S3Harp on real Kestrel with test credentials and a throwaway data
/// directory, and hands out AWS SDK clients. The AWS SDK is the compatibility
/// oracle, so tests exercise real HTTP end to end.
/// </summary>
public sealed class S3HarpFactory : WebApplicationFactory<Program>
{
    public const string AccessKeyId = "S3HARPTESTACCESSKEY";
    public const string SecretAccessKey = "s3harp-test-secret-access-key";

    private readonly string dataDirectory =
        Path.Combine(Path.GetTempPath(), $"s3harp-test-{Guid.NewGuid():N}");

    public S3HarpFactory()
    {
        UseKestrel(port: 0);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ACCESS_KEY_ID", AccessKeyId);
        builder.UseSetting("SECRET_ACCESS_KEY", SecretAccessKey);
        builder.UseSetting("DATA_DIR", dataDirectory);
    }

    public AmazonS3Client CreateS3Client(
        string accessKeyId = AccessKeyId, string secretAccessKey = SecretAccessKey)
    {
        using var httpClient = CreateClient();
        var config = new AmazonS3Config
        {
            ServiceURL = httpClient.BaseAddress!.ToString(),
            ForcePathStyle = true,
            MaxErrorRetry = 0,
        };
        return new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
