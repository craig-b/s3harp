using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class BucketTests : IDisposable
{
    private readonly S3HarpFactory factory = new();

    [Fact]
    public async Task CreatedBucket_AppearsInTheBucketList()
    {
        using var s3 = factory.CreateS3Client();

        await s3.PutBucketAsync(
            new PutBucketRequest { BucketName = "alpha" }, Token);
        var response = await s3.ListBucketsAsync(Token);

        var bucket = Assert.Single(response.Buckets ?? []);
        Assert.Equal("alpha", bucket.BucketName);
        Assert.Equal(S3HarpFactory.AccessKeyId, response.Owner.Id);
    }

    [Fact]
    public async Task CreatingAnExistingBucket_ThrowsBucketAlreadyOwnedByYou()
    {
        using var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = "alpha" }, Token);

        var exception = await Assert.ThrowsAsync<BucketAlreadyOwnedByYouException>(
            () => s3.PutBucketAsync(new PutBucketRequest { BucketName = "alpha" }, Token));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task DeletedBucket_LeavesTheBucketListEmpty()
    {
        using var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = "alpha" }, Token);

        await s3.DeleteBucketAsync("alpha", Token);
        var response = await s3.ListBucketsAsync(Token);

        Assert.Empty(response.Buckets ?? []);
    }

    [Fact]
    public async Task DeletingABucketWithAnInProgressUpload_Succeeds()
    {
        using var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = "alpha" }, Token);
        var upload = await s3.InitiateMultipartUploadAsync("alpha", "big.bin", Token);
        await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = "alpha",
            Key = "big.bin",
            UploadId = upload.UploadId,
            PartNumber = 1,
            InputStream = new MemoryStream("part one"u8.ToArray()),
        }, Token);

        await s3.DeleteBucketAsync("alpha", Token);
        var response = await s3.ListBucketsAsync(Token);

        Assert.Empty(response.Buckets ?? []);
    }

    [Fact]
    public async Task DeletingAnUnknownBucket_ThrowsNoSuchBucket()
    {
        using var s3 = factory.CreateS3Client();

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => s3.DeleteBucketAsync("missing", Token));

        Assert.Equal("NoSuchBucket", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
