using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3RequestDispatcherTests
{
    private const string AccessKeyId = "S3HARPEXAMPLEKEY";
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryMetadataIndex index = new();
    private readonly S3RequestDispatcher dispatcher;

    public S3RequestDispatcherTests()
    {
        dispatcher = new S3RequestDispatcher(
            index,
            new RootCredentials(AccessKeyId, "secret"),
            new FixedTimeProvider(Now));
    }

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
    public async Task ObjectOperations_ReportNotImplemented()
    {
        var context = await Dispatch("GET", "/my-bucket/my-key");

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal("NotImplemented", ReadErrorCode(context));
    }

    private async Task<DefaultHttpContext> Dispatch(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var result = await dispatcher.DispatchAsync(context);
        await result.ExecuteAsync(context);
        return context;
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
