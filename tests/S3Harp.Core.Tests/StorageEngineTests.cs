using System.Text;
using Xunit;

namespace S3Harp.Core.Tests;

public sealed class StorageEngineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"s3harp-engine-{Guid.NewGuid():N}");

    private readonly InMemoryMetadataIndex index = new();
    private readonly StorageEngine engine;

    public StorageEngineTests()
    {
        engine = new StorageEngine(
            index, new BlobStore(root), new FixedTimeProvider(Now), StorageLimits.S3);
    }

    [Fact]
    public async Task PutThenGet_RoundtripsContentTypeAndMetadata()
    {
        await CreateBucket("alpha");

        await Put("alpha", "greeting.txt", "Hello, S3Harp!", "text/plain",
            new Dictionary<string, string> { ["note"] = "from-test" });
        var download = await engine.GetObjectAsync("alpha", "greeting.txt", Token);

        Assert.NotNull(download);
        Assert.Equal("Hello, S3Harp!", await ReadContent(download));
        Assert.Equal("text/plain", download.Record.ContentType);
        Assert.Equal("from-test", download.Record.Metadata["note"]);
        Assert.Equal(14, download.Record.Size);
        Assert.Equal(Now, download.Record.LastModified);
    }

    [Fact]
    public async Task Put_ComputesTheMd5ETag()
    {
        await CreateBucket("alpha");

        var result = await Put("alpha", "key", "hello world");

        Assert.Equal(PutObjectStatus.Stored, result.Status);
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", result.ETag);
    }

    [Fact]
    public async Task Put_OverAnExistingKey_LeavesExactlyOneBlobFile()
    {
        await CreateBucket("alpha");

        await Put("alpha", "key", "first version");
        await Put("alpha", "key", "second version");

        Assert.Equal("second version", await ReadContent(
            (await engine.GetObjectAsync("alpha", "key", Token))!));
        Assert.Equal(1, CountBlobFiles());
    }

    [Fact]
    public async Task Put_WhoseConditionFails_ReportsItAndReclaimsTheWrittenBlob()
    {
        await CreateBucket("alpha");
        await Put("alpha", "key", "first version");

        var result = await Put(
            "alpha", "key", "second version",
            condition: new WriteCondition(MustNotMatch: ETagCondition.AnyObject));

        Assert.Equal(PutObjectStatus.PreconditionFailed, result.Status);
        Assert.Equal("first version", await ReadContent(
            (await engine.GetObjectAsync("alpha", "key", Token))!));
        Assert.Equal(1, CountBlobFiles());
    }

    [Fact]
    public async Task Put_IntoAMissingBucket_ReportsItAndStoresNothing()
    {
        var result = await Put("missing", "key", "content");

        Assert.Equal(PutObjectStatus.BucketMissing, result.Status);
        Assert.Equal(0, CountBlobFiles());
    }

    [Fact]
    public async Task Delete_RemovesTheRecordAndTheBlobFile()
    {
        await CreateBucket("alpha");
        await Put("alpha", "key", "content");

        await engine.DeleteObjectAsync("alpha", "key", Token);

        Assert.Null(await engine.GetObjectAsync("alpha", "key", Token));
        Assert.Equal(0, CountBlobFiles());
    }

    [Fact]
    public async Task Get_OfAnUnknownKey_ReturnsNull()
    {
        await CreateBucket("alpha");

        Assert.Null(await engine.GetObjectAsync("alpha", "missing", Token));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task CreateBucket(string name) =>
        Assert.True(await index.TryCreateBucketAsync(name, Now, Token));

    private async Task<PutObjectOutcome> Put(
        string bucket, string key, string content, string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null, WriteCondition? condition = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await engine.PutObjectAsync(
            bucket, key, stream,
            new ObjectAttributes(contentType, ContentHeaders.None, metadata ?? new Dictionary<string, string>()),
            condition, Token);
    }

    private static async Task<string> ReadContent(ObjectDownload download)
    {
        await using (download.Content)
        {
            using var buffer = new MemoryStream();
            await download.Content.CopyToAsync(buffer, Token);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private int CountBlobFiles() =>
        Directory.Exists(Path.Combine(root, "blobs"))
            ? Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories).Count()
            : 0;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
