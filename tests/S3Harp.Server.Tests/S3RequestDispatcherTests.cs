using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3RequestDispatcherTests : IDisposable
{
    private const string AccessKeyId = "S3HARPEXAMPLEKEY";
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"s3harp-dispatch-{Guid.NewGuid():N}");

    private readonly InMemoryMetadataIndex index = new();
    private readonly S3RequestDispatcher dispatcher;

    public S3RequestDispatcherTests()
    {
        dispatcher = new S3RequestDispatcher(
            index,
            new StorageEngine(index, new BlobStore(root), new FixedTimeProvider(Now)),
            new RootCredentials(AccessKeyId, "secret"),
            new FixedTimeProvider(Now));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task PutBucket_CreatesTheBucket()
    {
        var context = await Dispatch("PUT", "/my-bucket");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(await index.BucketExistsAsync("my-bucket", CancellationToken.None));
    }

    [Fact]
    public async Task PutBucket_OnAnExistingBucket_ReportsTheOwnershipConflict()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("PUT", "/my-bucket");

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("BucketAlreadyOwnedByYou", ReadErrorCode(context));
    }

    [Fact]
    public async Task PutBucket_WithAnInvalidName_IsRejected()
    {
        var context = await Dispatch("PUT", "/ab");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidBucketName", ReadErrorCode(context));
    }

    [Fact]
    public async Task HeadBucket_OnAnExistingBucket_Succeeds()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("HEAD", "/my-bucket");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HeadBucket_OnAnUnknownBucket_Returns404()
    {
        var context = await Dispatch("HEAD", "/my-bucket");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task DeleteBucket_RemovesTheBucket()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("DELETE", "/my-bucket");

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.False(await index.BucketExistsAsync("my-bucket", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteBucket_OnAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("DELETE", "/my-bucket");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Fact]
    public async Task ListBuckets_ReturnsEveryBucketWithOwnerAndCreationTime()
    {
        await Dispatch("PUT", "/zebra");
        await Dispatch("PUT", "/alpha");

        var context = await Dispatch("GET", "/");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context).Root;
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListAllMyBucketsResult", root.Name);
        Assert.Equal(AccessKeyId, root.Element(S3Namespace + "Owner")?.Element(S3Namespace + "ID")?.Value);
        var buckets = root.Element(S3Namespace + "Buckets")?.Elements(S3Namespace + "Bucket").ToArray();
        Assert.NotNull(buckets);
        Assert.Equal(
            ["alpha", "zebra"],
            buckets.Select(b => b.Element(S3Namespace + "Name")?.Value));
        Assert.Equal(
            "2026-09-16T12:00:00.000Z",
            buckets[0].Element(S3Namespace + "CreationDate")?.Value);
    }

    [Fact]
    public async Task ListObjectsV2_ReturnsContentsAndCommonPrefixes()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/a.txt", body: "hello world");
        await Dispatch("PUT", "/my-bucket/docs/one.txt", body: "one");
        await Dispatch("PUT", "/my-bucket/docs/two.txt", body: "two");

        var context = await Dispatch("GET", "/my-bucket", query: "?list-type=2&delimiter=%2F");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context).Root;
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListBucketResult", root.Name);
        Assert.Equal("my-bucket", root.Element(S3Namespace + "Name")?.Value);
        Assert.Equal("2", root.Element(S3Namespace + "KeyCount")?.Value);
        Assert.Equal("false", root.Element(S3Namespace + "IsTruncated")?.Value);
        var contents = Assert.Single(root.Elements(S3Namespace + "Contents"));
        Assert.Equal("a.txt", contents.Element(S3Namespace + "Key")?.Value);
        Assert.Equal("11", contents.Element(S3Namespace + "Size")?.Value);
        Assert.Equal(
            "\"5eb63bbbe01eeed093cb22bb8f5acdc3\"",
            contents.Element(S3Namespace + "ETag")?.Value);
        Assert.Equal(
            "2026-09-16T12:00:00.000Z",
            contents.Element(S3Namespace + "LastModified")?.Value);
        var commonPrefix = Assert.Single(root.Elements(S3Namespace + "CommonPrefixes"));
        Assert.Equal("docs/", commonPrefix.Element(S3Namespace + "Prefix")?.Value);
    }

    [Fact]
    public async Task ListObjectsV2_PaginatesWithContinuationTokens()
    {
        await Dispatch("PUT", "/my-bucket");
        foreach (var key in new[] { "a", "b", "c" })
        {
            await Dispatch("PUT", $"/my-bucket/{key}", body: key);
        }

        var first = ReadBody(await Dispatch(
            "GET", "/my-bucket", query: "?list-type=2&max-keys=2")).Root;
        Assert.NotNull(first);
        Assert.Equal("true", first.Element(S3Namespace + "IsTruncated")?.Value);
        var token = first.Element(S3Namespace + "NextContinuationToken")?.Value;
        Assert.False(string.IsNullOrEmpty(token));

        var second = ReadBody(await Dispatch(
            "GET", "/my-bucket",
            query: $"?list-type=2&max-keys=2&continuation-token={Uri.EscapeDataString(token)}")).Root;
        Assert.NotNull(second);
        Assert.Equal("false", second.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal(
            ["c"],
            second.Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value));
    }

    [Fact]
    public async Task ListObjectsV2_StartAfter_BeginsStrictlyBeyondTheGivenKey()
    {
        await Dispatch("PUT", "/my-bucket");
        foreach (var key in new[] { "a", "b", "c" })
        {
            await Dispatch("PUT", $"/my-bucket/{key}", body: key);
        }

        var root = ReadBody(await Dispatch(
            "GET", "/my-bucket", query: "?list-type=2&start-after=a")).Root;

        Assert.NotNull(root);
        Assert.Equal(
            ["b", "c"],
            root.Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value));
    }

    [Fact]
    public async Task ListObjectsV2_OnAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("GET", "/my-bucket", query: "?list-type=2");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetBucketWithoutListType_ReportsNotImplemented()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("GET", "/my-bucket");

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal("NotImplemented", ReadErrorCode(context));
    }

    [Fact]
    public async Task SubresourceOperations_ReportNotImplemented()
    {
        var context = await Dispatch("POST", "/my-bucket/my-key", query: "?uploads");

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal("NotImplemented", ReadErrorCode(context));
    }

    [Fact]
    public async Task PutObject_StoresTheObjectAndReturnsItsETag()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", context.Response.Headers.ETag);
    }

    [Fact]
    public async Task PutObject_IntoAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObject_RoundtripsContentHeadersAndMetadata()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world",
            configure: request =>
            {
                request.ContentType = "text/plain";
                request.Headers["x-amz-meta-note"] = "from-test";
            });

        var context = await Dispatch("GET", "/my-bucket/greeting.txt");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("hello world", ReadBodyText(context));
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", context.Response.Headers.ETag);
        Assert.Equal("from-test", context.Response.Headers["x-amz-meta-note"]);
        Assert.Equal(11, context.Response.ContentLength);
        Assert.Equal("Wed, 16 Sep 2026 12:00:00 GMT", context.Response.Headers.LastModified);
    }

    [Fact]
    public async Task GetObject_WithAnUnknownKey_ReportsNoSuchKey()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("GET", "/my-bucket/missing.txt");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObject_FromAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("GET", "/my-bucket/missing.txt");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Fact]
    public async Task HeadObject_ReturnsHeadersWithoutABody()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world");

        var context = await Dispatch("HEAD", "/my-bucket/greeting.txt");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(11, context.Response.ContentLength);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", context.Response.Headers.ETag);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task DeleteObject_RemovesTheObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        var context = await Dispatch("DELETE", "/my-bucket/greeting.txt");

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        var after = await Dispatch("GET", "/my-bucket/greeting.txt");
        Assert.Equal("NoSuchKey", ReadErrorCode(after));
    }

    [Fact]
    public async Task DeleteObject_WithAnUnknownKey_StillSucceeds()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("DELETE", "/my-bucket/missing.txt");

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task DeleteBucket_HoldingObjects_ReportsBucketNotEmpty()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        var context = await Dispatch("DELETE", "/my-bucket");

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("BucketNotEmpty", ReadErrorCode(context));
    }

    private async Task<DefaultHttpContext> Dispatch(
        string method,
        string path,
        string? query = null,
        string? body = null,
        Action<HttpRequest>? configure = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        if (body is not null)
        {
            context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        }

        configure?.Invoke(context.Request);
        context.Response.Body = new MemoryStream();
        var result = await dispatcher.DispatchAsync(context);
        await result.ExecuteAsync(context);
        return context;
    }

    private static string ReadBodyText(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static XDocument ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return XDocument.Load(context.Response.Body);
    }

    private static string? ReadErrorCode(DefaultHttpContext context) =>
        ReadBody(context).Root?.Element("Code")?.Value;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
