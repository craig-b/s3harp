using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using S3Harp.Core;
using S3Harp.Server.Authentication;
using S3Harp.TestSupport;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class S3RequestDispatcherTests : IDisposable
{
    private const string AccessKeyId = "S3HARPEXAMPLEKEY";
    private const string SecondPartMd5 = "76881423a29bf44fbb150195f6e671ea";
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory root = new("dispatch");

    private readonly InMemoryMetadataIndex index = new();
    private readonly S3RequestDispatcher dispatcher;

    public S3RequestDispatcherTests()
    {
        dispatcher = new S3RequestDispatcher(
            index,
            new StorageEngine(
                index,
                new BlobStore(root.Path),
                new FixedTimeProvider(Now),
                new StorageLimits(MinimumPartSize: 5)
            ),
            new RootCredentials(AccessKeyId, "secret"),
            new FixedTimeProvider(Now),
            new ServiceDomain("localhost")
        );
    }

    public void Dispose() => root.Dispose();

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
        var root = ReadBody(context);
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListAllMyBucketsResult", root.Name);
        Assert.Equal(
            AccessKeyId,
            root.Element(S3Namespace + "Owner")?.Element(S3Namespace + "ID")?.Value
        );
        var buckets = root.Element(S3Namespace + "Buckets")
            ?.Elements(S3Namespace + "Bucket")
            .ToArray();
        Assert.NotNull(buckets);
        Assert.Equal(
            ["alpha", "zebra"],
            buckets.Select(b => b.Element(S3Namespace + "Name")?.Value)
        );
        Assert.Equal(
            "2026-09-16T12:00:00.000Z",
            buckets[0].Element(S3Namespace + "CreationDate")?.Value
        );
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
        var root = ReadBody(context);
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
            contents.Element(S3Namespace + "ETag")?.Value
        );
        Assert.Equal(
            "2026-09-16T12:00:00.000Z",
            contents.Element(S3Namespace + "LastModified")?.Value
        );
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

        var first = ReadBody(await Dispatch("GET", "/my-bucket", query: "?list-type=2&max-keys=2"));
        Assert.NotNull(first);
        Assert.Equal("true", first.Element(S3Namespace + "IsTruncated")?.Value);
        var token = first.Element(S3Namespace + "NextContinuationToken")?.Value;
        Assert.False(string.IsNullOrEmpty(token));

        var second = ReadBody(
            await Dispatch(
                "GET",
                "/my-bucket",
                query: $"?list-type=2&max-keys=2&continuation-token={Uri.EscapeDataString(token)}"
            )
        );
        Assert.NotNull(second);
        Assert.Equal("false", second.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal(
            ["c"],
            second
                .Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjectsV2_StartAfter_BeginsStrictlyBeyondTheGivenKey()
    {
        await Dispatch("PUT", "/my-bucket");
        foreach (var key in new[] { "a", "b", "c" })
        {
            await Dispatch("PUT", $"/my-bucket/{key}", body: key);
        }

        var root = ReadBody(
            await Dispatch("GET", "/my-bucket", query: "?list-type=2&start-after=a")
        );

        Assert.NotNull(root);
        Assert.Equal(
            ["b", "c"],
            root.Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjectsV2_WithUrlEncoding_EncodesKeysAndEchoesTheEncodingType()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/plus+and space.txt", body: "1");
        await Dispatch("PUT", "/my-bucket/docs/nested key.txt", body: "2");

        var context = await Dispatch(
            "GET",
            "/my-bucket",
            query: "?list-type=2&encoding-type=url&delimiter=%2F"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context);
        Assert.NotNull(root);
        Assert.Equal("url", root.Element(S3Namespace + "EncodingType")?.Value);
        var contents = Assert.Single(root.Elements(S3Namespace + "Contents"));
        Assert.Equal("plus%2Band%20space.txt", contents.Element(S3Namespace + "Key")?.Value);
        var commonPrefix = Assert.Single(root.Elements(S3Namespace + "CommonPrefixes"));
        Assert.Equal("docs/", commonPrefix.Element(S3Namespace + "Prefix")?.Value);
    }

    [Theory]
    [InlineData("+2")]
    [InlineData(" 2")]
    public async Task ListObjectsV2_WithALooselyFormattedMaxKeys_ReportsInvalidArgument(
        string maxKeys
    )
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "GET",
            "/my-bucket",
            query: $"?list-type=2&max-keys={Uri.EscapeDataString(maxKeys)}"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task ListObjectsV2_WithAnUnknownEncodingType_ReportsInvalidArgument()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "GET",
            "/my-bucket",
            query: "?list-type=2&encoding-type=base64"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task ListObjectsV2_OnAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("GET", "/my-bucket", query: "?list-type=2");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Fact]
    public async Task ListObjects_ReturnsContentsCommonPrefixesAndTheMarkerShape()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/a.txt", body: "hello world");
        await Dispatch("PUT", "/my-bucket/docs/one.txt", body: "one");

        var context = await Dispatch("GET", "/my-bucket", query: "?delimiter=%2F");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context);
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListBucketResult", root.Name);
        Assert.Equal("my-bucket", root.Element(S3Namespace + "Name")?.Value);
        Assert.Equal("", root.Element(S3Namespace + "Marker")?.Value);
        Assert.Equal("1000", root.Element(S3Namespace + "MaxKeys")?.Value);
        Assert.Equal("false", root.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Null(root.Element(S3Namespace + "KeyCount"));
        var contents = Assert.Single(root.Elements(S3Namespace + "Contents"));
        Assert.Equal("a.txt", contents.Element(S3Namespace + "Key")?.Value);
        var commonPrefix = Assert.Single(root.Elements(S3Namespace + "CommonPrefixes"));
        Assert.Equal("docs/", commonPrefix.Element(S3Namespace + "Prefix")?.Value);
    }

    [Fact]
    public async Task ListObjects_PaginatesWithMarkers()
    {
        await Dispatch("PUT", "/my-bucket");
        foreach (var key in new[] { "a", "b", "c" })
        {
            await Dispatch("PUT", $"/my-bucket/{key}", body: key);
        }

        var first = ReadBody(await Dispatch("GET", "/my-bucket", query: "?max-keys=2"));
        Assert.NotNull(first);
        Assert.Equal("true", first.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Null(first.Element(S3Namespace + "NextMarker"));

        var second = ReadBody(await Dispatch("GET", "/my-bucket", query: "?max-keys=2&marker=b"));
        Assert.NotNull(second);
        Assert.Equal("b", second.Element(S3Namespace + "Marker")?.Value);
        Assert.Equal("false", second.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal(
            ["c"],
            second
                .Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjects_WithADelimiter_ReturnsNextMarkerAndResumesPastTheGroup()
    {
        await Dispatch("PUT", "/my-bucket");
        foreach (var key in new[] { "a", "docs/one", "docs/two", "z" })
        {
            await Dispatch("PUT", $"/my-bucket/{key}", body: key);
        }

        var first = ReadBody(
            await Dispatch("GET", "/my-bucket", query: "?max-keys=2&delimiter=%2F")
        );
        Assert.NotNull(first);
        Assert.Equal("true", first.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal("docs/", first.Element(S3Namespace + "NextMarker")?.Value);

        var second = ReadBody(
            await Dispatch("GET", "/my-bucket", query: "?max-keys=2&delimiter=%2F&marker=docs%2F")
        );
        Assert.NotNull(second);
        Assert.Empty(second.Elements(S3Namespace + "CommonPrefixes"));
        Assert.Equal(
            ["z"],
            second
                .Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjectVersions_ReportsEachObjectAsItsOnlyVersion()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/a.txt", body: "hello world");
        await Dispatch("PUT", "/my-bucket/docs/one.txt", body: "one");

        var context = await Dispatch("GET", "/my-bucket", query: "?versions&delimiter=%2F");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context);
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListVersionsResult", root.Name);
        Assert.Equal("my-bucket", root.Element(S3Namespace + "Name")?.Value);
        Assert.Equal("", root.Element(S3Namespace + "KeyMarker")?.Value);
        Assert.Equal("", root.Element(S3Namespace + "VersionIdMarker")?.Value);
        Assert.Equal("1000", root.Element(S3Namespace + "MaxKeys")?.Value);
        Assert.Equal("false", root.Element(S3Namespace + "IsTruncated")?.Value);
        var version = Assert.Single(root.Elements(S3Namespace + "Version"));
        Assert.Equal("a.txt", version.Element(S3Namespace + "Key")?.Value);
        Assert.Equal("null", version.Element(S3Namespace + "VersionId")?.Value);
        Assert.Equal("true", version.Element(S3Namespace + "IsLatest")?.Value);
        Assert.Equal("11", version.Element(S3Namespace + "Size")?.Value);
        Assert.Equal(
            "\"5eb63bbbe01eeed093cb22bb8f5acdc3\"",
            version.Element(S3Namespace + "ETag")?.Value
        );
        Assert.Equal(
            "2026-09-16T12:00:00.000Z",
            version.Element(S3Namespace + "LastModified")?.Value
        );
        Assert.Equal(
            AccessKeyId,
            version.Element(S3Namespace + "Owner")?.Element(S3Namespace + "ID")?.Value
        );
        Assert.Empty(root.Elements(S3Namespace + "DeleteMarker"));
        var commonPrefix = Assert.Single(root.Elements(S3Namespace + "CommonPrefixes"));
        Assert.Equal("docs/", commonPrefix.Element(S3Namespace + "Prefix")?.Value);
    }

    [Fact]
    public async Task ListObjectVersions_PaginatesWithKeyMarkersPastDelimiterGroups()
    {
        await Dispatch("PUT", "/my-bucket");
        foreach (var key in new[] { "a", "docs/one", "docs/two", "z" })
        {
            await Dispatch("PUT", $"/my-bucket/{key}", body: key);
        }

        var first = ReadBody(
            await Dispatch("GET", "/my-bucket", query: "?versions&max-keys=2&delimiter=%2F")
        );
        Assert.NotNull(first);
        Assert.Equal("true", first.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal("docs/", first.Element(S3Namespace + "NextKeyMarker")?.Value);
        Assert.Equal("null", first.Element(S3Namespace + "NextVersionIdMarker")?.Value);

        var second = ReadBody(
            await Dispatch(
                "GET",
                "/my-bucket",
                query: "?versions&max-keys=2&delimiter=%2F&key-marker=docs%2F&version-id-marker=null"
            )
        );
        Assert.NotNull(second);
        Assert.Equal("docs/", second.Element(S3Namespace + "KeyMarker")?.Value);
        Assert.Equal("null", second.Element(S3Namespace + "VersionIdMarker")?.Value);
        Assert.Equal("false", second.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Empty(second.Elements(S3Namespace + "CommonPrefixes"));
        Assert.Equal(
            ["z"],
            second
                .Elements(S3Namespace + "Version")
                .Select(v => v.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjectVersions_OnAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("GET", "/my-bucket", query: "?versions");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Fact]
    public async Task ObjectKeysEndingInASlash_KeepTheSlash()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/folder/", body: "");

        var get = await Dispatch("GET", "/my-bucket/folder/");
        Assert.Equal(StatusCodes.Status200OK, get.Response.StatusCode);
        var listing = ReadBody(await Dispatch("GET", "/my-bucket", query: "?list-type=2"));
        Assert.NotNull(listing);
        Assert.Equal(
            ["folder/"],
            listing
                .Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjectsV2_EchoesAnEmptyContinuationToken()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/a.txt", body: "a");

        var root = ReadBody(
            await Dispatch("GET", "/my-bucket", query: "?list-type=2&continuation-token=")
        );

        Assert.NotNull(root);
        Assert.Equal("", root.Element(S3Namespace + "ContinuationToken")?.Value);
        Assert.Equal("false", root.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal(
            ["a.txt"],
            root.Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
    }

    [Fact]
    public async Task ListObjectsV2_IncludesOwnersOnlyWhenAsked()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/a.txt", body: "a");

        var plain = ReadBody(await Dispatch("GET", "/my-bucket", query: "?list-type=2"));
        var withOwner = ReadBody(
            await Dispatch("GET", "/my-bucket", query: "?list-type=2&fetch-owner=true")
        );

        Assert.Null(plain?.Element(S3Namespace + "Contents")?.Element(S3Namespace + "Owner"));
        var owner = withOwner?.Element(S3Namespace + "Contents")?.Element(S3Namespace + "Owner");
        Assert.Equal(AccessKeyId, owner?.Element(S3Namespace + "ID")?.Value);
        Assert.Equal(AccessKeyId, owner?.Element(S3Namespace + "DisplayName")?.Value);
    }

    [Fact]
    public async Task ListObjects_AlwaysIncludesOwners()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/a.txt", body: "a");

        var root = ReadBody(await Dispatch("GET", "/my-bucket"));

        var owner = root?.Element(S3Namespace + "Contents")?.Element(S3Namespace + "Owner");
        Assert.Equal(AccessKeyId, owner?.Element(S3Namespace + "ID")?.Value);
    }

    [Fact]
    public async Task ListObjects_OnAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch("GET", "/my-bucket");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("PUT", "/my-bucket", "?versioning")]
    [InlineData("GET", "/my-bucket/key", "?retention")]
    [InlineData("GET", "/my-bucket", "?publicAccessBlock")]
    public async Task SubresourceOperations_ReportNotImplemented(
        string method,
        string path,
        string query
    )
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "content");

        var context = await Dispatch(method, path, query: query);

        Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        Assert.Equal("NotImplemented", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObjectAttributes_ReportsTheRequestedAttributesOfASinglePieceObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "Hello, S3Harp!");

        var context = await Dispatch(
            "GET",
            "/my-bucket/key",
            query: "?attributes",
            configure: request =>
                request.Headers["x-amz-object-attributes"] =
                    "ETag, Checksum, ObjectParts, StorageClass, ObjectSize"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("Wed, 16 Sep 2026 12:00:00 GMT", context.Response.Headers.LastModified);
        var root = ReadBody(context);
        Assert.Equal(S3Namespace + "GetObjectAttributesResponse", root.Name);
        Assert.Equal("d6f1f9b294570683440503af6883416c", root.Element(S3Namespace + "ETag")?.Value);
        var checksum = root.Required(S3Namespace + "Checksum");
        Assert.Equal("v+mfzPLqhcw=", checksum.Element(S3Namespace + "ChecksumCRC64NVME")?.Value);
        Assert.Equal("FULL_OBJECT", checksum.Element(S3Namespace + "ChecksumType")?.Value);
        Assert.Null(root.Element(S3Namespace + "ObjectParts"));
        Assert.Equal("STANDARD", root.Element(S3Namespace + "StorageClass")?.Value);
        Assert.Equal("14", root.Element(S3Namespace + "ObjectSize")?.Value);
    }

    [Fact]
    public async Task GetObjectAttributes_OfAMultipartObject_ListsItsPartsAndPagesThem()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate(
            "/my-bucket/key",
            request => request.Headers["x-amz-checksum-algorithm"] = "SHA256"
        );
        var first = await UploadPart("/my-bucket/key", uploadId, 1, "Hello, ");
        var second = await UploadPart("/my-bucket/key", uploadId, 2, "S3Harp!");
        await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: $"""
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>{first}</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>{second}</ETag></Part>
            </CompleteMultipartUpload>
            """
        );

        var firstPage = ReadBody(
                await Dispatch(
                    "GET",
                    "/my-bucket/key",
                    query: "?attributes",
                    configure: request =>
                    {
                        request.Headers["x-amz-object-attributes"] = "ObjectParts";
                        request.Headers["x-amz-max-parts"] = "1";
                    }
                )
            )
            .Required(S3Namespace + "ObjectParts");
        var secondPage = ReadBody(
                await Dispatch(
                    "GET",
                    "/my-bucket/key",
                    query: "?attributes",
                    configure: request =>
                    {
                        request.Headers["x-amz-object-attributes"] = "ObjectParts";
                        request.Headers["x-amz-part-number-marker"] = "1";
                    }
                )
            )
            .Required(S3Namespace + "ObjectParts");

        Assert.Equal("2", firstPage.Element(S3Namespace + "PartsCount")?.Value);
        Assert.Equal("1", firstPage.Element(S3Namespace + "MaxParts")?.Value);
        Assert.Equal("0", firstPage.Element(S3Namespace + "PartNumberMarker")?.Value);
        Assert.Equal("true", firstPage.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal("1", firstPage.Element(S3Namespace + "NextPartNumberMarker")?.Value);
        var part = Assert.Single(firstPage.Elements(S3Namespace + "Part"));
        Assert.Equal("1", part.Element(S3Namespace + "PartNumber")?.Value);
        Assert.Equal("7", part.Element(S3Namespace + "Size")?.Value);
        Assert.Equal(
            "I0Kb2bqY3VFAMJu5sAlLOq1kJDD/9vs8ph8AjOZE80o=",
            part.Element(S3Namespace + "ChecksumSHA256")?.Value
        );
        Assert.Equal("1", secondPage.Element(S3Namespace + "PartNumberMarker")?.Value);
        Assert.Equal("false", secondPage.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Null(secondPage.Element(S3Namespace + "NextPartNumberMarker"));
        Assert.Equal(
            ["2"],
            secondPage
                .Elements(S3Namespace + "Part")
                .Select(p => p.Element(S3Namespace + "PartNumber")?.Value)
        );
    }

    [Fact]
    public async Task GetObjectAttributes_ReturnsOnlyTheAttributesAskedFor()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "Hello, S3Harp!");

        var root = ReadBody(
            await Dispatch(
                "GET",
                "/my-bucket/key",
                query: "?attributes",
                configure: request => request.Headers["x-amz-object-attributes"] = "ObjectSize"
            )
        );

        Assert.Equal(["ObjectSize"], root.Elements().Select(e => e.Name.LocalName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ETag, Colour")]
    public async Task GetObjectAttributes_WithoutUsableAttributes_ReportsInvalidArgument(
        string? header
    )
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "Hello, S3Harp!");

        var context = await Dispatch(
            "GET",
            "/my-bucket/key",
            query: "?attributes",
            configure: request =>
            {
                if (header is not null)
                {
                    request.Headers["x-amz-object-attributes"] = header;
                }
            }
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObjectAttributes_OfAMissingKey_ReportsNoSuchKey()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "GET",
            "/my-bucket/missing",
            query: "?attributes",
            configure: request => request.Headers["x-amz-object-attributes"] = "ETag"
        );

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(context));
    }

    [Fact]
    public async Task DeleteObjects_RemovesEveryListedKey()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/one.txt", body: "1");
        await Dispatch("PUT", "/my-bucket/two.txt", body: "2");
        await Dispatch("PUT", "/my-bucket/keep.txt", body: "3");

        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: """
            <Delete>
              <Object><Key>one.txt</Key></Object>
              <Object><Key>two.txt</Key></Object>
              <Object><Key>never-existed.txt</Key></Object>
            </Delete>
            """
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var result = ReadBody(context);
        Assert.Equal(S3Namespace + "DeleteResult", result?.Name);
        Assert.Equal(
            ["never-existed.txt", "one.txt", "two.txt"],
            result!
                .Elements(S3Namespace + "Deleted")
                .Select(d => d.Element(S3Namespace + "Key")?.Value)
                .Order(StringComparer.Ordinal)
        );
        Assert.Equal("NoSuchKey", ReadErrorCode(await Dispatch("GET", "/my-bucket/one.txt")));
        Assert.Equal(
            StatusCodes.Status200OK,
            (await Dispatch("GET", "/my-bucket/keep.txt")).Response.StatusCode
        );
    }

    [Fact]
    public async Task DeleteObjects_InQuietMode_ReportsNothingForSuccesses()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/one.txt", body: "1");

        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: """
            <Delete>
              <Quiet>true</Quiet>
              <Object><Key>one.txt</Key></Object>
            </Delete>
            """
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(ReadBody(context).Elements(S3Namespace + "Deleted"));
        Assert.Equal("NoSuchKey", ReadErrorCode(await Dispatch("GET", "/my-bucket/one.txt")));
    }

    [Fact]
    public async Task DeleteObjects_OnAnUnknownBucket_ReportsNoSuchBucket()
    {
        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: "<Delete><Object><Key>k</Key></Object></Delete>"
        );

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchBucket", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("<Delete><Object><Size>1</Size></Object></Delete>")]
    [InlineData("<Delete><Object/></Delete>")]
    public async Task DeleteObjects_WithAnEntryLackingAKey_ReportsMalformedXml(string body)
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("POST", "/my-bucket", query: "?delete", body: body);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
    }

    [Fact]
    public async Task DeleteObjects_WithAMalformedBody_ReportsMalformedXml()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("POST", "/my-bucket", query: "?delete", body: "not xml");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
    }

    [Fact]
    public async Task DeleteObjects_WithMoreThanAThousandKeys_ReportsMalformedXml()
    {
        await Dispatch("PUT", "/my-bucket");
        var objects = string.Concat(
            Enumerable.Range(0, 1001).Select(i => $"<Object><Key>k{i}</Key></Object>")
        );

        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: $"<Delete>{objects}</Delete>"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
    }

    [Fact]
    public async Task MultipartLifecycle_AssemblesThePartsIntoTheObject()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/assembled.txt");
        var firstETag = await UploadPart("/my-bucket/assembled.txt", uploadId, 1, "Hello, ");
        var secondETag = await UploadPart("/my-bucket/assembled.txt", uploadId, 2, "S3Harp!");

        var complete = await Dispatch(
            "POST",
            "/my-bucket/assembled.txt",
            query: $"?uploadId={uploadId}",
            body: $"""
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>{firstETag}</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>{secondETag}</ETag></Part>
            </CompleteMultipartUpload>
            """
        );

        Assert.Equal(StatusCodes.Status200OK, complete.Response.StatusCode);
        var result = ReadBody(complete);
        Assert.Equal(S3Namespace + "CompleteMultipartUploadResult", result?.Name);
        Assert.Equal(
            "\"3c4e718dd79097f10b153c92cfded190-2\"",
            result?.Element(S3Namespace + "ETag")?.Value
        );
        var download = await Dispatch("GET", "/my-bucket/assembled.txt");
        Assert.Equal("Hello, S3Harp!", ReadBodyText(download));
    }

    [Fact]
    public async Task CompletingAnUnknownUpload_ReportsNoSuchUpload()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: "?uploadId=missing",
            body: "<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>x</ETag></Part></CompleteMultipartUpload>"
        );

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchUpload", ReadErrorCode(context));
    }

    [Fact]
    public async Task CompletingWithAMalformedBody_ReportsMalformedXml()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: "not xml at all"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
    }

    [Fact]
    public async Task UploadPartWithAnInvalidPartNumber_ReportsInvalidArgument()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=0&uploadId={uploadId}",
            body: "data"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public async Task UploadPartWithALooselyFormattedPartNumber_ReportsInvalidArgument(
        string partNumber
    )
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber={Uri.EscapeDataString(partNumber)}&uploadId={uploadId}",
            body: "data"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("<Part><ETag>\"etag\"</ETag></Part>")]
    [InlineData("<Part><PartNumber>1</PartNumber></Part>")]
    [InlineData("<Part/>")]
    public async Task CompleteMultipartUploadWithAnIncompletePart_ReportsMalformedXml(string part)
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: $"<CompleteMultipartUpload>{part}</CompleteMultipartUpload>"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
    }

    [Theory]
    [InlineData("+1")]
    [InlineData(" 1")]
    public async Task CompleteMultipartUploadWithALooselyFormattedPartNumber_ReportsMalformedXml(
        string partNumber
    )
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");
        var part = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            body: "data"
        );

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: $"""
            <CompleteMultipartUpload>
              <Part><PartNumber>{partNumber}</PartNumber><ETag>{part.Response.Headers.ETag}</ETag></Part>
            </CompleteMultipartUpload>
            """
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
    }

    [Fact]
    public async Task AbortedUpload_StopsAcceptingParts()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var abort = await Dispatch("DELETE", "/my-bucket/key", query: $"?uploadId={uploadId}");
        var part = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            body: "data"
        );

        Assert.Equal(StatusCodes.Status204NoContent, abort.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, part.Response.StatusCode);
        Assert.Equal("NoSuchUpload", ReadErrorCode(part));
    }

    [Fact]
    public async Task ListParts_ReturnsUploadedPartsInOrder()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");
        await UploadPart("/my-bucket/key", uploadId, 2, "S3Harp!");
        await UploadPart("/my-bucket/key", uploadId, 1, "Hello, ");

        var context = await Dispatch("GET", "/my-bucket/key", query: $"?uploadId={uploadId}");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context);
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListPartsResult", root.Name);
        Assert.Equal(uploadId, root.Element(S3Namespace + "UploadId")?.Value);
        var parts = root.Elements(S3Namespace + "Part").ToArray();
        Assert.Equal(["1", "2"], parts.Select(p => p.Element(S3Namespace + "PartNumber")?.Value));
        Assert.Equal("7", parts[0].Element(S3Namespace + "Size")?.Value);
        Assert.Equal(
            "\"c84cabbaebee9a9631c8be234ac64c26\"",
            parts[0].Element(S3Namespace + "ETag")?.Value
        );
    }

    [Fact]
    public async Task ListParts_ReportsWhenEachPartWasUploaded()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");
        await UploadPart("/my-bucket/key", uploadId, 1, "Hello, ");

        var context = await Dispatch("GET", "/my-bucket/key", query: $"?uploadId={uploadId}");

        var part = Assert.Single(ReadBody(context).Elements(S3Namespace + "Part"));
        Assert.Equal("2026-09-16T12:00:00.000Z", part.Element(S3Namespace + "LastModified")?.Value);
    }

    [Fact]
    public async Task ListParts_PaginatesWithMaxPartsAndPartNumberMarker()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");
        foreach (var number in new[] { 1, 3, 5 })
        {
            await UploadPart("/my-bucket/key", uploadId, number, "content");
        }

        var first = ReadBody(
            await Dispatch("GET", "/my-bucket/key", query: $"?uploadId={uploadId}&max-parts=2")
        );
        var second = ReadBody(
            await Dispatch(
                "GET",
                "/my-bucket/key",
                query: $"?uploadId={uploadId}&max-parts=2&part-number-marker=3"
            )
        );

        Assert.Equal("2", first.Element(S3Namespace + "MaxParts")?.Value);
        Assert.Equal("0", first.Element(S3Namespace + "PartNumberMarker")?.Value);
        Assert.Equal("true", first.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Equal("3", first.Element(S3Namespace + "NextPartNumberMarker")?.Value);
        Assert.Equal(
            ["1", "3"],
            first
                .Elements(S3Namespace + "Part")
                .Select(p => p.Element(S3Namespace + "PartNumber")?.Value)
        );
        Assert.Equal("3", second.Element(S3Namespace + "PartNumberMarker")?.Value);
        Assert.Equal("false", second.Element(S3Namespace + "IsTruncated")?.Value);
        Assert.Null(second.Element(S3Namespace + "NextPartNumberMarker"));
        Assert.Equal(
            ["5"],
            second
                .Elements(S3Namespace + "Part")
                .Select(p => p.Element(S3Namespace + "PartNumber")?.Value)
        );
    }

    [Theory]
    [InlineData("max-parts=-1")]
    [InlineData("max-parts=two")]
    [InlineData("part-number-marker=first")]
    public async Task ListParts_WithAnUnusablePagingParameter_ReportsInvalidArgument(
        string parameter
    )
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "GET",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}&{parameter}"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task InitiateUpload_AnnouncesTheChecksumAlgorithmAndType()
    {
        await Dispatch("PUT", "/my-bucket");

        var named = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: "?uploads",
            configure: request => request.Headers["x-amz-checksum-algorithm"] = "SHA256"
        );
        var unnamed = await Dispatch("POST", "/my-bucket/other", query: "?uploads");

        Assert.Equal("SHA256", named.Response.Headers["x-amz-checksum-algorithm"]);
        Assert.Equal("COMPOSITE", named.Response.Headers["x-amz-checksum-type"]);
        Assert.Equal("CRC64NVME", unnamed.Response.Headers["x-amz-checksum-algorithm"]);
        Assert.Equal("FULL_OBJECT", unnamed.Response.Headers["x-amz-checksum-type"]);
    }

    [Theory]
    [InlineData("SHA256", "FULL_OBJECT")]
    [InlineData("CRC64NVME", "COMPOSITE")]
    [InlineData("CRC32", "PARTIAL")]
    public async Task InitiateUpload_WithAnUnsupportedChecksumType_ReportsInvalidRequest(
        string algorithm,
        string type
    )
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: "?uploads",
            configure: request =>
            {
                request.Headers["x-amz-checksum-algorithm"] = algorithm;
                request.Headers["x-amz-checksum-type"] = type;
            }
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidRequest", ReadErrorCode(context));
    }

    [Fact]
    public async Task UploadPart_EchoesThePartsChecksum()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate(
            "/my-bucket/key",
            request => request.Headers["x-amz-checksum-algorithm"] = "SHA256"
        );

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            body: "Hello, "
        );

        Assert.Equal(
            "I0Kb2bqY3VFAMJu5sAlLOq1kJDD/9vs8ph8AjOZE80o=",
            context.Response.Headers["x-amz-checksum-sha256"]
        );
    }

    [Fact]
    public async Task CompleteUpload_ReportsTheCompositeChecksumAndChecksTheDeclaredOnes()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate(
            "/my-bucket/key",
            request => request.Headers["x-amz-checksum-algorithm"] = "SHA256"
        );
        var first = await UploadPart("/my-bucket/key", uploadId, 1, "Hello, ");
        var second = await UploadPart("/my-bucket/key", uploadId, 2, "S3Harp!");
        string Body(string firstChecksum) =>
            $"""
                <CompleteMultipartUpload>
                  <Part><PartNumber>1</PartNumber><ETag>{first}</ETag><ChecksumSHA256>{firstChecksum}</ChecksumSHA256></Part>
                  <Part><PartNumber>2</PartNumber><ETag>{second}</ETag></Part>
                </CompleteMultipartUpload>
                """;

        var wrongPart = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: Body("bad=")
        );
        var wrongWhole = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: Body("I0Kb2bqY3VFAMJu5sAlLOq1kJDD/9vs8ph8AjOZE80o="),
            configure: request => request.Headers["x-amz-checksum-sha256"] = "bad="
        );
        var completed = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: Body("I0Kb2bqY3VFAMJu5sAlLOq1kJDD/9vs8ph8AjOZE80o="),
            configure: request =>
                request.Headers["x-amz-checksum-sha256"] =
                    "sDGBh5Sl/cL+/VEtpYWyKkP3wHD+lmz/q9Wq8TQpY8c=-2"
        );

        Assert.Equal("InvalidPart", ReadErrorCode(wrongPart));
        Assert.Equal("BadDigest", ReadErrorCode(wrongWhole));
        Assert.Equal(StatusCodes.Status200OK, completed.Response.StatusCode);
        var result = ReadBody(completed);
        Assert.Equal(
            "sDGBh5Sl/cL+/VEtpYWyKkP3wHD+lmz/q9Wq8TQpY8c=-2",
            result.Element(S3Namespace + "ChecksumSHA256")?.Value
        );
        Assert.Equal("COMPOSITE", result.Element(S3Namespace + "ChecksumType")?.Value);
    }

    [Fact]
    public async Task ListParts_ReportsTheChecksumAlgorithmTypeAndEachPartsChecksum()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate(
            "/my-bucket/key",
            request => request.Headers["x-amz-checksum-algorithm"] = "CRC32"
        );
        await UploadPart("/my-bucket/key", uploadId, 1, "Hello, ");

        var root = ReadBody(
            await Dispatch("GET", "/my-bucket/key", query: $"?uploadId={uploadId}")
        );

        Assert.Equal("CRC32", root.Element(S3Namespace + "ChecksumAlgorithm")?.Value);
        Assert.Equal("COMPOSITE", root.Element(S3Namespace + "ChecksumType")?.Value);
        var part = Assert.Single(root.Elements(S3Namespace + "Part"));
        Assert.Equal("3ldvBQ==", part.Element(S3Namespace + "ChecksumCRC32")?.Value);
    }

    [Fact]
    public async Task GetObject_ByPartNumber_WithChecksumMode_ReportsThatPartsChecksum()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate(
            "/my-bucket/key",
            request => request.Headers["x-amz-checksum-algorithm"] = "CRC32"
        );
        var first = await UploadPart("/my-bucket/key", uploadId, 1, "Hello, ");
        var second = await UploadPart("/my-bucket/key", uploadId, 2, "S3Harp!");
        await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: $"""
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>{first}</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>{second}</ETag></Part>
            </CompleteMultipartUpload>
            """
        );

        var part = await Dispatch(
            "GET",
            "/my-bucket/key",
            query: "?partNumber=2",
            configure: request => request.Headers["x-amz-checksum-mode"] = "ENABLED"
        );
        var whole = await Dispatch(
            "HEAD",
            "/my-bucket/key",
            configure: request => request.Headers["x-amz-checksum-mode"] = "ENABLED"
        );

        Assert.Equal("0oUPLw==", part.Response.Headers["x-amz-checksum-crc32"]);
        Assert.Equal("COMPOSITE", part.Response.Headers["x-amz-checksum-type"]);
        Assert.Equal("5m/Xbg==-2", whole.Response.Headers["x-amz-checksum-crc32"]);
    }

    [Fact]
    public async Task UploadPartCopy_FillsThePartFromTheSourceRange()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src", body: "Hello, S3Harp!");
        var uploadId = await Initiate(
            "/my-bucket/key",
            request => request.Headers["x-amz-checksum-algorithm"] = "CRC32"
        );

        var ranged = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/src";
                request.Headers["x-amz-copy-source-range"] = "bytes=7-13";
            }
        );
        var whole = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=2&uploadId={uploadId}",
            configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/src"
        );

        Assert.Equal(StatusCodes.Status200OK, ranged.Response.StatusCode);
        var result = ReadBody(ranged);
        Assert.Equal(S3Namespace + "CopyPartResult", result.Name);
        Assert.Equal("\"" + SecondPartMd5 + "\"", result.Element(S3Namespace + "ETag")?.Value);
        Assert.Equal(
            "2026-09-16T12:00:00.000Z",
            result.Element(S3Namespace + "LastModified")?.Value
        );
        Assert.Equal("0oUPLw==", result.Element(S3Namespace + "ChecksumCRC32")?.Value);
        var parts = ReadBody(
                await Dispatch("GET", "/my-bucket/key", query: $"?uploadId={uploadId}")
            )
            .Elements(S3Namespace + "Part")
            .Select(p => p.Element(S3Namespace + "Size")?.Value);
        Assert.Equal(["7", "14"], parts);
        Assert.Equal(StatusCodes.Status200OK, whole.Response.StatusCode);
    }

    [Theory]
    [InlineData("0-2")]
    [InlineData("bytes=0-2,3-5")]
    public async Task UploadPartCopy_WithAMalformedRange_ReportsInvalidArgument(string range)
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src", body: "Hello");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/src";
                request.Headers["x-amz-copy-source-range"] = range;
            }
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task UploadPartCopy_WithARangeBeyondTheSource_ReportsInvalidRange()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src", body: "Hello");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/src";
                request.Headers["x-amz-copy-source-range"] = "bytes=0-21";
            }
        );

        Assert.Equal("InvalidRange", ReadErrorCode(context));
    }

    [Fact]
    public async Task UploadPartCopy_FromAMissingSource_ReportsNoSuchKey()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/missing"
        );

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(context));
    }

    [Fact]
    public async Task UploadPartCopy_WhoseSourceConditionFails_ReportsPreconditionFailed()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src", body: "Hello");
        var uploadId = await Initiate("/my-bucket/key");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: $"?partNumber=1&uploadId={uploadId}",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/src";
                request.Headers["x-amz-copy-source-if-match"] = "\"badetag\"";
            }
        );

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
    }

    [Fact]
    public async Task UploadPartCopy_IntoAnUnknownUpload_ReportsNoSuchUpload()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src", body: "Hello");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            query: "?partNumber=1&uploadId=missing",
            configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/src"
        );

        Assert.Equal("NoSuchUpload", ReadErrorCode(context));
    }

    [Fact]
    public async Task ListParts_OfAnUnknownUpload_ReportsNoSuchUpload()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("GET", "/my-bucket/key", query: "?uploadId=missing");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchUpload", ReadErrorCode(context));
    }

    [Fact]
    public async Task ListMultipartUploads_ReturnsActiveUploadsInKeyOrder()
    {
        await Dispatch("PUT", "/my-bucket");
        var second = await Initiate("/my-bucket/zulu.txt");
        var first = await Initiate("/my-bucket/alpha.txt");

        var context = await Dispatch("GET", "/my-bucket", query: "?uploads");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var root = ReadBody(context);
        Assert.NotNull(root);
        Assert.Equal(S3Namespace + "ListMultipartUploadsResult", root.Name);
        var uploads = root.Elements(S3Namespace + "Upload").ToArray();
        Assert.Equal(
            ["alpha.txt", "zulu.txt"],
            uploads.Select(u => u.Element(S3Namespace + "Key")?.Value)
        );
        Assert.Equal(
            [first, second],
            uploads.Select(u => u.Element(S3Namespace + "UploadId")?.Value)
        );
    }

    [Fact]
    public async Task CopiedObject_ServesTheSameContent()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src.txt", body: "hello world");

        var copy = await Dispatch(
            "PUT",
            "/my-bucket/dst.txt",
            configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/src.txt"
        );

        Assert.Equal(StatusCodes.Status200OK, copy.Response.StatusCode);
        var result = ReadBody(copy);
        Assert.Equal(S3Namespace + "CopyObjectResult", result?.Name);
        Assert.Equal(
            "\"5eb63bbbe01eeed093cb22bb8f5acdc3\"",
            result?.Element(S3Namespace + "ETag")?.Value
        );
        Assert.Equal("hello world", ReadBodyText(await Dispatch("GET", "/my-bucket/dst.txt")));
    }

    [Fact]
    public async Task CopyWithReplaceDirective_TakesTheRequestContentType()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch(
            "PUT",
            "/my-bucket/src.txt",
            body: "hello",
            configure: request => request.ContentType = "audio/mpeg"
        );

        await Dispatch(
            "PUT",
            "/my-bucket/dst.txt",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/src.txt";
                request.Headers["x-amz-metadata-directive"] = "REPLACE";
                request.ContentType = "audio/ogg";
            }
        );

        var copied = await Dispatch("HEAD", "/my-bucket/dst.txt");
        Assert.Equal("audio/ogg", copied.Response.ContentType);
    }

    [Fact]
    public async Task GetObject_WhenIfNoneMatchNamesTheETag_IsNotModifiedWithoutABody()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/key",
            configure: request =>
                request.Headers.IfNoneMatch = "\"5eb63bbbe01eeed093cb22bb8f5acdc3\""
        );

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", context.Response.Headers.ETag);
        Assert.Equal("", ReadBodyText(context));
    }

    [Fact]
    public async Task GetObject_WhenIfMatchMissesTheETag_FailsThePrecondition()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/key",
            configure: request => request.Headers.IfMatch = "\"ABCORZ\""
        );

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Equal("PreconditionFailed", ReadErrorCode(context));
    }

    [Fact]
    public async Task HeadObject_WhenUnmodifiedSinceTheGivenDate_IsNotModified()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "hello world");

        var context = await Dispatch(
            "HEAD",
            "/my-bucket/key",
            configure: request => request.Headers.IfModifiedSince = "Wed, 16 Sep 2026 12:00:00 GMT"
        );

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("x-amz-copy-source-if-match", "\"ABCORZ\"")]
    [InlineData("x-amz-copy-source-if-none-match", "\"5eb63bbbe01eeed093cb22bb8f5acdc3\"")]
    [InlineData("x-amz-copy-source-if-modified-since", "Wed, 16 Sep 2026 12:00:00 GMT")]
    [InlineData("x-amz-copy-source-if-unmodified-since", "Sat, 29 Oct 1994 19:43:31 GMT")]
    public async Task CopyObject_WhenASourceConditionFails_FailsThePrecondition(
        string header,
        string value
    )
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/src.txt", body: "hello world");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/dst.txt",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/src.txt";
                request.Headers[header] = value;
            }
        );

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Equal("PreconditionFailed", ReadErrorCode(context));
        var probe = await Dispatch("HEAD", "/my-bucket/dst.txt");
        Assert.Equal(StatusCodes.Status404NotFound, probe.Response.StatusCode);
    }

    [Fact]
    public async Task PutObject_WithIfNoneMatchStar_RefusesToOverwrite()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "first");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "second",
            configure: request => request.Headers.IfNoneMatch = "*"
        );

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Equal("PreconditionFailed", ReadErrorCode(context));
        Assert.Equal("first", ReadBodyText(await Dispatch("GET", "/my-bucket/key")));
    }

    [Fact]
    public async Task PutObject_WithIfMatch_OnAMissingKey_ReportsNoSuchKey()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "content",
            configure: request => request.Headers.IfMatch = "*"
        );

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(context));
    }

    [Fact]
    public async Task PutObject_WithAMatchingIfMatch_Overwrites()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "hello world");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "second",
            configure: request => request.Headers.IfMatch = "\"5eb63bbbe01eeed093cb22bb8f5acdc3\""
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("second", ReadBodyText(await Dispatch("GET", "/my-bucket/key")));
    }

    [Fact]
    public async Task CompleteUpload_WithIfNoneMatchStar_RefusesToOverwrite()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "existing");
        var uploadId = await Initiate("/my-bucket/key");
        var etag = await UploadPart("/my-bucket/key", uploadId, 1, "part");

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{etag}</ETag></Part></CompleteMultipartUpload>",
            configure: request => request.Headers.IfNoneMatch = "*"
        );

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Equal("existing", ReadBodyText(await Dispatch("GET", "/my-bucket/key")));
    }

    [Fact]
    public async Task GetObject_WithResponseOverrides_ServesTheRequestedHeadersForThatResponse()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "hello",
            configure: request => request.ContentType = "text/plain"
        );

        var overridden = await Dispatch(
            "GET",
            "/my-bucket/key",
            query: "?response-content-type=foo/bar&response-cache-control=no-cache"
                + "&response-content-disposition=bla&response-content-encoding=aaa"
                + "&response-content-language=esperanto&response-expires=123"
        );

        Assert.Equal(StatusCodes.Status200OK, overridden.Response.StatusCode);
        Assert.Equal("foo/bar", overridden.Response.ContentType);
        Assert.Equal("no-cache", overridden.Response.Headers.CacheControl);
        Assert.Equal("bla", overridden.Response.Headers.ContentDisposition);
        Assert.Equal("aaa", overridden.Response.Headers.ContentEncoding);
        Assert.Equal("esperanto", overridden.Response.Headers.ContentLanguage);
        Assert.Equal("123", overridden.Response.Headers.Expires);
        Assert.Equal("hello", ReadBodyText(overridden));
        var plain = await Dispatch("HEAD", "/my-bucket/key");
        Assert.Equal("text/plain", plain.Response.ContentType);
        Assert.False(plain.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task PutObject_EchoesTheChecksumItStored()
    {
        await Dispatch("PUT", "/my-bucket");

        var put = await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "Hello, S3Harp!",
            configure: request => request.Headers["x-amz-checksum-algorithm"] = "SHA256"
        );

        Assert.Equal(StatusCodes.Status200OK, put.Response.StatusCode);
        Assert.Equal(
            "Aj0Lx1vWnbGF+irlCT3Pa4HNGctHtn3/Q49ApNekoy8=",
            put.Response.Headers["x-amz-checksum-sha256"]
        );
        Assert.Equal("FULL_OBJECT", put.Response.Headers["x-amz-checksum-type"]);
    }

    [Fact]
    public async Task HeadObject_ReportsTheChecksumOnlyWhenChecksumModeIsEnabled()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "Hello, S3Harp!");

        var plain = await Dispatch("HEAD", "/my-bucket/key");
        var enabled = await Dispatch(
            "HEAD",
            "/my-bucket/key",
            configure: request => request.Headers["x-amz-checksum-mode"] = "enabled"
        );

        Assert.False(plain.Response.Headers.ContainsKey("x-amz-checksum-crc64nvme"));
        Assert.Equal("v+mfzPLqhcw=", enabled.Response.Headers["x-amz-checksum-crc64nvme"]);
        Assert.Equal("FULL_OBJECT", enabled.Response.Headers["x-amz-checksum-type"]);
    }

    [Fact]
    public async Task GetObject_OfARange_LeavesTheWholeObjectChecksumOut()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/key", body: "Hello, S3Harp!");

        var context = await Dispatch(
            "GET",
            "/my-bucket/key",
            configure: request =>
            {
                request.Headers["x-amz-checksum-mode"] = "ENABLED";
                request.Headers.Range = "bytes=0-4";
            }
        );

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-checksum-crc64nvme"));
    }

    [Fact]
    public async Task CopyObject_KeepsTheChecksumUnlessAnAlgorithmIsRequested()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch(
            "PUT",
            "/my-bucket/src",
            body: "Hello, S3Harp!",
            configure: request => request.Headers["x-amz-checksum-algorithm"] = "SHA1"
        );

        var kept = ReadBody(
            await Dispatch(
                "PUT",
                "/my-bucket/kept",
                configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/src"
            )
        );
        var fresh = ReadBody(
            await Dispatch(
                "PUT",
                "/my-bucket/fresh",
                configure: request =>
                {
                    request.Headers["x-amz-copy-source"] = "/my-bucket/src";
                    request.Headers["x-amz-checksum-algorithm"] = "CRC32";
                }
            )
        );

        Assert.Equal(
            "gLagvNJpcFuHJZa/U8arrgX+MoM=",
            kept.Element(S3Namespace + "ChecksumSHA1")?.Value
        );
        Assert.Equal("FULL_OBJECT", kept.Element(S3Namespace + "ChecksumType")?.Value);
        Assert.Equal("NadAdg==", fresh.Element(S3Namespace + "ChecksumCRC32")?.Value);
        var head = await Dispatch(
            "HEAD",
            "/my-bucket/fresh",
            configure: request => request.Headers["x-amz-checksum-mode"] = "ENABLED"
        );
        Assert.Equal("NadAdg==", head.Response.Headers["x-amz-checksum-crc32"]);
    }

    [Fact]
    public async Task PutObject_StoresTheContentHeaders_AndHeadReplaysThem()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "hello",
            configure: request =>
            {
                request.Headers.CacheControl = "public, max-age=14400";
                request.Headers.ContentDisposition = "attachment; filename=key.txt";
                request.Headers.ContentEncoding = "gzip, aws-chunked";
                request.Headers.ContentLanguage = "en-GB";
                request.Headers.Expires = "Thu, 01 Jan 2026 00:00:00 GMT";
            }
        );

        var head = await Dispatch("HEAD", "/my-bucket/key");

        Assert.Equal("public, max-age=14400", head.Response.Headers.CacheControl);
        Assert.Equal("attachment; filename=key.txt", head.Response.Headers.ContentDisposition);
        Assert.Equal("gzip", head.Response.Headers.ContentEncoding);
        Assert.Equal("en-GB", head.Response.Headers.ContentLanguage);
        Assert.Equal("Thu, 01 Jan 2026 00:00:00 GMT", head.Response.Headers.Expires);
    }

    [Fact]
    public async Task PutObject_WithOnlyAwsChunkedEncoding_StoresNoContentEncoding()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch(
            "PUT",
            "/my-bucket/key",
            body: "hello",
            configure: request => request.Headers.ContentEncoding = "aws-chunked"
        );

        var head = await Dispatch("HEAD", "/my-bucket/key");

        Assert.False(head.Response.Headers.ContainsKey("Content-Encoding"));
    }

    [Fact]
    public async Task CompleteUpload_WithAShortNonFinalPart_ReportsEntityTooSmall()
    {
        await Dispatch("PUT", "/my-bucket");
        var uploadId = await Initiate("/my-bucket/key");
        var tiny = await UploadPart("/my-bucket/key", uploadId, 1, "tiny");
        var second = await UploadPart("/my-bucket/key", uploadId, 2, "S3Harp!");

        var context = await Dispatch(
            "POST",
            "/my-bucket/key",
            query: $"?uploadId={uploadId}",
            body: $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{tiny}</ETag></Part>"
                + $"<Part><PartNumber>2</PartNumber><ETag>{second}</ETag></Part></CompleteMultipartUpload>"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("EntityTooSmall", ReadErrorCode(context));
    }

    [Fact]
    public async Task CopyingAnObjectOntoItself_WithoutReplacingAttributes_IsInvalid()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/same.txt", body: "hello");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/same.txt",
            configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/same.txt"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidRequest", ReadErrorCode(context));
    }

    [Fact]
    public async Task CopyingAnObjectOntoItself_WithReplacedAttributes_Succeeds()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/same.txt", body: "hello");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/same.txt",
            configure: request =>
            {
                request.Headers["x-amz-copy-source"] = "/my-bucket/same.txt";
                request.Headers["x-amz-metadata-directive"] = "REPLACE";
                request.Headers["x-amz-meta-note"] = "replaced";
            }
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var head = await Dispatch("HEAD", "/my-bucket/same.txt");
        Assert.Equal("replaced", head.Response.Headers["x-amz-meta-note"]);
    }

    [Fact]
    public async Task CopyingAMissingSource_ReportsNoSuchKey()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "PUT",
            "/my-bucket/dst.txt",
            configure: request => request.Headers["x-amz-copy-source"] = "/my-bucket/missing"
        );

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(context));
    }

    private async Task<string> Initiate(string path, Action<HttpRequest>? configure = null)
    {
        var context = await Dispatch("POST", path, query: "?uploads", configure: configure);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var uploadId = ReadBody(context).Element(S3Namespace + "UploadId")?.Value;
        Assert.False(string.IsNullOrEmpty(uploadId));
        return uploadId;
    }

    private async Task<string> UploadPart(string path, string uploadId, int number, string content)
    {
        var context = await Dispatch(
            "PUT",
            path,
            query: $"?partNumber={number}&uploadId={uploadId}",
            body: content
        );
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var etag = context.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));
        return etag;
    }

    [Fact]
    public async Task GetObject_ByPartNumber_ServesThatPartWithThePartsCount()
    {
        await Dispatch("PUT", "/my-bucket");
        var etag = await CompleteTwoPartUpload("/my-bucket/parts.txt");

        var context = await Dispatch("GET", "/my-bucket/parts.txt", query: "?partNumber=2");

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("S3Harp!", ReadBodyText(context));
        Assert.Equal("bytes 7-13/14", context.Response.Headers.ContentRange);
        Assert.Equal("2", context.Response.Headers["x-amz-mp-parts-count"]);
        Assert.Equal(etag, context.Response.Headers.ETag);
    }

    [Fact]
    public async Task HeadObject_ByPartNumber_ReportsThePartsLengthAndThePartsCount()
    {
        await Dispatch("PUT", "/my-bucket");
        await CompleteTwoPartUpload("/my-bucket/parts.txt");

        var context = await Dispatch("HEAD", "/my-bucket/parts.txt", query: "?partNumber=1");

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(7, context.Response.ContentLength);
        Assert.Equal("bytes 0-6/14", context.Response.Headers.ContentRange);
        Assert.Equal("2", context.Response.Headers["x-amz-mp-parts-count"]);
        Assert.Equal("", ReadBodyText(context));
    }

    [Fact]
    public async Task GetObject_ByAPartNumberBeyondTheLast_ReportsInvalidPart()
    {
        await Dispatch("PUT", "/my-bucket");
        await CompleteTwoPartUpload("/my-bucket/parts.txt");

        var context = await Dispatch("GET", "/my-bucket/parts.txt", query: "?partNumber=3");

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidPart", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObject_OfAnObjectStoredInOnePiece_ByPartNumberOne_ServesTheWholeObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/whole.txt", body: "hello world");

        var context = await Dispatch("GET", "/my-bucket/whole.txt", query: "?partNumber=1");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("hello world", ReadBodyText(context));
        Assert.False(context.Response.Headers.ContainsKey("x-amz-mp-parts-count"));
        var beyond = await Dispatch("GET", "/my-bucket/whole.txt", query: "?partNumber=2");
        Assert.Equal("InvalidPart", ReadErrorCode(beyond));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10001")]
    [InlineData("two")]
    public async Task GetObject_WithAnUnusablePartNumber_ReportsInvalidArgument(string partNumber)
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/whole.txt", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/whole.txt",
            query: $"?partNumber={partNumber}"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObject_WithBothARangeAndAPartNumber_ReportsInvalidRequest()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/whole.txt", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/whole.txt",
            query: "?partNumber=1",
            configure: request => request.Headers.Range = "bytes=0-1"
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidRequest", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObject_ByPartNumber_OfAMissingKey_ReportsNoSuchKey()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch("GET", "/my-bucket/missing.txt", query: "?partNumber=1");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(context));
    }

    [Fact]
    public async Task VirtualHostedRequests_NameTheBucketInTheHost()
    {
        await Dispatch(
            "PUT",
            "/",
            configure: request => request.Host = new HostString("my-bucket.localhost")
        );

        var put = await Dispatch(
            "PUT",
            "/greeting.txt",
            body: "hello",
            configure: request => request.Host = new HostString("my-bucket.localhost", 9000)
        );
        var get = await Dispatch(
            "GET",
            "/greeting.txt",
            configure: request => request.Host = new HostString("my-bucket.localhost", 9000)
        );
        var listed = ReadBody(await Dispatch("GET", "/my-bucket", query: "?list-type=2"));

        Assert.Equal(StatusCodes.Status200OK, put.Response.StatusCode);
        Assert.Equal("hello", ReadBodyText(get));
        Assert.Equal(
            ["greeting.txt"],
            listed
                .Elements(S3Namespace + "Contents")
                .Select(c => c.Element(S3Namespace + "Key")?.Value)
        );
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
        await Dispatch(
            "PUT",
            "/my-bucket/greeting.txt",
            body: "hello world",
            configure: request =>
            {
                request.ContentType = "text/plain";
                request.Headers["x-amz-meta-note"] = "from-test";
            }
        );

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
    public async Task GetObject_WithARange_ServesTheSliceAs206()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers.Range = "bytes=0-4"
        );

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("hello", ReadBodyText(context));
        Assert.Equal(5, context.Response.ContentLength);
        Assert.Equal("bytes 0-4/11", context.Response.Headers.ContentRange);
        Assert.Equal("\"5eb63bbbe01eeed093cb22bb8f5acdc3\"", context.Response.Headers.ETag);
    }

    [Fact]
    public async Task GetObject_WithASuffixRange_ServesTheTail()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers.Range = "bytes=-5"
        );

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("world", ReadBodyText(context));
        Assert.Equal("bytes 6-10/11", context.Response.Headers.ContentRange);
    }

    [Fact]
    public async Task GetObject_WithAnUnsatisfiableRange_ReportsInvalidRange()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers.Range = "bytes=999-"
        );

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
        Assert.Equal("InvalidRange", ReadErrorCode(context));
    }

    [Fact]
    public async Task GetObject_WithAMalformedRange_ServesTheWholeObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello world");

        var context = await Dispatch(
            "GET",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers.Range = "bytes=nonsense"
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("hello world", ReadBodyText(context));
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
    public async Task DeleteObject_WhoseIfMatchFails_ReportsPreconditionFailedAndKeepsTheObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        var context = await Dispatch(
            "DELETE",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers.IfMatch = "\"badetag\""
        );

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Equal("PreconditionFailed", ReadErrorCode(context));
        var after = await Dispatch("GET", "/my-bucket/greeting.txt");
        Assert.Equal(StatusCodes.Status200OK, after.Response.StatusCode);
    }

    [Fact]
    public async Task DeleteObject_WhoseSizeAndLastModifiedTimeMatch_RemovesTheObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        var context = await Dispatch(
            "DELETE",
            "/my-bucket/greeting.txt",
            configure: request =>
            {
                request.Headers["x-amz-if-match-size"] = "5";
                request.Headers["x-amz-if-match-last-modified-time"] =
                    "Wed, 16 Sep 2026 12:00:00 GMT";
            }
        );

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(await Dispatch("GET", "/my-bucket/greeting.txt")));
    }

    [Fact]
    public async Task DeleteObject_WithIfMatchAnyObject_RemovesTheObject()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        var context = await Dispatch(
            "DELETE",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers.IfMatch = "*"
        );

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("NoSuchKey", ReadErrorCode(await Dispatch("GET", "/my-bucket/greeting.txt")));
    }

    [Fact]
    public async Task DeleteObject_OfAnUnknownKey_UnderAFailingCondition_StillSucceeds()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "DELETE",
            "/my-bucket/missing.txt",
            configure: request => request.Headers.IfMatch = "\"badetag\""
        );

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("x-amz-if-match-size", "many")]
    [InlineData("x-amz-if-match-last-modified-time", "yesterday")]
    public async Task DeleteObject_WithAnUnusableConditionValue_ReportsInvalidArgument(
        string header,
        string value
    )
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/greeting.txt", body: "hello");

        var context = await Dispatch(
            "DELETE",
            "/my-bucket/greeting.txt",
            configure: request => request.Headers[header] = value
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("InvalidArgument", ReadErrorCode(context));
    }

    [Fact]
    public async Task DeleteObjects_ChecksEachEntrysConditionAndReportsFailuresPerKey()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/one.txt", body: "1");
        await Dispatch("PUT", "/my-bucket/two.txt", body: "22");
        await Dispatch("PUT", "/my-bucket/three.txt", body: "333");

        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: """
            <Delete>
              <Object><Key>one.txt</Key><ETag>"badetag"</ETag></Object>
              <Object><Key>two.txt</Key><Size>2</Size></Object>
              <Object><Key>three.txt</Key><LastModifiedTime>2026-09-16T12:00:00Z</LastModifiedTime></Object>
              <Object><Key>never-existed.txt</Key><Size>9</Size></Object>
            </Delete>
            """
        );

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var result = ReadBody(context);
        Assert.Equal(
            ["never-existed.txt", "three.txt", "two.txt"],
            result
                .Elements(S3Namespace + "Deleted")
                .Select(d => d.Element(S3Namespace + "Key")?.Value)
                .Order(StringComparer.Ordinal)
        );
        var error = Assert.Single(result.Elements(S3Namespace + "Error"));
        Assert.Equal("one.txt", error.Element(S3Namespace + "Key")?.Value);
        Assert.Equal("PreconditionFailed", error.Element(S3Namespace + "Code")?.Value);
        Assert.Equal(
            StatusCodes.Status200OK,
            (await Dispatch("GET", "/my-bucket/one.txt")).Response.StatusCode
        );
        Assert.Equal("NoSuchKey", ReadErrorCode(await Dispatch("GET", "/my-bucket/two.txt")));
    }

    [Fact]
    public async Task DeleteObjects_InQuietMode_StillReportsFailedConditions()
    {
        await Dispatch("PUT", "/my-bucket");
        await Dispatch("PUT", "/my-bucket/one.txt", body: "1");

        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: """
            <Delete>
              <Quiet>true</Quiet>
              <Object><Key>one.txt</Key><Size>9</Size></Object>
            </Delete>
            """
        );

        var error = Assert.Single(ReadBody(context).Elements());
        Assert.Equal(S3Namespace + "Error", error.Name);
        Assert.Equal("PreconditionFailed", error.Element(S3Namespace + "Code")?.Value);
    }

    [Fact]
    public async Task DeleteObjects_WithAnUnusableConditionValue_ReportsMalformedXml()
    {
        await Dispatch("PUT", "/my-bucket");

        var context = await Dispatch(
            "POST",
            "/my-bucket",
            query: "?delete",
            body: """
            <Delete><Object><Key>one.txt</Key><Size>many</Size></Object></Delete>
            """
        );

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("MalformedXML", ReadErrorCode(context));
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

    /// <summary>Uploads and completes "Hello, " + "S3Harp!" as two parts, returning the object's ETag header.</summary>
    private async Task<string> CompleteTwoPartUpload(string path)
    {
        var uploadId = await Initiate(path);
        var first = await UploadPart(path, uploadId, 1, "Hello, ");
        var second = await UploadPart(path, uploadId, 2, "S3Harp!");
        var completed = await Dispatch(
            "POST",
            path,
            query: $"?uploadId={uploadId}",
            body: $"""
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>{first}</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>{second}</ETag></Part>
            </CompleteMultipartUpload>
            """
        );
        Assert.Equal(StatusCodes.Status200OK, completed.Response.StatusCode);
        var etag = ReadBody(completed).Element(S3Namespace + "ETag")?.Value;
        Assert.False(string.IsNullOrEmpty(etag));
        return etag;
    }

    private async Task<DefaultHttpContext> Dispatch(
        string method,
        string path,
        string? query = null,
        string? body = null,
        Action<HttpRequest>? configure = null
    )
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

    /// <summary>The root element of the XML response body.</summary>
    private static XElement ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        var root = XDocument.Load(context.Response.Body).Root;
        Assert.NotNull(root);
        return root;
    }

    private static string? ReadErrorCode(DefaultHttpContext context) =>
        ReadBody(context).Element("Code")?.Value;
}
