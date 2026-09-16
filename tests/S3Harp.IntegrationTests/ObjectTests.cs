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
    public async Task PutThenGet_RoundtripsNonAsciiMetadataBytes()
    {
        // S3 metadata values are UTF-8 on the wire: clients such as boto3 send
        // the UTF-8 bytes and expect the same bytes back.
        using var s3 = await CreateClientWithBucket();
        using var rawClient = new HttpClient(new SocketsHttpHandler
        {
            RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
            ResponseHeaderEncodingSelector = (_, _) => Encoding.UTF8,
        });
        var put = new HttpRequestMessage(HttpMethod.Put, await PresignedUrl(s3, HttpVerb.PUT))
        {
            Content = new StringContent("Hello"),
        };
        put.Headers.TryAddWithoutValidation("x-amz-meta-note", "Hello W\u00f6rld\u00e9");

        var stored = await rawClient.SendAsync(put, Token);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var fetched = await rawClient.GetAsync(await PresignedUrl(s3, HttpVerb.GET), Token);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(
            "Hello W\u00f6rld\u00e9", Assert.Single(fetched.Headers.GetValues("x-amz-meta-note")));
    }

    [Fact]
    public async Task CopyObject_WithReplaceDirective_TakesTheNewContentTypeAndMetadata()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "song",
            ContentBody = "content",
            ContentType = "audio/mpeg",
            Metadata = { ["note"] = "old" },
        }, Token);

        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = Bucket,
            SourceKey = "song",
            DestinationBucket = Bucket,
            DestinationKey = "copy",
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "audio/ogg",
            Metadata = { ["note"] = "new" },
        }, Token);
        var copied = await s3.GetObjectMetadataAsync(Bucket, "copy", Token);

        Assert.Equal("audio/ogg", copied.Headers.ContentType);
        Assert.Equal("new", copied.Metadata["note"]);
    }

    [Fact]
    public async Task GetObject_WithAMatchingIfNoneMatch_ReportsNotModified()
    {
        using var s3 = await CreateClientWithBucket();
        var stored = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "greeting.txt",
            ContentBody = "Hello",
        }, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = Bucket,
                Key = "greeting.txt",
                EtagToNotMatch = stored.ETag,
            }, Token));

        Assert.Equal(HttpStatusCode.NotModified, exception.StatusCode);
    }

    [Fact]
    public async Task GetObject_WithAMismatchedIfMatch_ReportsPreconditionFailed()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "greeting.txt",
            ContentBody = "Hello",
        }, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = Bucket,
                Key = "greeting.txt",
                EtagToMatch = "\"ABCORZ\"",
            }, Token));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
        Assert.Equal("PreconditionFailed", exception.ErrorCode);
    }

    [Fact]
    public async Task CopyObject_WithAMismatchedSourceETag_ReportsPreconditionFailed()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "song",
            ContentBody = "content",
        }, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.CopyObjectAsync(
            new CopyObjectRequest
            {
                SourceBucket = Bucket,
                SourceKey = "song",
                DestinationBucket = Bucket,
                DestinationKey = "copy",
                ETagToMatch = "\"ABCORZ\"",
            }, Token));

        Assert.Equal("PreconditionFailed", exception.ErrorCode);
    }

    [Fact]
    public async Task PutObject_WithIfNoneMatchStar_CreatesOnceThenReportsPreconditionFailed()
    {
        using var s3 = await CreateClientWithBucket();
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "once.txt",
            ContentBody = "first",
            IfNoneMatch = "*",
        };

        await s3.PutObjectAsync(request, Token);
        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => s3.PutObjectAsync(request, Token));

        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
        Assert.Equal("PreconditionFailed", exception.ErrorCode);
    }

    [Fact]
    public async Task PutObject_WithAMismatchedIfMatch_ReportsPreconditionFailed()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "guarded.txt",
            ContentBody = "first",
        }, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = "guarded.txt",
                ContentBody = "second",
                IfMatch = "\"ABCORZ\"",
            }, Token));

        Assert.Equal("PreconditionFailed", exception.ErrorCode);
    }

    [Fact]
    public async Task PutThenHead_RoundtripsTheStandardContentHeaders()
    {
        using var s3 = await CreateClientWithBucket();
        var expires = new DateTime(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc);
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "report.txt.gz",
            ContentBody = "content",
        };
        request.Headers.CacheControl = "public, max-age=14400";
        request.Headers.ContentDisposition = "attachment; filename=report.txt";
        request.Headers.ContentEncoding = "gzip";
        request.Headers.ContentLanguage = "en-GB";
        request.Headers.Expires = expires;

        await s3.PutObjectAsync(request, Token);
        var head = await s3.GetObjectMetadataAsync(Bucket, "report.txt.gz", Token);

        Assert.Equal("public, max-age=14400", head.Headers.CacheControl);
        Assert.Equal("attachment; filename=report.txt", head.Headers.ContentDisposition);
        Assert.Equal("gzip", head.Headers.ContentEncoding);
        Assert.Equal("en-GB", head.Headers.ContentLanguage);
        Assert.Equal("Fri, 25 Dec 2026 00:00:00 GMT", head.ExpiresString);
    }

    [Fact]
    public async Task GetObject_WithResponseHeaderOverrides_ServesThemInPlaceOfTheStoredOnes()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "report.txt",
            ContentBody = "content",
            ContentType = "text/plain",
        }, Token);

        using var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = Bucket,
            Key = "report.txt",
            ResponseHeaderOverrides =
            {
                ContentType = "application/x-report",
                ContentDisposition = "attachment; filename=report.txt",
                CacheControl = "no-cache",
            },
        }, Token);

        Assert.Equal("application/x-report", response.Headers.ContentType);
        Assert.Equal("attachment; filename=report.txt", response.Headers.ContentDisposition);
        Assert.Equal("no-cache", response.Headers.CacheControl);
        var head = await s3.GetObjectMetadataAsync(Bucket, "report.txt", Token);
        Assert.Equal("text/plain", head.Headers.ContentType);
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
    public async Task RangedGet_ReturnsExactlyTheRequestedSlice()
    {
        using var s3 = await CreateClientWithBucket();
        var content = RandomNumberGenerator.GetBytes(300 * 1024);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "ranged.bin",
            InputStream = new MemoryStream(content),
        }, Token);

        using var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = Bucket,
            Key = "ranged.bin",
            ByteRange = new ByteRange(0, 99),
        }, Token);

        Assert.Equal(HttpStatusCode.PartialContent, response.HttpStatusCode);
        Assert.Equal(100, response.ContentLength);
        using var received = new MemoryStream();
        await response.ResponseStream.CopyToAsync(received, Token);
        Assert.Equal(content[..100], received.ToArray());
    }

    [Fact]
    public async Task ParallelStyleRangedDownload_ReassemblesTheExactObject()
    {
        using var s3 = await CreateClientWithBucket();
        var content = RandomNumberGenerator.GetBytes(300 * 1024);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "chunked-download.bin",
            InputStream = new MemoryStream(content),
        }, Token);

        using var reassembled = new MemoryStream();
        foreach (var (from, to) in new[] { (0L, 149_999L), (150_000L, 307_199L) })
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Bucket,
                Key = "chunked-download.bin",
                ByteRange = new ByteRange(from, to),
            }, Token);
            await response.ResponseStream.CopyToAsync(reassembled, Token);
        }

        Assert.Equal(content, reassembled.ToArray());
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
    public async Task DeleteObject_WithAFailingCondition_ThrowsPreconditionFailed()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "guarded.txt",
            ContentBody = "content",
        }, Token);

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = Bucket,
                Key = "guarded.txt",
                IfMatch = "\"badetag\"",
            }, Token));

        Assert.Equal("PreconditionFailed", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
        var head = await s3.GetObjectMetadataAsync(Bucket, "guarded.txt", Token);
        await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = Bucket,
            Key = "guarded.txt",
            IfMatchSize = head.ContentLength,
            IfMatchLastModifiedTime = head.LastModified,
        }, Token);
        await Assert.ThrowsAsync<NoSuchKeyException>(
            () => s3.GetObjectAsync(Bucket, "guarded.txt", Token));
    }

    [Fact]
    public async Task DeleteObjects_ReportsAFailedConditionForThatKeyOnly()
    {
        using var s3 = await CreateClientWithBucket();
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "guarded.txt",
            ContentBody = "content",
        }, Token);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = "other.txt",
            ContentBody = "other",
        }, Token);

        var exception = await Assert.ThrowsAsync<DeleteObjectsException>(
            () => s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = Bucket,
                Objects =
                [
                    new KeyVersion { Key = "guarded.txt", ETag = "\"badetag\"" },
                    new KeyVersion { Key = "other.txt", Size = 5 },
                ],
            }, Token));

        var error = Assert.Single(exception.Response.DeleteErrors ?? []);
        Assert.Equal("guarded.txt", error.Key);
        Assert.Equal("PreconditionFailed", error.Code);
        Assert.Equal(
            ["other.txt"], (exception.Response.DeletedObjects ?? []).Select(d => d.Key));
        var retried = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = Bucket,
            Objects = [new KeyVersion { Key = "guarded.txt", ETag = put.ETag }],
        }, Token);
        Assert.Equal(["guarded.txt"], (retried.DeletedObjects ?? []).Select(d => d.Key));
    }

    [Fact]
    public async Task BatchDelete_RemovesEveryListedKeyInOneRequest()
    {
        using var s3 = await CreateClientWithBucket();
        foreach (var key in new[] { "one.txt", "two.txt", "keep.txt" })
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = key,
                ContentBody = key,
            }, Token);
        }

        var response = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = Bucket,
            Objects =
            [
                new KeyVersion { Key = "one.txt" },
                new KeyVersion { Key = "two.txt" },
                new KeyVersion { Key = "never-existed.txt" },
            ],
        }, Token);

        Assert.Equal(3, response.DeletedObjects?.Count);
        Assert.Empty(response.DeleteErrors ?? []);
        var remaining = await s3.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = Bucket }, Token);
        Assert.Equal(["keep.txt"], (remaining.S3Objects ?? []).Select(o => o.Key));
    }

    [Fact]
    public async Task DeleteObjects_RemovesAKeyThatIsOnlyWhitespace()
    {
        using var s3 = await CreateClientWithBucket();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = " ",
            ContentBody = "content",
        }, Token);

        var response = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = Bucket,
            Objects = [new KeyVersion { Key = " " }],
        }, Token);

        Assert.Equal([" "], (response.DeletedObjects ?? []).Select(d => d.Key));
        var remaining = await s3.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = Bucket }, Token);
        Assert.Empty(remaining.S3Objects ?? []);
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

    private static async Task<Uri> PresignedUrl(AmazonS3Client s3, HttpVerb verb) =>
        new(await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = "greeting.txt",
            Verb = verb,
            Protocol = Protocol.HTTP,
            Expires = DateTime.UtcNow.AddMinutes(5),
        }));

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<AmazonS3Client> CreateClientWithBucket()
    {
        var s3 = factory.CreateS3Client();
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, Token);
        return s3;
    }
}
