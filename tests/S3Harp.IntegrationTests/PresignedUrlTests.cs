using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class PresignedUrlTests : IDisposable
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
        var response = await httpClient.PutAsync(
            new Uri(url),
            new StringContent("uploaded via presigned url"),
            Token
        );

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

        var url = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = Bucket,
                Key = "gone.txt",
                Verb = HttpVerb.GET,
                Protocol = Protocol.HTTP,
                Expires = DateTime.UtcNow.AddSeconds(1),
            }
        );
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        var response = await httpClient.GetAsync(new Uri(url), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public void Dispose()
    {
        httpClient.Dispose();
        factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<AmazonS3Client> CreateClientWithBucket()
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        return s3;
    }
}
