using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class PresignedUrlTests : IAsyncLifetime
{
    private const string Bucket = "presign-bucket";

    private readonly S3HarpFactory factory = new();
    private readonly HttpClient httpClient = new();

    [Fact]
    public async Task PresignedGet_DownloadsTheObjectWithoutSdkAuthentication()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "shared.txt",
                ContentBody = "hello presigned world",
            },
            Token
        );

        var url = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = "shared.txt",
                Verb = HttpVerb.GET,
                Protocol = Protocol.HTTP,
                Expires = DateTime.UtcNow.AddMinutes(5),
            }
        );
        var response = await httpClient.GetAsync(new Uri(url), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello presigned world", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task PresignedPut_UploadsTheObjectWithoutSdkAuthentication()
    {
        using var s3 = await CreateClientWithBucket();

        var url = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = "uploaded.txt",
                Verb = HttpVerb.PUT,
                Protocol = Protocol.HTTP,
                Expires = DateTime.UtcNow.AddMinutes(5),
            }
        );
        using var content = new StringContent("uploaded via presigned url");
        var response = await httpClient.PutAsync(new Uri(url), content, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stored = await s3.GetObjectAsync(Bucket, "uploaded.txt", Token);
        using var reader = new StreamReader(stored.ResponseStream);
        Assert.Equal("uploaded via presigned url", await reader.ReadToEndAsync(Token));
    }

    [Fact]
    public async Task ExpiredPresignedUrl_IsRejected()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "gone.txt",
                ContentBody = "content",
            },
            Token
        );

        var response = await httpClient.GetAsync(UrlPresignedTwoMinutesAgo("gone.txt"), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A GET URL presigned two minutes ago with a one-minute lifetime, built with the
    /// server's own signer so the test needs no waiting.
    /// </summary>
    private Uri UrlPresignedTwoMinutesAgo(string key)
    {
        var timestamp = DateTimeOffset
            .UtcNow.AddMinutes(-2)
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var scope = new CredentialScope(timestamp[..8], "us-east-1", "s3");
        var canonicalQuery =
            "X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + $"&X-Amz-Credential={Uri.EscapeDataString($"{S3HarpFactory.AccessKeyId}/{scope}")}"
            + $"&X-Amz-Date={timestamp}"
            + "&X-Amz-Expires=60"
            + "&X-Amz-SignedHeaders=host";
        var canonicalRequest =
            $"GET\n/{Bucket}/{key}\n{canonicalQuery}\nhost:{factory.BaseAddress.Authority}\n\nhost\nUNSIGNED-PAYLOAD";
        var signature = SigV4Signer.SignCanonicalRequest(
            SigV4Signer.DeriveSigningKey(S3HarpFactory.SecretAccessKey, scope),
            scope,
            timestamp,
            canonicalRequest
        );
        return new Uri(
            factory.BaseAddress,
            $"/{Bucket}/{key}?{canonicalQuery}&X-Amz-Signature={signature}"
        );
    }

    public ValueTask InitializeAsync() => factory.StartAsync();

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        await factory.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<AmazonS3Client> CreateClientWithBucket()
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        return s3;
    }
}
