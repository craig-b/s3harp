using System.Globalization;
using System.Xml.Linq;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>
/// Routes each authenticated request to its S3 operation. S3 dispatches on
/// verb + path shape + query markers, so the operation table lives here as
/// explicit pattern matches.
/// </summary>
public sealed class S3RequestDispatcher(
    IMetadataIndex index,
    StorageEngine engine,
    RootCredentials credentials,
    TimeProvider timeProvider)
{
    private const string MetadataHeaderPrefix = "x-amz-meta-";

    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>Query markers selecting S3 subresources and operations S3Harp will grow into.</summary>
    private static readonly string[] SubresourceMarkers =
    [
        "acl", "cors", "delete", "lifecycle", "location", "policy",
        "tagging", "versioning", "versions", "website",
    ];

    public async Task<IResult> DispatchAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (SubresourceMarkers.Any(context.Request.Query.ContainsKey))
        {
            return new S3ErrorResult(S3Errors.NotImplemented);
        }

        var (bucket, key) = ParsePath(context.Request.Path.Value ?? "/");
        var cancellationToken = context.RequestAborted;
        var query = context.Request.Query;
        return (context.Request.Method, bucket, key) switch
        {
            ("GET", "", null) => await ListBucketsAsync(cancellationToken).ConfigureAwait(false),
            ("GET", not "", null) when query["list-type"] == "2" =>
                await ListObjectsV2Async(context, bucket, cancellationToken).ConfigureAwait(false),
            ("PUT", not "", null) =>
                await CreateBucketAsync(context, bucket, cancellationToken).ConfigureAwait(false),
            ("HEAD", not "", null) =>
                await HeadBucketAsync(bucket, cancellationToken).ConfigureAwait(false),
            ("DELETE", not "", null) =>
                await DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false),
            ("POST", not "", not null) when query.ContainsKey("uploads") =>
                await InitiateUploadAsync(context, bucket, key, cancellationToken)
                    .ConfigureAwait(false),
            ("POST", not "", not null) when query.ContainsKey("uploadId") =>
                await CompleteUploadAsync(context, bucket, key, cancellationToken)
                    .ConfigureAwait(false),
            ("PUT", not "", not null) when query.ContainsKey("uploadId") =>
                await UploadPartAsync(context, bucket, key, cancellationToken)
                    .ConfigureAwait(false),
            ("DELETE", not "", not null) when query.ContainsKey("uploadId") =>
                await AbortUploadAsync(bucket, key, query["uploadId"].ToString(), cancellationToken)
                    .ConfigureAwait(false),
            ("PUT", not "", not null)
                when context.Request.Headers.ContainsKey("x-amz-copy-source") =>
                await CopyObjectAsync(context, bucket, key, cancellationToken)
                    .ConfigureAwait(false),
            ("PUT", not "", not null) =>
                await PutObjectAsync(context, bucket, key, cancellationToken).ConfigureAwait(false),
            ("GET", not "", not null) =>
                await GetObjectAsync(context, bucket, key, includeContent: true, cancellationToken)
                    .ConfigureAwait(false),
            ("HEAD", not "", not null) =>
                await GetObjectAsync(context, bucket, key, includeContent: false, cancellationToken)
                    .ConfigureAwait(false),
            ("DELETE", not "", not null) =>
                await DeleteObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false),
            _ => new S3ErrorResult(S3Errors.NotImplemented),
        };
    }

    private static (string Bucket, string? Key) ParsePath(string path)
    {
        var trimmed = path.Trim('/');
        var separator = trimmed.IndexOf('/', StringComparison.Ordinal);
        return separator < 0
            ? (trimmed, null)
            : (trimmed[..separator], trimmed[(separator + 1)..]);
    }

    private async Task<IResult> ListBucketsAsync(CancellationToken cancellationToken)
    {
        var buckets = await index.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", standalone: null),
            new XElement(S3Namespace + "ListAllMyBucketsResult",
                new XElement(S3Namespace + "Owner",
                    new XElement(S3Namespace + "ID", credentials.AccessKeyId),
                    new XElement(S3Namespace + "DisplayName", credentials.AccessKeyId)),
                new XElement(S3Namespace + "Buckets",
                    buckets.Select(bucket => new XElement(S3Namespace + "Bucket",
                        new XElement(S3Namespace + "Name", bucket.Name),
                        new XElement(S3Namespace + "CreationDate", FormatTimestamp(bucket.CreatedAt)))))));
        return new S3XmlResult(StatusCodes.Status200OK, document);
    }

    private async Task<IResult> CreateBucketAsync(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        if (!BucketName.IsValid(bucket))
        {
            return new S3ErrorResult(S3Errors.InvalidBucketName);
        }

        var created = await index
            .TryCreateBucketAsync(bucket, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            return new S3ErrorResult(S3Errors.BucketAlreadyOwnedByYou);
        }

        context.Response.Headers.Location = "/" + bucket;
        return new S3StatusResult(StatusCodes.Status200OK);
    }

    private async Task<IResult> HeadBucketAsync(string bucket, CancellationToken cancellationToken) =>
        await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status200OK)
            : new S3ErrorResult(S3Errors.NoSuchBucket);

    private async Task<IResult> DeleteBucketAsync(
        string bucket, CancellationToken cancellationToken) =>
        await index.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false) switch
        {
            DeleteBucketResult.Deleted => new S3StatusResult(StatusCodes.Status204NoContent),
            DeleteBucketResult.NotEmpty => new S3ErrorResult(S3Errors.BucketNotEmpty),
            _ => new S3ErrorResult(S3Errors.NoSuchBucket),
        };

    private async Task<IResult> ListObjectsV2Async(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var query = context.Request.Query;
        var prefix = query["prefix"].ToString();
        var delimiter = query["delimiter"].ToString() is { Length: > 0 } value ? value : null;
        var startAfter = query["start-after"].ToString();
        var continuationToken = query["continuation-token"].ToString();

        var maxKeys = 1000;
        if (query.ContainsKey("max-keys")
            && (!int.TryParse(query["max-keys"], out maxKeys) || maxKeys < 0))
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        maxKeys = Math.Min(maxKeys, 1000);

        var fromKey = "";
        if (continuationToken.Length > 0)
        {
            try
            {
                fromKey = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(continuationToken));
            }
            catch (FormatException)
            {
                return new S3ErrorResult(S3Errors.InvalidArgument);
            }
        }
        else if (startAfter.Length > 0)
        {
            fromKey = startAfter + "\0";
        }

        var listing = maxKeys == 0
            ? new ObjectListing([], [], IsTruncated: false, null)
            : await engine.ListObjectsAsync(
                bucket, prefix, delimiter, fromKey, maxKeys, cancellationToken)
                .ConfigureAwait(false);

        var root = new XElement(S3Namespace + "ListBucketResult",
            new XElement(S3Namespace + "Name", bucket),
            new XElement(S3Namespace + "Prefix", prefix),
            new XElement(S3Namespace + "MaxKeys", maxKeys),
            new XElement(S3Namespace + "KeyCount",
                listing.Objects.Count + listing.CommonPrefixes.Count),
            new XElement(S3Namespace + "IsTruncated",
                listing.IsTruncated ? "true" : "false"));
        if (delimiter is not null)
        {
            root.Add(new XElement(S3Namespace + "Delimiter", delimiter));
        }

        if (startAfter.Length > 0)
        {
            root.Add(new XElement(S3Namespace + "StartAfter", startAfter));
        }

        if (continuationToken.Length > 0)
        {
            root.Add(new XElement(S3Namespace + "ContinuationToken", continuationToken));
        }

        if (listing.NextFromKey is not null)
        {
            root.Add(new XElement(S3Namespace + "NextContinuationToken",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(listing.NextFromKey))));
        }

        root.Add(listing.Objects.Select(record => new XElement(S3Namespace + "Contents",
            new XElement(S3Namespace + "Key", record.Key),
            new XElement(S3Namespace + "LastModified", FormatTimestamp(record.LastModified)),
            new XElement(S3Namespace + "ETag", $"\"{record.ETag}\""),
            new XElement(S3Namespace + "Size", record.Size),
            new XElement(S3Namespace + "StorageClass", "STANDARD"))));
        root.Add(listing.CommonPrefixes.Select(commonPrefix =>
            new XElement(S3Namespace + "CommonPrefixes",
                new XElement(S3Namespace + "Prefix", commonPrefix))));

        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(new XDeclaration("1.0", "UTF-8", standalone: null), root));
    }

    private async Task<IResult> PutObjectAsync(
        HttpContext context, string bucket, string key, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        PutObjectOutcome outcome;
        try
        {
            outcome = await engine.PutObjectAsync(
                bucket, key, context.Request.Body, context.Request.ContentType,
                ReadMetadataHeaders(context.Request), cancellationToken).ConfigureAwait(false);
        }
        catch (PayloadVerificationException exception)
        {
            return new S3ErrorResult(exception.Error);
        }

        if (!outcome.BucketExists)
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        context.Response.Headers.ETag = $"\"{outcome.ETag}\"";
        return new S3StatusResult(StatusCodes.Status200OK);
    }

    private async Task<IResult> GetObjectAsync(
        HttpContext context, string bucket, string key, bool includeContent,
        CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var download = await engine.GetObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        if (download is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        var range = RangeHeader.Evaluate(
            context.Request.Headers.Range.ToString(), download.Record.Size);
        if (range.Outcome == RangeOutcome.Unsatisfiable)
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            return new S3ErrorResult(S3Errors.InvalidRange);
        }

        if (includeContent)
        {
            return new S3ObjectResult(download.Record, download.Content, range);
        }

        await download.Content.DisposeAsync().ConfigureAwait(false);
        return new S3ObjectResult(download.Record, content: null, range);
    }

    private async Task<IResult> DeleteObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        await engine.DeleteObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        return new S3StatusResult(StatusCodes.Status204NoContent);
    }

    private async Task<IResult> InitiateUploadAsync(
        HttpContext context, string bucket, string key, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var uploadId = await engine.InitiateUploadAsync(
            bucket, key, context.Request.ContentType, ReadMetadataHeaders(context.Request),
            cancellationToken).ConfigureAwait(false);
        if (uploadId is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(
                new XDeclaration("1.0", "UTF-8", standalone: null),
                new XElement(S3Namespace + "InitiateMultipartUploadResult",
                    new XElement(S3Namespace + "Bucket", bucket),
                    new XElement(S3Namespace + "Key", key),
                    new XElement(S3Namespace + "UploadId", uploadId))));
    }

    private async Task<IResult> UploadPartAsync(
        HttpContext context, string bucket, string key, CancellationToken cancellationToken)
    {
        if (context.Request.Headers.ContainsKey("x-amz-copy-source"))
        {
            return new S3ErrorResult(S3Errors.NotImplemented);
        }

        if (!int.TryParse(context.Request.Query["partNumber"], out var partNumber)
            || partNumber is < 1 or > 10_000)
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        UploadPartOutcome outcome;
        try
        {
            outcome = await engine.UploadPartAsync(
                bucket, key, context.Request.Query["uploadId"].ToString(), partNumber,
                context.Request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (PayloadVerificationException exception)
        {
            return new S3ErrorResult(exception.Error);
        }

        if (!outcome.UploadExists)
        {
            return new S3ErrorResult(S3Errors.NoSuchUpload);
        }

        context.Response.Headers.ETag = $"\"{outcome.ETag}\"";
        return new S3StatusResult(StatusCodes.Status200OK);
    }

    private async Task<IResult> CompleteUploadAsync(
        HttpContext context, string bucket, string key, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        List<(int PartNumber, string ETag)> parts;
        try
        {
            var document = await XDocument.LoadAsync(
                context.Request.Body, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            parts = [.. document.Root!
                .Elements().Where(e => e.Name.LocalName == "Part")
                .Select(part => (
                    int.Parse(
                        part.Elements().First(e => e.Name.LocalName == "PartNumber").Value,
                        CultureInfo.InvariantCulture),
                    part.Elements().First(e => e.Name.LocalName == "ETag").Value))];
        }
        catch (Exception exception) when (
            exception is System.Xml.XmlException or InvalidOperationException
                or FormatException or NullReferenceException)
        {
            return new S3ErrorResult(S3Errors.MalformedXML);
        }

        var outcome = await engine.CompleteUploadAsync(
            bucket, key, context.Request.Query["uploadId"].ToString(), parts, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            CompleteUploadStatus.NoSuchUpload => new S3ErrorResult(S3Errors.NoSuchUpload),
            CompleteUploadStatus.InvalidPart => new S3ErrorResult(S3Errors.InvalidPart),
            CompleteUploadStatus.InvalidPartOrder => new S3ErrorResult(S3Errors.InvalidPartOrder),
            _ => new S3XmlResult(
                StatusCodes.Status200OK,
                new XDocument(
                    new XDeclaration("1.0", "UTF-8", standalone: null),
                    new XElement(S3Namespace + "CompleteMultipartUploadResult",
                        new XElement(S3Namespace + "Location", $"/{bucket}/{key}"),
                        new XElement(S3Namespace + "Bucket", bucket),
                        new XElement(S3Namespace + "Key", key),
                        new XElement(S3Namespace + "ETag", $"\"{outcome.ETag}\"")))),
        };
    }

    private async Task<IResult> AbortUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        return await engine.AbortUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status204NoContent)
            : new S3ErrorResult(S3Errors.NoSuchUpload);
    }

    private async Task<IResult> CopyObjectAsync(
        HttpContext context, string bucket, string key, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var source = Uri.UnescapeDataString(
            context.Request.Headers["x-amz-copy-source"].ToString()).TrimStart('/');
        var separator = source.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == source.Length - 1)
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var sourceBucket = source[..separator];
        var sourceKey = source[(separator + 1)..];
        if (!await index.BucketExistsAsync(sourceBucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var replaceMetadata = string.Equals(
            context.Request.Headers["x-amz-metadata-directive"], "REPLACE",
            StringComparison.OrdinalIgnoreCase);
        var outcome = await engine.CopyObjectAsync(
            sourceBucket, sourceKey, bucket, key,
            replaceMetadata ? ReadMetadataHeaders(context.Request) : null,
            cancellationToken).ConfigureAwait(false);
        if (outcome is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(
                new XDeclaration("1.0", "UTF-8", standalone: null),
                new XElement(S3Namespace + "CopyObjectResult",
                    new XElement(S3Namespace + "ETag", $"\"{outcome.ETag}\""),
                    new XElement(S3Namespace + "LastModified", FormatTimestamp(outcome.LastModified)))));
    }

    private static Dictionary<string, string> ReadMetadataHeaders(HttpRequest request)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                metadata[header.Key[MetadataHeaderPrefix.Length..].ToLowerInvariant()] =
                    header.Value.ToString();
            }
        }

        return metadata;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
