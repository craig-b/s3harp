using System.Text;
using Xunit;

namespace S3Harp.Core.Tests;

public sealed class MultipartUploadTests : IDisposable
{
    private const string FirstPartETag = "c84cabbaebee9a9631c8be234ac64c26";
    private const string SecondPartETag = "76881423a29bf44fbb150195f6e671ea";
    private const string CombinedETag = "3c4e718dd79097f10b153c92cfded190-2";

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"s3harp-multipart-{Guid.NewGuid():N}");

    private readonly InMemoryMetadataIndex index = new();
    private readonly StorageEngine engine;

    public MultipartUploadTests()
    {
        engine = new StorageEngine(index, new BlobStore(root), new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task InitiateUpload_ReturnsAnUploadId()
    {
        await CreateBucket();

        var uploadId = await engine.InitiateUploadAsync(
            "alpha", "key", "text/plain", new Dictionary<string, string>(), Token);

        Assert.False(string.IsNullOrEmpty(uploadId));
    }

    [Fact]
    public async Task InitiateUpload_IntoAMissingBucket_ReportsIt()
    {
        Assert.Null(await engine.InitiateUploadAsync(
            "missing", "key", null, new Dictionary<string, string>(), Token));
    }

    [Fact]
    public async Task CompletedUpload_ServesTheConcatenatedContentWithTheMultipartETag()
    {
        var uploadId = await StartUpload();
        var first = await UploadPart(uploadId, 1, "Hello, ");
        var second = await UploadPart(uploadId, 2, "S3Harp!");
        Assert.Equal(FirstPartETag, first);
        Assert.Equal(SecondPartETag, second);

        var outcome = await engine.CompleteUploadAsync(
            "alpha", "key", uploadId, [(1, FirstPartETag), (2, SecondPartETag)], Token);

        Assert.Equal(CompleteUploadStatus.Completed, outcome.Status);
        Assert.Equal(CombinedETag, outcome.ETag);
        var download = await engine.GetObjectAsync("alpha", "key", Token);
        Assert.NotNull(download);
        Assert.Equal("Hello, S3Harp!", await ReadContent(download));
        Assert.Equal(CombinedETag, download.Record.ETag);
        Assert.Equal("text/plain", download.Record.ContentType);
        Assert.Equal("from-test", download.Record.Metadata["note"]);
    }

    [Fact]
    public async Task Complete_LeavesOnlyTheAssembledBlobOnDisk()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        await engine.CompleteUploadAsync(
            "alpha", "key", uploadId, [(1, FirstPartETag), (2, SecondPartETag)], Token);

        Assert.Equal(1, CountBlobFiles());
    }

    [Fact]
    public async Task CompletingWithAWrongETag_ReportsInvalidPart()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");

        var outcome = await engine.CompleteUploadAsync(
            "alpha", "key", uploadId, [(1, SecondPartETag)], Token);

        Assert.Equal(CompleteUploadStatus.InvalidPart, outcome.Status);
    }

    [Fact]
    public async Task CompletingWithUnorderedPartNumbers_ReportsInvalidPartOrder()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        var outcome = await engine.CompleteUploadAsync(
            "alpha", "key", uploadId, [(2, SecondPartETag), (1, FirstPartETag)], Token);

        Assert.Equal(CompleteUploadStatus.InvalidPartOrder, outcome.Status);
    }

    [Fact]
    public async Task CompletingAnUnknownUpload_ReportsIt()
    {
        await CreateBucket();

        var outcome = await engine.CompleteUploadAsync(
            "alpha", "key", "missing", [(1, FirstPartETag)], Token);

        Assert.Equal(CompleteUploadStatus.NoSuchUpload, outcome.Status);
    }

    [Fact]
    public async Task AbortedUpload_RemovesEveryPartBlob()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        Assert.True(await engine.AbortUploadAsync("alpha", "key", uploadId, Token));

        Assert.Equal(0, CountBlobFiles());
        Assert.False(await engine.AbortUploadAsync("alpha", "key", uploadId, Token));
    }

    [Fact]
    public async Task DeletingABucket_AbortsItsUploadsAndRemovesTheirParts()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        Assert.Equal(DeleteBucketResult.Deleted, await engine.DeleteBucketAsync("alpha", Token));

        Assert.Equal(0, CountBlobFiles());
        Assert.False(await index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task UploadPart_OnAnUnknownUpload_ReportsIt()
    {
        await CreateBucket();

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        var outcome = await engine.UploadPartAsync("alpha", "key", "missing", 1, content, Token);

        Assert.False(outcome.UploadExists);
    }

    [Fact]
    public async Task CopiedObject_KeepsContentETagAndMetadata()
    {
        await CreateBucket();
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes("hello world")))
        {
            await engine.PutObjectAsync("alpha", "src", content, "text/plain",
                new Dictionary<string, string> { ["note"] = "kept" }, Token);
        }

        var copy = await engine.CopyObjectAsync("alpha", "src", "alpha", "dst", null, Token);

        Assert.NotNull(copy);
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", copy.ETag);
        var download = await engine.GetObjectAsync("alpha", "dst", Token);
        Assert.NotNull(download);
        Assert.Equal("hello world", await ReadContent(download));
        Assert.Equal("kept", download.Record.Metadata["note"]);
        Assert.Equal(2, CountBlobFiles());
    }

    [Fact]
    public async Task CopiedObject_TakesReplacementContentTypeAndMetadata()
    {
        await CreateBucket();
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes("hello world")))
        {
            await engine.PutObjectAsync("alpha", "src", content, "audio/mpeg",
                new Dictionary<string, string> { ["note"] = "old" }, Token);
        }

        var replacement = new ObjectAttributes(
            "audio/ogg", new Dictionary<string, string> { ["note"] = "new" });
        await engine.CopyObjectAsync("alpha", "src", "alpha", "dst", replacement, Token);

        var download = await engine.GetObjectAsync("alpha", "dst", Token);
        Assert.NotNull(download);
        await download.Content.DisposeAsync();
        Assert.Equal("audio/ogg", download.Record.ContentType);
        Assert.Equal("new", download.Record.Metadata["note"]);
    }

    [Fact]
    public async Task CopyingAMissingSource_ReturnsNothing()
    {
        await CreateBucket();

        Assert.Null(await engine.CopyObjectAsync("alpha", "missing", "alpha", "dst", null, Token));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task CreateBucket() =>
        Assert.True(await index.TryCreateBucketAsync("alpha", Now, Token));

    private async Task<string> StartUpload()
    {
        await CreateBucket();
        var uploadId = await engine.InitiateUploadAsync(
            "alpha", "key", "text/plain",
            new Dictionary<string, string> { ["note"] = "from-test" }, Token);
        Assert.NotNull(uploadId);
        return uploadId;
    }

    private async Task<string?> UploadPart(string uploadId, int number, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var outcome = await engine.UploadPartAsync("alpha", "key", uploadId, number, stream, Token);
        Assert.True(outcome.UploadExists);
        return outcome.ETag;
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
        Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
            .Count();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
