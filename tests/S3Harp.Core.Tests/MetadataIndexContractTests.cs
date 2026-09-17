using Xunit;

namespace S3Harp.Core.Tests;

/// <summary>
/// The behavioral contract every metadata index implementation satisfies.
/// Each implementation runs the same tests through a concrete subclass.
/// </summary>
public abstract class MetadataIndexContractTests
{
    private static readonly DateTimeOffset CreationTime = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

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

        var outcome = await Index.DeleteBucketAsync("alpha", Token);

        Assert.Equal(DeleteBucketResult.Deleted, outcome.Status);
        Assert.Empty(outcome.ReleasedBlobIds);
        Assert.False(await Index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownBucket_ReportsItMissing()
    {
        Assert.Equal(
            DeleteBucketResult.NotFound,
            (await Index.DeleteBucketAsync("missing", Token)).Status
        );
    }

    [Fact]
    public async Task DeletingABucketHoldingObjects_ReportsItNotEmpty()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), null, Token);

        Assert.Equal(
            DeleteBucketResult.NotEmpty,
            (await Index.DeleteBucketAsync("alpha", Token)).Status
        );
        Assert.True(await Index.BucketExistsAsync("alpha", Token));
    }

    [Fact]
    public async Task StoredObject_KeepsItsContentHeaders()
    {
        await Create("alpha");
        var headers = new ContentHeaders(
            CacheControl: "max-age=60",
            ContentDisposition: "attachment",
            ContentEncoding: "gzip",
            ContentLanguage: "en",
            Expires: "Thu, 01 Jan 2026 00:00:00 GMT"
        );

        await Index.PutObjectAsync(
            "alpha",
            Record("key", "blob-1") with
            {
                ContentHeaders = headers,
            },
            null,
            Token
        );

        Assert.Equal(headers, (await Index.FindObjectAsync("alpha", "key", Token))?.ContentHeaders);
    }

    [Fact]
    public async Task StoredObject_KeepsItsParts()
    {
        await Create("alpha");
        CompletedPart[] parts = [new(5, "abc="), new(3, null), new(1, "xyz=")];

        await Index.PutObjectAsync(
            "alpha",
            Record("key", "blob-1") with
            {
                Parts = parts,
            },
            null,
            Token
        );

        Assert.Equal(parts, (await Index.FindObjectAsync("alpha", "key", Token))?.Parts);
        Assert.Equal(
            parts,
            Assert.Single(await Index.ScanObjectsAsync("alpha", "", "", 10, Token)).Parts
        );
    }

    [Fact]
    public async Task StoredObject_KeepsItsChecksum()
    {
        await Create("alpha");
        var checksum = new Checksum(ChecksumAlgorithm.Sha256, "abc=-2", ChecksumType.Composite);

        await Index.PutObjectAsync(
            "alpha",
            Record("key", "blob-1") with
            {
                Checksum = checksum,
            },
            null,
            Token
        );
        await Index.PutObjectAsync("alpha", Record("plain", "blob-2"), null, Token);

        Assert.Equal(checksum, (await Index.FindObjectAsync("alpha", "key", Token))?.Checksum);
        Assert.Null((await Index.FindObjectAsync("alpha", "plain", Token))?.Checksum);
    }

    [Fact]
    public async Task Upload_KeepsItsChecksumAlgorithmAndType()
    {
        await Create("alpha");
        var upload = Upload("u1") with
        {
            ChecksumAlgorithm = ChecksumAlgorithm.Crc32C,
            ChecksumType = ChecksumType.FullObject,
        };

        await Index.TryCreateUploadAsync("alpha", upload, Token);

        var found = await Index.FindUploadAsync("alpha", "key", "u1", Token);
        Assert.Equal(ChecksumAlgorithm.Crc32C, found?.ChecksumAlgorithm);
        Assert.Equal(ChecksumType.FullObject, found?.ChecksumType);
        Assert.Equal(
            ChecksumType.FullObject,
            Assert.Single(await Index.ListUploadsAsync("alpha", Token)).ChecksumType
        );
    }

    [Fact]
    public async Task Part_KeepsItsChecksum()
    {
        await StartUpload("alpha", "u1");

        await Index.PutPartAsync(
            "alpha",
            "key",
            "u1",
            Part(1, "blob-1") with
            {
                Checksum = "abc=",
            },
            Token
        );
        await Index.PutPartAsync(
            "alpha",
            "key",
            "u1",
            Part(2, "blob-2") with
            {
                Checksum = null,
            },
            Token
        );

        var parts = await Index.ListPartsAsync("alpha", "key", "u1", Token);
        Assert.Equal(["abc=", null], parts.Select(part => part.Checksum));
    }

    [Fact]
    public async Task Upload_KeepsItsContentHeaders()
    {
        await Create("alpha");
        var headers = new ContentHeaders(ContentEncoding: "gzip");

        await Index.TryCreateUploadAsync(
            "alpha",
            Upload("u1") with
            {
                ContentHeaders = headers,
            },
            Token
        );

        Assert.Equal(
            headers,
            (await Index.FindUploadAsync("alpha", "key", "u1", Token))?.ContentHeaders
        );
        Assert.Equal(
            headers,
            Assert.Single(await Index.ListUploadsAsync("alpha", Token)).ContentHeaders
        );
    }

    [Fact]
    public async Task PutObject_IntoAMissingBucket_IsRefused()
    {
        var result = await Index.PutObjectAsync("missing", Record("key", "blob-1"), null, Token);

        Assert.Equal(PutObjectStatus.BucketMissing, result.Status);
    }

    [Fact]
    public async Task StoredObject_IsRetrievableByItsKey()
    {
        await Create("alpha");

        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), null, Token);
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
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), null, Token);

        var result = await Index.PutObjectAsync("alpha", Record("key", "blob-2"), null, Token);

        Assert.Equal(PutObjectStatus.Stored, result.Status);
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
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), null, Token);

        var result = await Index.DeleteObjectAsync("alpha", "key", null, Token);

        Assert.Equal(new DeleteObjectResult(DeleteObjectStatus.Deleted, "blob-1"), result);
        Assert.Null(await Index.FindObjectAsync("alpha", "key", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownKey_ReportsNotFound()
    {
        await Create("alpha");

        var result = await Index.DeleteObjectAsync("alpha", "missing", null, Token);

        Assert.Equal(DeleteObjectResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteObject_WhoseConditionTheObjectMeets_RemovesIt()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), null, Token);

        var result = await Index.DeleteObjectAsync(
            "alpha",
            "key",
            new DeleteCondition(ETag: "etag-hex", Size: 3),
            Token
        );

        Assert.Equal(new DeleteObjectResult(DeleteObjectStatus.Deleted, "blob-1"), result);
        Assert.Null(await Index.FindObjectAsync("alpha", "key", Token));
    }

    [Fact]
    public async Task DeleteObject_WhoseConditionTheObjectFails_LeavesItUntouched()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-1"), null, Token);

        var result = await Index.DeleteObjectAsync(
            "alpha",
            "key",
            new DeleteCondition(ETag: "other"),
            Token
        );

        Assert.Equal(DeleteObjectResult.PreconditionFailed, result);
        Assert.NotNull(await Index.FindObjectAsync("alpha", "key", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownKey_UnderACondition_ReportsNotFound()
    {
        await Create("alpha");

        var result = await Index.DeleteObjectAsync(
            "alpha",
            "missing",
            new DeleteCondition(ETag: "other"),
            Token
        );

        Assert.Equal(DeleteObjectResult.NotFound, result);
    }

    [Fact]
    public async Task ScanObjects_ReturnsKeysInOrdinalOrderFromTheInclusiveStart()
    {
        await Create("alpha");
        foreach (var key in new[] { "c", "a", "b" })
        {
            await Index.PutObjectAsync("alpha", Record(key, "blob-" + key), null, Token);
        }

        var all = await Index.ScanObjectsAsync("alpha", "", "", 10, Token);
        var fromB = await Index.ScanObjectsAsync("alpha", "", "b", 10, Token);

        Assert.Equal(["a", "b", "c"], all.Select(o => o.Key));
        Assert.Equal(["b", "c"], fromB.Select(o => o.Key));
    }

    [Fact]
    public async Task ScanObjects_ReturnsOnlyKeysUnderThePrefix()
    {
        await Create("alpha");
        foreach (var key in new[] { "logs/1", "logs/2", "logs", "other" })
        {
            await Index.PutObjectAsync("alpha", Record(key, "blob"), null, Token);
        }

        var scanned = await Index.ScanObjectsAsync("alpha", "logs/", "", 10, Token);

        Assert.Equal(["logs/1", "logs/2"], scanned.Select(o => o.Key));
    }

    [Fact]
    public async Task ScanObjects_HonorsTheLimit()
    {
        await Create("alpha");
        foreach (var key in new[] { "a", "b", "c" })
        {
            await Index.PutObjectAsync("alpha", Record(key, "blob"), null, Token);
        }

        var scanned = await Index.ScanObjectsAsync("alpha", "", "", 2, Token);

        Assert.Equal(["a", "b"], scanned.Select(o => o.Key));
    }

    [Fact]
    public async Task ScanObjects_OnAnUnknownBucket_ReturnsNothing()
    {
        Assert.Empty(await Index.ScanObjectsAsync("missing", "", "", 10, Token));
    }

    [Fact]
    public async Task CreateUpload_IntoAMissingBucket_IsRefused()
    {
        Assert.False(await Index.TryCreateUploadAsync("missing", Upload("u1"), Token));
    }

    [Fact]
    public async Task CreatedUpload_IsFindableWithItsFields()
    {
        await Create("alpha");

        Assert.True(await Index.TryCreateUploadAsync("alpha", Upload("u1"), Token));
        var found = await Index.FindUploadAsync("alpha", "key", "u1", Token);

        Assert.NotNull(found);
        Assert.Equal("u1", found.UploadId);
        Assert.Equal("key", found.Key);
        Assert.Equal("text/plain", found.ContentType);
        Assert.Equal("value-1", found.Metadata["meta-1"]);
        Assert.Equal(CreationTime, found.InitiatedAt);
    }

    [Fact]
    public async Task FindingAnUnknownUpload_ReturnsNothing()
    {
        await Create("alpha");

        Assert.Null(await Index.FindUploadAsync("alpha", "key", "missing", Token));
    }

    [Fact]
    public async Task Parts_ListInPartNumberOrder()
    {
        await StartUpload("alpha", "u1");

        await Index.PutPartAsync("alpha", "key", "u1", Part(2, "blob-2"), Token);
        await Index.PutPartAsync("alpha", "key", "u1", Part(1, "blob-1"), Token);
        var parts = await Index.ListPartsAsync("alpha", "key", "u1", Token);

        Assert.Equal([1, 2], parts.Select(p => p.PartNumber));
        Assert.Equal(["blob-1", "blob-2"], parts.Select(p => p.BlobId));
        Assert.Equal(3, parts[0].Size);
        Assert.Equal("part-etag", parts[0].ETag);
    }

    [Fact]
    public async Task Part_KeepsItsLastModifiedTime()
    {
        await StartUpload("alpha", "u1");
        var uploadedAt = new DateTimeOffset(2026, 9, 16, 15, 30, 45, TimeSpan.Zero);

        await Index.PutPartAsync(
            "alpha",
            "key",
            "u1",
            Part(1, "blob-1") with
            {
                LastModified = uploadedAt,
            },
            Token
        );

        Assert.Equal(
            uploadedAt,
            Assert.Single(await Index.ListPartsAsync("alpha", "key", "u1", Token)).LastModified
        );
    }

    [Fact]
    public async Task PutPart_ReplacingAPartNumber_ReturnsTheReplacedBlobId()
    {
        await StartUpload("alpha", "u1");
        await Index.PutPartAsync("alpha", "key", "u1", Part(1, "blob-old"), Token);

        var result = await Index.PutPartAsync("alpha", "key", "u1", Part(1, "blob-new"), Token);

        Assert.True(result.UploadExists);
        Assert.Equal("blob-old", result.ReplacedBlobId);
    }

    [Fact]
    public async Task PutPart_OnAnUnknownUpload_IsRefused()
    {
        await Create("alpha");

        var result = await Index.PutPartAsync("alpha", "key", "missing", Part(1, "blob"), Token);

        Assert.False(result.UploadExists);
    }

    [Fact]
    public async Task DeleteUpload_ReturnsThePartBlobIdsAndRemovesEverything()
    {
        await StartUpload("alpha", "u1");
        await Index.PutPartAsync("alpha", "key", "u1", Part(1, "blob-1"), Token);
        await Index.PutPartAsync("alpha", "key", "u1", Part(2, "blob-2"), Token);

        var blobIds = await Index.DeleteUploadAsync("alpha", "key", "u1", Token);

        Assert.NotNull(blobIds);
        Assert.Equal(["blob-1", "blob-2"], blobIds.Order(StringComparer.Ordinal));
        Assert.Null(await Index.FindUploadAsync("alpha", "key", "u1", Token));
    }

    [Fact]
    public async Task DeletingAnUnknownUpload_ReturnsNothing()
    {
        await Create("alpha");

        Assert.Null(await Index.DeleteUploadAsync("alpha", "key", "missing", Token));
    }

    [Fact]
    public async Task CompleteUpload_StoresTheObjectAndRemovesTheUpload()
    {
        await StartUpload("alpha", "u1");
        await Index.PutPartAsync("alpha", "key", "u1", Part(1, "blob-1"), Token);
        await Index.PutObjectAsync("alpha", Record("key", "blob-old"), null, Token);

        var result = await Index.CompleteUploadAsync(
            "alpha",
            "u1",
            Record("key", "blob-final"),
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.Completed, result.Status);
        Assert.Equal("blob-old", result.ReplacedBlobId);
        Assert.Equal(["blob-1"], result.PartBlobIds);
        Assert.Equal("blob-final", (await Index.FindObjectAsync("alpha", "key", Token))?.BlobId);
        Assert.Null(await Index.FindUploadAsync("alpha", "key", "u1", Token));
    }

    [Fact]
    public async Task PutObject_WhoseConditionTheExistingObjectFails_LeavesItUntouched()
    {
        await Create("alpha");
        await Index.PutObjectAsync("alpha", Record("key", "blob-old"), null, Token);

        var result = await Index.PutObjectAsync(
            "alpha",
            Record("key", "blob-new"),
            new WriteCondition(MustNotMatch: ETagCondition.AnyObject),
            Token
        );

        Assert.Equal(PutObjectStatus.PreconditionFailed, result.Status);
        Assert.Null(result.ReplacedBlobId);
        Assert.Equal("blob-old", (await Index.FindObjectAsync("alpha", "key", Token))?.BlobId);
    }

    [Fact]
    public async Task PutObject_RequiringAnObjectThatIsMissing_ReportsIt()
    {
        await Create("alpha");

        var result = await Index.PutObjectAsync(
            "alpha",
            Record("key", "blob-new"),
            new WriteCondition(MustMatch: ETagCondition.AnyObject),
            Token
        );

        Assert.Equal(PutObjectStatus.ObjectMissing, result.Status);
        Assert.Null(await Index.FindObjectAsync("alpha", "key", Token));
    }

    [Fact]
    public async Task CompleteUpload_WhoseConditionFails_KeepsTheUploadAndItsParts()
    {
        await StartUpload("alpha", "u1");
        await Index.PutPartAsync("alpha", "key", "u1", Part(1, "blob-1"), Token);
        await Index.PutObjectAsync("alpha", Record("key", "blob-old"), null, Token);

        var result = await Index.CompleteUploadAsync(
            "alpha",
            "u1",
            Record("key", "blob-final"),
            new WriteCondition(MustNotMatch: ETagCondition.AnyObject),
            Token
        );

        Assert.Equal(CompleteUploadStatus.PreconditionFailed, result.Status);
        Assert.Equal("blob-old", (await Index.FindObjectAsync("alpha", "key", Token))?.BlobId);
        Assert.NotNull(await Index.FindUploadAsync("alpha", "key", "u1", Token));
        Assert.Single(await Index.ListPartsAsync("alpha", "key", "u1", Token));
    }

    [Fact]
    public async Task CompletingAnUnknownUpload_ReturnsNothing()
    {
        await Create("alpha");

        var result = await Index.CompleteUploadAsync(
            "alpha",
            "missing",
            Record("key", "blob"),
            null,
            Token
        );

        Assert.Equal(CompleteUploadStatus.NoSuchUpload, result.Status);
    }

    [Fact]
    public async Task Uploads_ListInKeyThenUploadIdOrder()
    {
        await Create("alpha");
        foreach (var (key, uploadId) in new[] { ("b", "u2"), ("a", "u9"), ("a", "u1") })
        {
            Assert.True(await Index.TryCreateUploadAsync("alpha", UploadFor(key, uploadId), Token));
        }

        var uploads = await Index.ListUploadsAsync("alpha", Token);

        Assert.Equal(
            [("a", "u1"), ("a", "u9"), ("b", "u2")],
            uploads.Select(u => (u.Key, u.UploadId))
        );
        Assert.Equal(CreationTime, uploads[0].InitiatedAt);
    }

    [Fact]
    public async Task ListingUploads_OfAnUnknownBucket_ReturnsNothing()
    {
        Assert.Empty(await Index.ListUploadsAsync("missing", Token));
    }

    [Fact]
    public async Task DeletingABucketWithActiveUploads_AbortsThemAndReleasesTheirParts()
    {
        await StartUpload("alpha", "u1");
        await Index.PutPartAsync("alpha", "key", "u1", Part(1, "part-1"), Token);
        await Index.PutPartAsync("alpha", "key", "u1", Part(2, "part-2"), Token);

        var outcome = await Index.DeleteBucketAsync("alpha", Token);

        Assert.Equal(DeleteBucketResult.Deleted, outcome.Status);
        Assert.Equal(["part-1", "part-2"], outcome.ReleasedBlobIds.Order());
        Assert.False(await Index.BucketExistsAsync("alpha", Token));
        Assert.Null(await Index.FindUploadAsync("alpha", "key", "u1", Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task StartUpload(string bucket, string uploadId)
    {
        await Create(bucket);
        Assert.True(await Index.TryCreateUploadAsync(bucket, Upload(uploadId), Token));
    }

    private static MultipartUpload Upload(string uploadId) => UploadFor("key", uploadId);

    private static MultipartUpload UploadFor(string key, string uploadId) =>
        new(
            uploadId,
            key,
            "text/plain",
            ContentHeaders.None,
            new Dictionary<string, string> { ["meta-1"] = "value-1" },
            ChecksumAlgorithm.Sha256,
            ChecksumType.Composite,
            CreationTime
        );

    private static PartRecord Part(int number, string blobId) =>
        new(
            number,
            blobId,
            Size: 3,
            ETag: "part-etag",
            Checksum: "part-sum=",
            LastModified: CreationTime
        );

    private static ObjectRecord Record(string key, string blobId) =>
        new(
            key,
            blobId,
            Size: 3,
            ETag: "etag-hex",
            Parts: [],
            Checksum: null,
            ContentType: "text/plain",
            ContentHeaders: ContentHeaders.None,
            Metadata: new Dictionary<string, string> { ["meta-1"] = "value-1" },
            LastModified: CreationTime
        );

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
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"s3harp-test-{Guid.NewGuid():N}.db"
    );

    private readonly SqliteMetadataIndex index;

    public SqliteMetadataIndexTests()
    {
        index = new SqliteMetadataIndex(databasePath);
    }

    protected override IMetadataIndex Index => index;

    [Fact]
    public async Task OpensADatabaseCreatedBeforeContentHeadersExisted()
    {
        index.Dispose();
        File.Delete(databasePath);
        await using (
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={databasePath}"
            )
        )
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE buckets (name TEXT PRIMARY KEY, created_at TEXT NOT NULL);
                CREATE TABLE objects (
                    bucket TEXT NOT NULL REFERENCES buckets(name), key TEXT NOT NULL,
                    blob_id TEXT NOT NULL, size INTEGER NOT NULL, etag TEXT NOT NULL,
                    content_type TEXT, metadata TEXT NOT NULL, last_modified TEXT NOT NULL,
                    PRIMARY KEY (bucket, key)) WITHOUT ROWID;
                CREATE TABLE uploads (
                    upload_id TEXT PRIMARY KEY, bucket TEXT NOT NULL REFERENCES buckets(name),
                    key TEXT NOT NULL, content_type TEXT, metadata TEXT NOT NULL,
                    initiated_at TEXT NOT NULL);
                CREATE TABLE parts (
                    upload_id TEXT NOT NULL REFERENCES uploads(upload_id),
                    part_number INTEGER NOT NULL, blob_id TEXT NOT NULL, size INTEGER NOT NULL,
                    etag TEXT NOT NULL, PRIMARY KEY (upload_id, part_number)) WITHOUT ROWID;
                INSERT INTO buckets VALUES ('alpha', '2026-09-16T12:00:00.000Z');
                INSERT INTO objects VALUES
                    ('alpha', 'old', 'blob-1', 3, 'etag-hex', 'text/plain', '{}', '2026-09-16T12:00:00.000Z');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var upgraded = new SqliteMetadataIndex(databasePath);

        var old = await upgraded.FindObjectAsync(
            "alpha",
            "old",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(ContentHeaders.None, old?.ContentHeaders);
        Assert.Empty(old!.Parts);
        Assert.Null(old.Checksum);
        var stored = await upgraded.PutObjectAsync(
            "alpha",
            new ObjectRecord(
                "new",
                "blob-2",
                Size: 3,
                ETag: "etag-hex",
                Parts: [],
                Checksum: null,
                ContentType: null,
                ContentHeaders: new ContentHeaders(ContentEncoding: "gzip"),
                Metadata: new Dictionary<string, string>(),
                LastModified: DateTimeOffset.UnixEpoch
            ),
            null,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(PutObjectStatus.Stored, stored.Status);
        Assert.Equal(
            "gzip",
            (await upgraded.FindObjectAsync("alpha", "new", TestContext.Current.CancellationToken))
                ?.ContentHeaders
                .ContentEncoding
        );
    }

    [Fact]
    public async Task MigratesPartSizesIntoParts()
    {
        index.Dispose();
        File.Delete(databasePath);
        await using (
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={databasePath}"
            )
        )
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE buckets (name TEXT PRIMARY KEY, created_at TEXT NOT NULL);
                CREATE TABLE objects (
                    bucket TEXT NOT NULL REFERENCES buckets(name), key TEXT NOT NULL,
                    blob_id TEXT NOT NULL, size INTEGER NOT NULL, etag TEXT NOT NULL,
                    content_type TEXT, metadata TEXT NOT NULL, last_modified TEXT NOT NULL,
                    content_headers TEXT NOT NULL DEFAULT '{}',
                    part_sizes TEXT NOT NULL DEFAULT '[]',
                    PRIMARY KEY (bucket, key)) WITHOUT ROWID;
                INSERT INTO buckets VALUES ('alpha', '2026-09-16T00:00:00.0000000+00:00');
                INSERT INTO objects
                    (bucket, key, blob_id, size, etag, metadata, last_modified, part_sizes)
                VALUES
                    ('alpha', 'multi', 'blob-1', 8, 'etag-2', '{}', '2026-09-16T00:00:00.0000000+00:00', '[5,3]'),
                    ('alpha', 'single', 'blob-2', 1, 'etag', '{}', '2026-09-16T00:00:00.0000000+00:00', '[]');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var upgraded = new SqliteMetadataIndex(databasePath);

        var multi = await upgraded.FindObjectAsync(
            "alpha",
            "multi",
            TestContext.Current.CancellationToken
        );
        Assert.Equal([new CompletedPart(5, null), new CompletedPart(3, null)], multi?.Parts);
        var single = await upgraded.FindObjectAsync(
            "alpha",
            "single",
            TestContext.Current.CancellationToken
        );
        Assert.Empty(single!.Parts);
    }

    public void Dispose()
    {
        index.Dispose();
        File.Delete(databasePath);
    }
}
