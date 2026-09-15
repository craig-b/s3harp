using Xunit;

namespace S3Harp.Core.Tests;

/// <summary>
/// The behavioral contract every metadata index implementation satisfies.
/// Each implementation runs the same tests through a concrete subclass.
/// </summary>
public abstract class MetadataIndexContractTests
{
    private static readonly DateTimeOffset CreationTime =
        new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    protected abstract IMetadataIndex Index { get; }

    [Fact]
    public async Task CreatedBucket_Exists()
    {
        await Create("alpha");

        Assert.True(await Index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task UnknownBucket_DoesNotExist()
    {
        Assert.False(await Index.BucketExistsAsync("missing", Token));
    }

    [Fact]
    public async Task CreatedBucket_AppearsInTheListWithItsCreationTime()
    {
        await Create("alpha");

        var buckets = await Index.ListBucketsAsync(Token);

        var bucket = Assert.Single(buckets);
        Assert.Equal("alpha", bucket.Name);
        Assert.Equal(CreationTime, bucket.CreatedAt);
    }

    [Fact]
    public async Task CreatingAnExistingBucket_ReportsTheConflict()
    {
        await Create("alpha");

        Assert.False(await Index.TryCreateBucketAsync("alpha", CreationTime, Token));
    }

    [Fact]
    public async Task Buckets_ListInNameOrder()
    {
        await Create("zebra");
        await Create("alpha");
        await Create("mider");

        var buckets = await Index.ListBucketsAsync(Token);

        Assert.Equal(["alpha", "mider", "zebra"], buckets.Select(b => b.Name));
    }

    [Fact]
    public async Task DeletedBucket_NoLongerExists()
    {
        await Create("alpha");

        Assert.Equal(DeleteBucketResult.Deleted, await Index.DeleteBucketAsync("alpha", Token));
        Assert.False(await Index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownBucket_ReportsItMissing()
    {
        Assert.Equal(DeleteBucketResult.NotFound, await Index.DeleteBucketAsync("missing", Token));
    }

    [Fact]
    public async Task DeletingABucketHoldingObjects_ReportsItNotEmpty()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), Token);

        Assert.Equal(DeleteBucketResult.NotEmpty, await Index.DeleteBucketAsync("alpha", Token));
        Assert.True(await Index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task PutObject_IntoAMissingBucket_IsRefused()
    {
        var result = await Index.PutObjectAsync("missing", Record("key", "blob-1"), Token);

        Assert.False(result.BucketExists);
    }

    [Fact]
    public async Task StoredObject_IsRetrievableByItsKey()
    {
        await Create("alpha");

        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), Token);
        var found = await Index.FindObjectAsync("alpha", "key", Token);

        Assert.NotNull(found);
        Assert.Equal("key", found.Key);
        Assert.Equal("blob-1", found.BlobId);
        Assert.Equal(3, found.Size);
        Assert.Equal("etag-hex", found.ETag);
        Assert.Equal("text/plain", found.ContentType);
        Assert.Equal("value-1", found.Metadata["meta-1"]);
        Assert.Equal(CreationTime, found.LastModified);
    }

    [Fact]
    public async Task PutObject_OverAnExistingKey_ReturnsTheReplacedBlobId()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), Token);

        var result = await Index.PutObjectAsync("alpha", Record("key", "blob-2"), Token);

        Assert.True(result.BucketExists);
        Assert.Equal("blob-1", result.ReplacedBlobId);
        Assert.Equal("blob-2", (await Index.FindObjectAsync("alpha", "key", Token))?.BlobId);
    }

    [Fact]
    public async Task FindingAnUnknownKey_ReturnsNothing()
    {
        await Create("alpha");

        Assert.Null(await Index.FindObjectAsync("alpha", "missing", Token));
    }

    [Fact]
    public async Task DeleteObject_ReturnsTheBlobIdAndRemovesTheRecord()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), Token);

        Assert.Equal("blob-1", await Index.DeleteObjectAsync("alpha", "key", Token));
        Assert.Null(await Index.FindObjectAsync("alpha", "key", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownKey_ReturnsNothing()
    {
        await Create("alpha");

        Assert.Null(await Index.DeleteObjectAsync("alpha", "missing", Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ObjectRecord Record(string key, string blobId) => new(
        key, blobId, Size: 3, ETag: "etag-hex", ContentType: "text/plain",
        Metadata: new Dictionary<string, string> { ["meta-1"] = "value-1" },
        LastModified: CreationTime);

    private async Task Create(string name)
    {
        Assert.True(await Index.TryCreateBucketAsync(name, CreationTime, Token));
    }
}

public sealed class InMemoryMetadataIndexTests : MetadataIndexContractTests
{
    protected override IMetadataIndex Index { get; } = new InMemoryMetadataIndex();
}

public sealed class SqliteMetadataIndexTests : MetadataIndexContractTests, IDisposable
{
    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"s3harp-test-{Guid.NewGuid():N}.db");

    private readonly SqliteMetadataIndex index;

    public SqliteMetadataIndexTests()
    {
        index = new SqliteMetadataIndex(databasePath);
    }

    protected override IMetadataIndex Index => index;

    public void Dispose()
    {
        index.Dispose();
        File.Delete(databasePath);
    }
}
