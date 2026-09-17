using System.Text;
using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Core.Tests;

public sealed class MultipartUploadTests : IDisposable
{
    private const string FirstPartETag = "c84cabbaebee9a9631c8be234ac64c26";
    private const string SecondPartETag = "76881423a29bf44fbb150195f6e671ea";
    private const string CombinedETag = "3c4e718dd79097f10b153c92cfded190-2";

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory root = new("multipart");

    private readonly InMemoryMetadataIndex index = new();
    private readonly StorageEngine engine;

    public MultipartUploadTests()
    {
        engine = new StorageEngine(
            index,
            new BlobStore(root.Path),
            new FixedTimeProvider(Now),
            new StorageLimits(MinimumPartSize: 5)
        );
    }

    [Fact]
    public async Task InitiateUpload_ReturnsAnUploadId()
    {
        await CreateBucket();

        var uploadId = await engine.InitiateUploadAsync(
            "alpha",
            "key",
            new ObjectAttributes(
                "text/plain",
                ContentHeaders.None,
                new Dictionary<string, string>()
            ),
            ChecksumAlgorithm.Crc64Nvme,
            ChecksumType.FullObject,
            Token
        );

        Assert.False(string.IsNullOrEmpty(uploadId));
    }

    [Fact]
    public async Task InitiateUpload_IntoAMissingBucket_ReportsIt()
    {
        Assert.Null(
            await engine.InitiateUploadAsync(
                "missing",
                "key",
                new ObjectAttributes(null, ContentHeaders.None, new Dictionary<string, string>()),
                ChecksumAlgorithm.Crc64Nvme,
                ChecksumType.FullObject,
                Token
            )
        );
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
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag), new(2, SecondPartETag)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.Completed, outcome.Status);
        Assert.Equal(CombinedETag, outcome.ETag);
        var download = await engine.GetObjectAsync("alpha", "key", Token);
        Assert.NotNull(download);
        Assert.Equal("Hello, S3Harp!", await download.ReadContentAsync(Token));
        Assert.Equal(CombinedETag, download.Record.ETag);
        Assert.Equal("text/plain", download.Record.ContentType);
        Assert.Equal("from-test", download.Record.Metadata["note"]);
    }

    [Fact]
    public async Task CompletedUpload_RecordsTheSizeOfEachPartInOrder()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 3, "S3Harp!");

        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag), new(3, SecondPartETag)],
            null,
            null,
            Token
        );

        var record = await index.FindObjectAsync("alpha", "key", Token);
        Assert.Equal([7L, 7L], record?.Parts.Select(part => part.Size));
    }

    [Fact]
    public async Task PutObject_RecordsNoParts()
    {
        await CreateBucket();
        using var content = new MemoryStream("hello"u8.ToArray());

        await engine.PutObjectAsync(
            "alpha",
            "key",
            content,
            new ObjectAttributes(null, ContentHeaders.None, new Dictionary<string, string>()),
            ChecksumAlgorithm.Crc64Nvme,
            null,
            Token
        );

        var record = await index.FindObjectAsync("alpha", "key", Token);
        Assert.NotNull(record);
        Assert.Empty(record.Parts);
    }

    [Fact]
    public async Task CopiedObject_KeepsThePartSizesWithTheMultipartETag()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");
        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag), new(2, SecondPartETag)],
            null,
            null,
            Token
        );

        await engine.CopyObjectAsync("alpha", "key", "alpha", "copy", null, null, Token);

        var record = await index.FindObjectAsync("alpha", "copy", Token);
        Assert.Equal(CombinedETag, record?.ETag);
        Assert.Equal([7L, 7L], record?.Parts.Select(part => part.Size));
    }

    [Fact]
    public async Task UploadedPart_IsStampedWithTheCurrentTime()
    {
        var uploadId = await StartUpload();

        await UploadPart(uploadId, 1, "Hello, ");

        var part = Assert.Single(await index.ListPartsAsync("alpha", "key", uploadId, Token));
        Assert.Equal(Now, part.LastModified);
    }

    [Fact]
    public async Task InitiatedUpload_KeepsItsChecksumAlgorithmAndType()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Sha1, ChecksumType.Composite);

        var upload = await index.FindUploadAsync("alpha", "key", uploadId, Token);

        Assert.Equal(ChecksumAlgorithm.Sha1, upload?.ChecksumAlgorithm);
        Assert.Equal(ChecksumType.Composite, upload?.ChecksumType);
    }

    [Fact]
    public async Task UploadedPart_CarriesItsChecksumInTheUploadsAlgorithm()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Crc32, ChecksumType.Composite);

        using var stream = new MemoryStream("Hello, "u8.ToArray());
        var outcome = await engine.UploadPartAsync("alpha", "key", uploadId, 1, stream, Token);

        Assert.Equal(new ChecksumValue(ChecksumAlgorithm.Crc32, "3ldvBQ=="), outcome.Checksum);
        Assert.Equal(
            "3ldvBQ==",
            Assert.Single(await index.ListPartsAsync("alpha", "key", uploadId, Token)).Checksum
        );
    }

    [Fact]
    public async Task CompletedUpload_CarriesTheCompositeChecksumAndEachPartsChecksum()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Sha256, ChecksumType.Composite);
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [
                new(
                    1,
                    FirstPartETag,
                    new(ChecksumAlgorithm.Sha256, "I0Kb2bqY3VFAMJu5sAlLOq1kJDD/9vs8ph8AjOZE80o=")
                ),
                new(2, SecondPartETag),
            ],
            new ChecksumValue(
                ChecksumAlgorithm.Sha256,
                "sDGBh5Sl/cL+/VEtpYWyKkP3wHD+lmz/q9Wq8TQpY8c=-2"
            ),
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.Completed, outcome.Status);
        var expected = new Checksum(
            ChecksumAlgorithm.Sha256,
            "sDGBh5Sl/cL+/VEtpYWyKkP3wHD+lmz/q9Wq8TQpY8c=-2",
            ChecksumType.Composite
        );
        Assert.Equal(expected, outcome.Checksum);
        var record = await index.FindObjectAsync("alpha", "key", Token);
        Assert.Equal(expected, record?.Checksum);
        Assert.Equal(
            [
                new CompletedPart(7, "I0Kb2bqY3VFAMJu5sAlLOq1kJDD/9vs8ph8AjOZE80o="),
                new CompletedPart(7, "u3yWE0yGgxW/IQ83c7yx8/C9BEoHBn9Z9+ka8q8dzhU="),
            ],
            record?.Parts
        );
    }

    [Fact]
    public async Task CompletedUpload_OfFullObjectType_ChecksumsTheAssembledObject()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Crc32, ChecksumType.FullObject);
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag), new(2, SecondPartETag)],
            null,
            null,
            Token
        );

        Assert.Equal(
            new Checksum(ChecksumAlgorithm.Crc32, "NadAdg==", ChecksumType.FullObject),
            outcome.Checksum
        );
    }

    [Fact]
    public async Task CompletingWithAPartChecksumThatDiffers_ReportsInvalidPart()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Sha256, ChecksumType.Composite);
        await UploadPart(uploadId, 1, "Hello, ");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag, new(ChecksumAlgorithm.Sha256, "bad="))],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.InvalidPart, outcome.Status);
    }

    [Fact]
    public async Task CompletingWithAnExpectedChecksumThatDiffers_ReportsBadDigestAndKeepsTheUpload()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Sha256, ChecksumType.Composite);
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag), new(2, SecondPartETag)],
            new ChecksumValue(ChecksumAlgorithm.Sha256, "bad="),
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.BadDigest, outcome.Status);
        Assert.NotNull(await index.FindUploadAsync("alpha", "key", uploadId, Token));
        Assert.Equal(2, root.CountFiles("blobs"));
    }

    [Fact]
    public async Task CompletingAnUploadAgain_ReturnsTheStoredChecksum()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Crc32, ChecksumType.Composite);
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");
        RequestedPart[] parts = [new(1, FirstPartETag), new(2, SecondPartETag)];

        var first = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            parts,
            null,
            null,
            Token
        );
        var second = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            parts,
            null,
            null,
            Token
        );

        Assert.Equal("5m/Xbg==-2", first.Checksum?.Value);
        Assert.Equal(first.Checksum, second.Checksum);
    }

    [Fact]
    public async Task CopiedPart_TakesTheSourceRangeWithItsETagAndChecksum()
    {
        var uploadId = await StartUpload(ChecksumAlgorithm.Crc32, ChecksumType.Composite);
        await PutSource("Hello, S3Harp!");

        var outcome = await engine.UploadPartCopyAsync(
            "alpha",
            "key",
            uploadId,
            1,
            "alpha",
            "src",
            new ByteRange(7, 13),
            Token
        );

        Assert.Equal(UploadPartCopyStatus.Copied, outcome.Status);
        Assert.Equal(SecondPartETag, outcome.ETag);
        Assert.Equal(new ChecksumValue(ChecksumAlgorithm.Crc32, "0oUPLw=="), outcome.Checksum);
        Assert.Equal(Now, outcome.LastModified);
        var part = Assert.Single(await index.ListPartsAsync("alpha", "key", uploadId, Token));
        Assert.Equal(7, part.Size);
    }

    [Fact]
    public async Task CopiedParts_AssembleIntoTheObject()
    {
        var uploadId = await StartUpload();
        await PutSource("Hello, S3Harp!");

        var first = await engine.UploadPartCopyAsync(
            "alpha",
            "key",
            uploadId,
            1,
            "alpha",
            "src",
            new ByteRange(0, 6),
            Token
        );
        var second = await engine.UploadPartCopyAsync(
            "alpha",
            "key",
            uploadId,
            2,
            "alpha",
            "src",
            null,
            Token
        );
        Assert.NotNull(first.ETag);
        Assert.NotNull(second.ETag);
        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, first.ETag), new(2, second.ETag)],
            null,
            null,
            Token
        );

        var download = await engine.GetObjectAsync("alpha", "key", Token);
        Assert.NotNull(download);
        Assert.Equal("Hello, Hello, S3Harp!", await download.ReadContentAsync(Token));
    }

    [Fact]
    public async Task CopyingAPart_OfAnUnknownUpload_ReportsIt()
    {
        await CreateBucket();
        await PutSource("Hello, S3Harp!");

        var outcome = await engine.UploadPartCopyAsync(
            "alpha",
            "key",
            "missing",
            1,
            "alpha",
            "src",
            null,
            Token
        );

        Assert.Equal(UploadPartCopyStatus.NoSuchUpload, outcome.Status);
        Assert.Equal(1, root.CountFiles("blobs"));
    }

    [Fact]
    public async Task CopyingAPart_FromAMissingSource_ReportsIt()
    {
        var uploadId = await StartUpload();

        var outcome = await engine.UploadPartCopyAsync(
            "alpha",
            "key",
            uploadId,
            1,
            "alpha",
            "missing",
            null,
            Token
        );

        Assert.Equal(UploadPartCopyStatus.SourceMissing, outcome.Status);
    }

    [Fact]
    public async Task CopyingAPart_FromARangeBeyondTheSource_ReportsIt()
    {
        var uploadId = await StartUpload();
        await PutSource("Hello");

        var outcome = await engine.UploadPartCopyAsync(
            "alpha",
            "key",
            uploadId,
            1,
            "alpha",
            "src",
            new ByteRange(0, 21),
            Token
        );

        Assert.Equal(UploadPartCopyStatus.RangeBeyondSource, outcome.Status);
        Assert.Empty(await index.ListPartsAsync("alpha", "key", uploadId, Token));
    }

    [Fact]
    public async Task Complete_LeavesOnlyTheAssembledBlobOnDisk()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag), new(2, SecondPartETag)],
            null,
            null,
            Token
        );

        Assert.Equal(1, root.CountFiles("blobs"));
    }

    [Fact]
    public async Task CompletingWithAWrongETag_ReportsInvalidPart()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, SecondPartETag)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.InvalidPart, outcome.Status);
    }

    [Fact]
    public async Task CompletingWithUnorderedPartNumbers_ReportsInvalidPartOrder()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(2, SecondPartETag), new(1, FirstPartETag)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.InvalidPartOrder, outcome.Status);
    }

    [Fact]
    public async Task CompletingWithAShortNonFinalPart_ReportsEntityTooSmall()
    {
        var uploadId = await StartUpload();
        var tiny = await UploadPart(uploadId, 1, "tiny");
        var second = await UploadPart(uploadId, 2, "S3Harp!");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, tiny), new(2, second)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.EntityTooSmall, outcome.Status);
        Assert.NotNull(await index.FindUploadAsync("alpha", "key", uploadId, Token));
    }

    [Fact]
    public async Task CompletingWithOnlyTheFinalPartShort_Succeeds()
    {
        var uploadId = await StartUpload();
        var first = await UploadPart(uploadId, 1, "Hello, ");
        var tiny = await UploadPart(uploadId, 2, "tiny");

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, first), new(2, tiny)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task CompletingAnUploadAgain_WithTheSameParts_RepeatsTheResult()
    {
        var uploadId = await StartUpload();
        var first = await UploadPart(uploadId, 1, "Hello, ");
        var second = await UploadPart(uploadId, 2, "S3Harp!");
        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, first), new(2, second)],
            null,
            null,
            Token
        );

        var again = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, first), new(2, second)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.Completed, again.Status);
        Assert.Equal(CombinedETag, again.ETag);
        Assert.Equal(1, root.CountFiles("blobs"));
    }

    [Fact]
    public async Task CompletingAnUploadAgain_WithDifferentParts_ReportsNoSuchUpload()
    {
        var uploadId = await StartUpload();
        var first = await UploadPart(uploadId, 1, "Hello, ");
        var second = await UploadPart(uploadId, 2, "S3Harp!");
        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, first), new(2, second)],
            null,
            null,
            Token
        );

        var again = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, first)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.NoSuchUpload, again.Status);
    }

    [Fact]
    public async Task CompletingAnUnknownUpload_ReportsIt()
    {
        await CreateBucket();

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            "missing",
            [new(1, FirstPartETag)],
            null,
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.NoSuchUpload, outcome.Status);
    }

    [Fact]
    public async Task CompletingAnUpload_WhoseConditionFails_KeepsTheUploadAndItsParts()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        using (var content = new MemoryStream("existing"u8.ToArray()))
        {
            await engine.PutObjectAsync(
                "alpha",
                "key",
                content,
                new ObjectAttributes(null, ContentHeaders.None, new Dictionary<string, string>()),
                ChecksumAlgorithm.Crc64Nvme,
                null,
                Token
            );
        }

        var outcome = await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag)],
            null,
            new WriteCondition(MustNotMatch: ETagCondition.AnyObject),
            Token
        );

        Assert.Equal(CompleteUploadStatus.PreconditionFailed, outcome.Status);
        Assert.Equal(
            "existing",
            await (await engine.GetObjectAsync("alpha", "key", Token)).ReadContentAsync(Token)
        );
        Assert.Equal(2, root.CountFiles("blobs"));
        Assert.Single(await index.ListPartsAsync("alpha", "key", uploadId, Token));
    }

    [Fact]
    public async Task AbortedUpload_RemovesEveryPartBlob()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        Assert.True(await engine.AbortUploadAsync("alpha", "key", uploadId, Token));

        Assert.Equal(0, root.CountFiles("blobs"));
        Assert.False(await engine.AbortUploadAsync("alpha", "key", uploadId, Token));
    }

    [Fact]
    public async Task DeletingABucket_AbortsItsUploadsAndRemovesTheirParts()
    {
        var uploadId = await StartUpload();
        await UploadPart(uploadId, 1, "Hello, ");
        await UploadPart(uploadId, 2, "S3Harp!");

        Assert.Equal(DeleteBucketResult.Deleted, await engine.DeleteBucketAsync("alpha", Token));

        Assert.Equal(0, root.CountFiles("blobs"));
        Assert.False(await index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task UploadPart_OnAnUnknownUpload_ReportsIt()
    {
        await CreateBucket();

        using var content = new MemoryStream("data"u8.ToArray());
        var outcome = await engine.UploadPartAsync("alpha", "key", "missing", 1, content, Token);

        Assert.False(outcome.UploadExists);
    }

    [Fact]
    public async Task CopiedObject_KeepsContentETagAndMetadata()
    {
        await CreateBucket();
        using (var content = new MemoryStream("hello world"u8.ToArray()))
        {
            await engine.PutObjectAsync(
                "alpha",
                "src",
                content,
                new ObjectAttributes(
                    "text/plain",
                    ContentHeaders.None,
                    new Dictionary<string, string> { ["note"] = "kept" }
                ),
                ChecksumAlgorithm.Crc64Nvme,
                null,
                Token
            );
        }

        var copy = await engine.CopyObjectAsync("alpha", "src", "alpha", "dst", null, null, Token);

        Assert.NotNull(copy);
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", copy.ETag);
        var download = await engine.GetObjectAsync("alpha", "dst", Token);
        Assert.NotNull(download);
        Assert.Equal("hello world", await download.ReadContentAsync(Token));
        Assert.Equal("kept", download.Record.Metadata["note"]);
        Assert.Equal(2, root.CountFiles("blobs"));
    }

    [Fact]
    public async Task CopiedObject_TakesReplacementContentTypeAndMetadata()
    {
        await CreateBucket();
        using (var content = new MemoryStream("hello world"u8.ToArray()))
        {
            await engine.PutObjectAsync(
                "alpha",
                "src",
                content,
                new ObjectAttributes(
                    "audio/mpeg",
                    ContentHeaders.None,
                    new Dictionary<string, string> { ["note"] = "old" }
                ),
                ChecksumAlgorithm.Crc64Nvme,
                null,
                Token
            );
        }

        var replacement = new ObjectAttributes(
            "audio/ogg",
            new ContentHeaders(ContentLanguage: "eo"),
            new Dictionary<string, string> { ["note"] = "new" }
        );
        await engine.CopyObjectAsync("alpha", "src", "alpha", "dst", replacement, null, Token);

        var download = await engine.GetObjectAsync("alpha", "dst", Token);
        Assert.NotNull(download);
        await download.Content.DisposeAsync();
        Assert.Equal("audio/ogg", download.Record.ContentType);
        Assert.Equal("eo", download.Record.ContentHeaders.ContentLanguage);
        Assert.Equal("new", download.Record.Metadata["note"]);
    }

    [Fact]
    public async Task CompletedUpload_CarriesTheUploadsContentHeaders()
    {
        await CreateBucket();
        var headers = new ContentHeaders(CacheControl: "no-cache", ContentEncoding: "gzip");
        var uploadId = await engine.InitiateUploadAsync(
            "alpha",
            "key",
            new ObjectAttributes("text/plain", headers, new Dictionary<string, string>()),
            ChecksumAlgorithm.Crc64Nvme,
            ChecksumType.FullObject,
            Token
        );
        Assert.NotNull(uploadId);
        await UploadPart(uploadId, 1, "Hello, ");

        await engine.CompleteUploadAsync(
            "alpha",
            "key",
            uploadId,
            [new(1, FirstPartETag)],
            null,
            null,
            Token
        );

        var download = await engine.GetObjectAsync("alpha", "key", Token);
        Assert.NotNull(download);
        await download.Content.DisposeAsync();
        Assert.Equal(headers, download.Record.ContentHeaders);
    }

    [Fact]
    public async Task CopyingAMissingSource_ReturnsNothing()
    {
        await CreateBucket();

        Assert.Null(
            await engine.CopyObjectAsync("alpha", "missing", "alpha", "dst", null, null, Token)
        );
    }

    public void Dispose() => root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task CreateBucket() =>
        Assert.True(await index.TryCreateBucketAsync("alpha", Now, Token));

    private async Task<string> StartUpload(
        ChecksumAlgorithm algorithm = ChecksumAlgorithm.Crc64Nvme,
        ChecksumType type = ChecksumType.FullObject
    )
    {
        await CreateBucket();
        var uploadId = await engine.InitiateUploadAsync(
            "alpha",
            "key",
            new ObjectAttributes(
                "text/plain",
                ContentHeaders.None,
                new Dictionary<string, string> { ["note"] = "from-test" }
            ),
            algorithm,
            type,
            Token
        );
        Assert.NotNull(uploadId);
        return uploadId;
    }

    private async Task PutSource(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var outcome = await engine.PutObjectAsync(
            "alpha",
            "src",
            stream,
            new ObjectAttributes(null, ContentHeaders.None, new Dictionary<string, string>()),
            ChecksumAlgorithm.Crc64Nvme,
            null,
            Token
        );
        Assert.Equal(PutObjectStatus.Stored, outcome.Status);
    }

    private async Task<string> UploadPart(string uploadId, int number, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var outcome = await engine.UploadPartAsync("alpha", "key", uploadId, number, stream, Token);
        Assert.True(outcome.UploadExists);
        Assert.NotNull(outcome.ETag);
        return outcome.ETag;
    }
}
