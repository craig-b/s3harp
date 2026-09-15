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

        Assert.True(await Index.TryDeleteBucketAsync("alpha", Token));
        Assert.False(await Index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownBucket_ReportsItMissing()
    {
        Assert.False(await Index.TryDeleteBucketAsync("missing", Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
