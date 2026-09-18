using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;

namespace S3Harp.IntegrationTests;

public sealed class MultipartTests : IAsyncLifetime
{
    private const string Bucket = "multipart-bucket";

    private readonly S3HarpFactory factory = new();

    [Fact]
    public async Task MultipartUpload_AssemblesThePartsIntoTheObject()
    {
        using var s3 = await CreateClientWithBucket();
        var firstPart = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var secondPart = RandomNumberGenerator.GetBytes(64 * 1024);

        var initiate = await s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = "assembled.bin",
                ContentType = "application/x-s3harp",
                Metadata = { ["note"] = "multipart" },
            },
            Token
        );
        var uploaded = new List<PartETag>();
        foreach (var (bytes, number) in new[] { (firstPart, 1), (secondPart, 2) })
        {
            var part = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = "assembled.bin",
                    UploadId = initiate.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(bytes),
                },
                Token
            );
            uploaded.Add(new PartETag(number, part.ETag));
        }

        var completed = await s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = "assembled.bin",
                UploadId = initiate.UploadId,
                PartETags = uploaded,
            },
            Token
        );

        Assert.EndsWith("-2\"", completed.ETag, StringComparison.Ordinal);
        using var response = await s3.GetObjectAsync(Bucket, "assembled.bin", Token);
        using var received = new MemoryStream();
        await response.ResponseStream.CopyToAsync(received, Token);
        Assert.Equal([.. firstPart, .. secondPart], received.ToArray());
        Assert.Equal("application/x-s3harp", response.Headers.ContentType);
        Assert.Equal("multipart", response.Metadata["note"]);
    }

    [Fact]
    public async Task GetObject_ByPartNumber_ServesThatPartWithThePartsCount()
    {
        using var s3 = await CreateClientWithBucket();
        var firstPart = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var secondPart = RandomNumberGenerator.GetBytes(64 * 1024);
        var etag = await CompleteTwoPartUpload(s3, "parts.bin", firstPart, secondPart);

        using var response = await s3.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = Bucket,
                Key = "parts.bin",
                PartNumber = 2,
            },
            Token
        );
        using var received = new MemoryStream();
        await response.ResponseStream.CopyToAsync(received, Token);

        Assert.Equal(HttpStatusCode.PartialContent, response.HttpStatusCode);
        Assert.Equal(secondPart, received.ToArray());
        Assert.Equal(2, response.PartsCount);
        Assert.Equal(etag, response.ETag);
        var head = await s3.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = Bucket,
                Key = "parts.bin",
                PartNumber = 1,
            },
            Token
        );
        Assert.Equal(firstPart.Length, head.ContentLength);
        Assert.Equal(2, head.PartsCount);
    }

    [Fact]
    public async Task GetObject_ByAPartNumberBeyondTheLast_ThrowsInvalidPart()
    {
        using var s3 = await CreateClientWithBucket();
        await CompleteTwoPartUpload(s3, "parts.bin", new byte[5 * 1024 * 1024], new byte[1024]);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = Bucket,
                    Key = "parts.bin",
                    PartNumber = 3,
                },
                Token
            )
        );

        Assert.Equal("InvalidPart", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task MultipartUpload_WithSha256_ReportsPartAndCompositeChecksums()
    {
        using var s3 = await CreateClientWithBucket();
        var initiate = await s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = "summed.bin",
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            },
            Token
        );
        var uploaded = new List<PartETag>();
        foreach (
            var (bytes, number) in new[] { (new byte[5 * 1024 * 1024], 1), (new byte[1024], 2) }
        )
        {
            var part = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = "summed.bin",
                    UploadId = initiate.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(bytes),
                    ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                },
                Token
            );
            Assert.NotNull(part.ChecksumSHA256);
            uploaded.Add(new PartETag(number, part.ETag) { ChecksumSHA256 = part.ChecksumSHA256 });
        }

        var completed = await s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = "summed.bin",
                UploadId = initiate.UploadId,
                PartETags = uploaded,
            },
            Token
        );
        var head = await s3.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = Bucket,
                Key = "summed.bin",
                ChecksumMode = ChecksumMode.ENABLED,
            },
            Token
        );
        using var secondPart = await s3.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = Bucket,
                Key = "summed.bin",
                PartNumber = 2,
                ChecksumMode = ChecksumMode.ENABLED,
            },
            Token
        );

        var attributes = await s3.GetObjectAttributesAsync(
            new GetObjectAttributesRequest
            {
                BucketName = Bucket,
                Key = "summed.bin",
                ObjectAttributes =
                [
                    ObjectAttributes.ETag,
                    ObjectAttributes.Checksum,
                    ObjectAttributes.ObjectParts,
                    ObjectAttributes.ObjectSize,
                ],
            },
            Token
        );

        Assert.Equal(ChecksumAlgorithm.SHA256, initiate.ChecksumAlgorithm);
        Assert.Equal(ChecksumType.COMPOSITE, initiate.ChecksumType);
        Assert.Equal(completed.ETag.Trim('"'), attributes.ETag);
        Assert.Equal(completed.ChecksumSHA256, attributes.Checksum.ChecksumSHA256);
        Assert.Equal(2, attributes.ObjectParts.TotalPartsCount);
        Assert.Equal((5 * 1024 * 1024) + 1024, attributes.ObjectSize);
        Assert.Equal(
            uploaded.Select(part => part.ChecksumSHA256),
            (attributes.ObjectParts.Parts ?? []).Select(part => part.ChecksumSHA256)
        );
        Assert.EndsWith("-2", completed.ChecksumSHA256, StringComparison.Ordinal);
        Assert.Equal(ChecksumType.COMPOSITE, completed.ChecksumType);
        Assert.Equal(completed.ChecksumSHA256, head.ChecksumSHA256);
        Assert.Equal(uploaded[1].ChecksumSHA256, secondPart.ChecksumSHA256);
    }

    [Fact]
    public async Task UploadPartCopy_AssemblesAnObjectFromRangesOfAnother()
    {
        using var s3 = await CreateClientWithBucket();
        var source = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "source.bin",
                InputStream = new MemoryStream(source),
            },
            Token
        );
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, "copied.bin", Token);
        var uploaded = new List<PartETag>();
        foreach (
            var (first, last, number) in new[]
            {
                (0L, (5L * 1024 * 1024) - 1, 1),
                (5L * 1024 * 1024, source.LongLength - 1, 2),
            }
        )
        {
            var part = await s3.CopyPartAsync(
                new CopyPartRequest
                {
                    SourceBucket = Bucket,
                    SourceKey = "source.bin",
                    DestinationBucket = Bucket,
                    DestinationKey = "copied.bin",
                    UploadId = initiate.UploadId,
                    PartNumber = number,
                    FirstByte = first,
                    LastByte = last,
                },
                Token
            );
            uploaded.Add(new PartETag(number, part.ETag));
        }

        await s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = "copied.bin",
                UploadId = initiate.UploadId,
                PartETags = uploaded,
            },
            Token
        );

        using var response = await s3.GetObjectAsync(Bucket, "copied.bin", Token);
        using var received = new MemoryStream();
        await response.ResponseStream.CopyToAsync(received, Token);
        Assert.Equal(source, received.ToArray());
    }

    [Fact]
    public async Task CompletingWithANonFinalPartUnderFiveMiB_ThrowsEntityTooSmall()
    {
        using var s3 = await CreateClientWithBucket();
        var upload = await s3.InitiateMultipartUploadAsync(Bucket, "small-parts.bin", Token);
        var parts = new List<PartETag>();
        for (var number = 1; number <= 2; number++)
        {
            var part = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = "small-parts.bin",
                    UploadId = upload.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(new byte[1024]),
                },
                Token
            );
            parts.Add(new PartETag(number, part.ETag));
        }

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = Bucket,
                    Key = "small-parts.bin",
                    UploadId = upload.UploadId,
                    PartETags = parts,
                },
                Token
            )
        );

        Assert.Equal("EntityTooSmall", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task CompletingTwiceWithTheSameParts_SucceedsBothTimes()
    {
        using var s3 = await CreateClientWithBucket();
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, "twice.bin", Token);
        var part = await s3.UploadPartAsync(
            new UploadPartRequest
            {
                BucketName = Bucket,
                Key = "twice.bin",
                UploadId = initiate.UploadId,
                PartNumber = 1,
                InputStream = new MemoryStream(new byte[1024]),
            },
            Token
        );
        var request = new CompleteMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = "twice.bin",
            UploadId = initiate.UploadId,
            PartETags = [new PartETag(1, part.ETag)],
        };

        var first = await s3.CompleteMultipartUploadAsync(request, Token);
        var second = await s3.CompleteMultipartUploadAsync(request, Token);

        Assert.Equal(first.ETag, second.ETag);
    }

    [Fact]
    public async Task AbortedUpload_RefusesCompletion()
    {
        using var s3 = await CreateClientWithBucket();
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, "doomed.bin", Token);
        var part = await s3.UploadPartAsync(
            new UploadPartRequest
            {
                BucketName = Bucket,
                Key = "doomed.bin",
                UploadId = initiate.UploadId,
                PartNumber = 1,
                InputStream = new MemoryStream(new byte[1024]),
            },
            Token
        );

        await s3.AbortMultipartUploadAsync(Bucket, "doomed.bin", initiate.UploadId, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = Bucket,
                    Key = "doomed.bin",
                    UploadId = initiate.UploadId,
                    PartETags = [new PartETag(1, part.ETag)],
                },
                Token
            )
        );
        Assert.Equal("NoSuchUpload", exception.ErrorCode);
    }

    [Fact]
    public async Task ListParts_ReturnsWhatWasUploadedSoFar()
    {
        using var s3 = await CreateClientWithBucket();
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, "inspect.bin", Token);
        var part = await s3.UploadPartAsync(
            new UploadPartRequest
            {
                BucketName = Bucket,
                Key = "inspect.bin",
                UploadId = initiate.UploadId,
                PartNumber = 1,
                InputStream = new MemoryStream(new byte[2048]),
            },
            Token
        );

        var response = await s3.ListPartsAsync(Bucket, "inspect.bin", initiate.UploadId, Token);

        var listed = Assert.Single(response.Parts ?? []);
        Assert.Equal(1, listed.PartNumber);
        Assert.Equal(2048, listed.Size);
        Assert.Equal(part.ETag, listed.ETag);
        Assert.InRange(
            listed.LastModified ?? DateTime.MinValue,
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow.AddMinutes(5)
        );
    }

    [Fact]
    public async Task ListParts_PagesThroughThePartsWithMaxPartsAndAMarker()
    {
        using var s3 = await CreateClientWithBucket();
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, "paged.bin", Token);
        foreach (var number in new[] { 1, 2, 3 })
        {
            await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = "paged.bin",
                    UploadId = initiate.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(new byte[16]),
                },
                Token
            );
        }

        var first = await s3.ListPartsAsync(
            new ListPartsRequest
            {
                BucketName = Bucket,
                Key = "paged.bin",
                UploadId = initiate.UploadId,
                MaxParts = 2,
            },
            Token
        );
        var second = await s3.ListPartsAsync(
            new ListPartsRequest
            {
                BucketName = Bucket,
                Key = "paged.bin",
                UploadId = initiate.UploadId,
                MaxParts = 2,
                PartNumberMarker = "2",
            },
            Token
        );

        Assert.Equal([1, 2], (first.Parts ?? []).Select(p => p.PartNumber));
        Assert.True(first.IsTruncated);
        Assert.Equal(2, first.NextPartNumberMarker);
        Assert.Equal([3], (second.Parts ?? []).Select(p => p.PartNumber));
        Assert.False(second.IsTruncated);
    }

    [Fact]
    public async Task ListMultipartUploads_ShowsInProgressUploads()
    {
        using var s3 = await CreateClientWithBucket();
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, "pending.bin", Token);

        var response = await s3.ListMultipartUploadsAsync(
            new ListMultipartUploadsRequest { BucketName = Bucket },
            Token
        );

        var upload = Assert.Single(response.MultipartUploads ?? []);
        Assert.Equal("pending.bin", upload.Key);
        Assert.Equal(initiate.UploadId, upload.UploadId);
    }

    [Fact]
    public async Task CopyingAnObjectOntoItself_ThrowsInvalidRequest()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "same.txt",
                ContentBody = "hello",
            },
            Token
        );

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            s3.CopyObjectAsync(Bucket, "same.txt", Bucket, "same.txt", Token)
        );

        Assert.Equal("InvalidRequest", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task CopiedObject_MatchesTheSourceContentAndETag()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "src.txt",
                ContentBody = "hello world",
                Metadata = { ["note"] = "kept" },
            },
            Token
        );

        var copy = await s3.CopyObjectAsync(Bucket, "src.txt", Bucket, "dst.txt", Token);

        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", copy.ETag);
        using var response = await s3.GetObjectAsync(Bucket, "dst.txt", Token);
        using var reader = new StreamReader(response.ResponseStream);
        Assert.Equal("hello world", await reader.ReadToEndAsync(Token));
        Assert.Equal("kept", response.Metadata["note"]);
    }

    public ValueTask InitializeAsync() => factory.StartAsync();

    public ValueTask DisposeAsync() => factory.DisposeAsync();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Uploads and completes the two parts, returning the object's ETag.</summary>
    private static async Task<string> CompleteTwoPartUpload(
        AmazonS3Client s3,
        string key,
        byte[] firstPart,
        byte[] secondPart
    )
    {
        var initiate = await s3.InitiateMultipartUploadAsync(Bucket, key, Token);
        var uploaded = new List<PartETag>();
        foreach (var (bytes, number) in new[] { (firstPart, 1), (secondPart, 2) })
        {
            var part = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = Bucket,
                    Key = key,
                    UploadId = initiate.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(bytes),
                },
                Token
            );
            uploaded.Add(new PartETag(number, part.ETag));
        }

        var completed = await s3.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = initiate.UploadId,
                PartETags = uploaded,
            },
            Token
        );
        return completed.ETag;
    }

    private async Task<AmazonS3Client> CreateClientWithBucket()
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        return s3;
    }
}
