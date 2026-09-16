using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class ListObjectsTests : IDisposable
{
    private const string Bucket = "list-bucket";

    private readonly S3HarpFactory factory = new();

    [Fact]
    public async Task ListObjectsV2_ReturnsObjectsAndCommonPrefixes()
    {
        using var s3 = await CreateClientWithKeys(
            "a.txt", "docs/one.txt", "docs/two.txt", "photos/pic.jpg", "z.txt");

        var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            Delimiter = "/",
        }, Token);

        Assert.Equal(["a.txt", "z.txt"], (response.S3Objects ?? []).Select(o => o.Key));
        Assert.Equal(["docs/", "photos/"], response.CommonPrefixes ?? []);
        Assert.False(response.IsTruncated);
    }

    [Fact]
    public async Task ListObjectsV2_WithAPrefix_ReturnsOnlyMatchingKeys()
    {
        using var s3 = await CreateClientWithKeys("a.txt", "docs/one.txt", "docs/two.txt");

        var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = "docs/",
        }, Token);

        Assert.Equal(
            ["docs/one.txt", "docs/two.txt"],
            (response.S3Objects ?? []).Select(o => o.Key));
    }

    [Fact]
    public async Task ListObjectsV2_PaginatesThroughEveryEntry()
    {
        using var s3 = await CreateClientWithKeys("a", "b", "c", "d", "e");
        var collected = new List<string>();
        string? token = null;

        do
        {
            var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                MaxKeys = 2,
                ContinuationToken = token,
            }, Token);
            collected.AddRange((response.S3Objects ?? []).Select(o => o.Key));
            token = response.NextContinuationToken;
        }
        while (token is not null);

        Assert.Equal(["a", "b", "c", "d", "e"], collected);
    }

    [Fact]
    public async Task ListObjectsV2_StartAfter_SkipsEarlierKeys()
    {
        using var s3 = await CreateClientWithKeys("a", "b", "c");

        var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            StartAfter = "a",
        }, Token);

        Assert.Equal(["b", "c"], (response.S3Objects ?? []).Select(o => o.Key));
    }

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<AmazonS3Client> CreateClientWithKeys(params string[] keys)
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        foreach (var key in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = key,
                ContentBody = key,
            }, Token);
        }

        return s3;
    }
}
