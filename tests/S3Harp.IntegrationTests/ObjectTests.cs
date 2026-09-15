using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class ObjectTests : IDisposable
{
    private const string Bucket = "test-bucket";

    private readonly S3HarpFactory factory = new();

    [Fact]
    public async Task PutThenGet_RoundtripsContentTypeAndMetadata()
    {
        using var s3 = await CreateClientWithBucket();

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "greeting.txt",
            ContentBody = "Hello, S3Harp!",
            ContentType = "text/plain",
            Metadata = { ["note"] = "from-test" },
        }, Token);
        using var response = await s3.GetObjectAsync(Bucket, "greeting.txt", Token);

        using var reader = new StreamReader(response.ResponseStream);
        Assert.Equal("Hello, S3Harp!", await reader.ReadToEndAsync(Token));
        Assert.Equal("text/plain", response.Headers.ContentType);
        Assert.Equal("from-test", response.Metadata["note"]);
    }

    [Fact]
    public async Task PutObject_ReturnsTheMd5ETag()
    {
        using var s3 = await CreateClientWithBucket();

        var response = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "known.txt",
            ContentBody = "hello world",
        }, Token);

        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", response.ETag);
    }

    [Fact]
    public async Task LargeBody_RoundtripsByteForByte()
    {
        using var s3 = await CreateClientWithBucket();
        var content = RandomNumberGenerator.GetBytes(300 * 1024);

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "large.bin",
            InputStream = new MemoryStream(content),
        }, Token);
        using var response = await s3.GetObjectAsync(Bucket, "large.bin", Token);

        using var received = new MemoryStream();
        await response.ResponseStream.CopyToAsync(received, Token);
        Assert.Equal(content, received.ToArray());
    }

    [Fact]
    public async Task KeysWithSlashesAndSpaces_Roundtrip()
    {
        using var s3 = await CreateClientWithBucket();
        const string key = "folder/sub folder/file with spaces.txt";

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            ContentBody = "nested",
        }, Token);
        using var response = await s3.GetObjectAsync(Bucket, key, Token);

        using var reader = new StreamReader(response.ResponseStream);
        Assert.Equal("nested", await reader.ReadToEndAsync(Token));
    }

    [Fact]
    public async Task HeadObject_ReportsSizeAndETag()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "head.txt",
            ContentBody = "hello world",
        }, Token);

        var metadata = await s3.GetObjectMetadataAsync(Bucket, "head.txt", Token);

        Assert.Equal(11, metadata.ContentLength);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", metadata.ETag);
    }

    [Fact]
    public async Task DeletedObject_IsNoLongerRetrievable()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "doomed.txt",
            ContentBody = "content",
        }, Token);

        await s3.DeleteObjectAsync(Bucket, "doomed.txt", Token);

        var exception = await Assert.ThrowsAsync<NoSuchKeyException>(
            () => s3.GetObjectAsync(Bucket, "doomed.txt", Token));
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task DeletingABucketHoldingObjects_ThrowsBucketNotEmpty()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "occupant.txt",
            ContentBody = "content",
        }, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => s3.DeleteBucketAsync(Bucket, Token));

        Assert.Equal("BucketNotEmpty", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
    }

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<AmazonS3Client> CreateClientWithBucket()
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        return s3;
    }
}
