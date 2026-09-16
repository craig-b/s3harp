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
    /// <summary>The version id S3 assigns to objects in unversioned buckets.</summary>
    private const string NullVersionId = "null";

    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// Every S3 subresource query marker beyond the operations S3Harp serves.
    /// Each names a distinct operation, so a request carrying one is answered
    /// as NotImplemented rather than as the plain bucket or object operation.
    /// </summary>
    private static readonly string[] SubresourceMarkers =
    [
        "accelerate", "acl", "analytics", "attributes", "cors", "encryption",
        "intelligent-tiering", "inventory", "legal-hold", "lifecycle", "location",
        "logging", "metrics", "notification", "object-lock", "ownershipControls",
        "policy", "policyStatus", "publicAccessBlock", "replication", "requestPayment",
        "restore", "retention", "select", "tagging", "torrent", "versioning", "website",
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
            ("GET", not "", null) when query.ContainsKey("uploads") =>
                await ListMultipartUploadsAsync(bucket, cancellationToken).ConfigureAwait(false),
            ("GET", not "", null) when query.ContainsKey("versions") =>
                await ListObjectVersionsAsync(context, bucket, cancellationToken)
                    .ConfigureAwait(false),
            ("GET", not "", null) =>
                await ListObjectsAsync(context, bucket, cancellationToken).ConfigureAwait(false),
            ("GET", not "", not null) when query.ContainsKey("uploadId") =>
                await ListPartsAsync(bucket, key, query["uploadId"].ToString(), cancellationToken)
                    .ConfigureAwait(false),
            ("PUT", not "", null) =>
                await CreateBucketAsync(context, bucket, cancellationToken).ConfigureAwait(false),
            ("HEAD", not "", null) =>
                await HeadBucketAsync(bucket, cancellationToken).ConfigureAwait(false),
            ("DELETE", not "", null) =>
                await DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false),
            ("POST", not "", null) when query.ContainsKey("delete") =>
                await DeleteObjectsAsync(context, bucket, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// Splits <c>/bucket/key</c>. Only the leading slash is structural: every
    /// later character, including a trailing slash, belongs to the key.
    /// </summary>
    private static (string Bucket, string? Key) ParsePath(string path)
    {
        var withoutRoot = path.StartsWith('/') ? path[1..] : path;
        var separator = withoutRoot.IndexOf('/', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (withoutRoot, null);
        }

        var key = withoutRoot[(separator + 1)..];
        return (withoutRoot[..separator], key.Length > 0 ? key : null);
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
        await engine.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false) switch
        {
            DeleteBucketResult.Deleted => new S3StatusResult(StatusCodes.Status204NoContent),
            DeleteBucketResult.NotEmpty => new S3ErrorResult(S3Errors.BucketNotEmpty),
            _ => new S3ErrorResult(S3Errors.NoSuchBucket),
        };

    private async Task<IResult> ListObjectsAsync(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var query = context.Request.Query;
        if (ListingQuery.TryParse(query) is not { } listingQuery)
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var marker = query["marker"].ToString();
        var listing = await ListAsync(
            bucket, listingQuery, ResumeAfterMarker(marker, listingQuery.Delimiter), cancellationToken)
            .ConfigureAwait(false);
        var root = new XElement(S3Namespace + "ListBucketResult",
            new XElement(S3Namespace + "Name", bucket),
            new XElement(S3Namespace + "Prefix", listingQuery.Encode(listingQuery.Prefix)),
            new XElement(S3Namespace + "Marker", listingQuery.Encode(marker)));
        if (listing.IsTruncated && listingQuery.Delimiter is not null)
        {
            root.Add(new XElement(S3Namespace + "NextMarker",
                listingQuery.Encode(LastEntry(listing))));
        }

        root.Add(
            new XElement(S3Namespace + "MaxKeys", listingQuery.MaxKeys),
            new XElement(S3Namespace + "IsTruncated", listing.IsTruncated ? "true" : "false"));
        AppendListing(root, listingQuery, listing, record => ContentsElement(listingQuery, record));
        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(new XDeclaration("1.0", "UTF-8", standalone: null), root));
    }

    private async Task<IResult> ListObjectsV2Async(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var query = context.Request.Query;
        if (ListingQuery.TryParse(query) is not { } listingQuery)
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var startAfter = query["start-after"].ToString();
        var continuationToken = query["continuation-token"].ToString();

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

        var listing = await ListAsync(bucket, listingQuery, fromKey, cancellationToken)
            .ConfigureAwait(false);
        var root = new XElement(S3Namespace + "ListBucketResult",
            new XElement(S3Namespace + "Name", bucket),
            new XElement(S3Namespace + "Prefix", listingQuery.Encode(listingQuery.Prefix)),
            new XElement(S3Namespace + "MaxKeys", listingQuery.MaxKeys),
            new XElement(S3Namespace + "KeyCount",
                listing.Objects.Count + listing.CommonPrefixes.Count),
            new XElement(S3Namespace + "IsTruncated",
                listing.IsTruncated ? "true" : "false"));
        if (startAfter.Length > 0)
        {
            root.Add(new XElement(S3Namespace + "StartAfter", listingQuery.Encode(startAfter)));
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

        AppendListing(root, listingQuery, listing, record => ContentsElement(listingQuery, record));
        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(new XDeclaration("1.0", "UTF-8", standalone: null), root));
    }

    private Task<ObjectListing> ListAsync(
        string bucket, ListingQuery query, string fromKey, CancellationToken cancellationToken) =>
        query.MaxKeys == 0
            ? Task.FromResult(new ObjectListing([], [], IsTruncated: false, null))
            : engine.ListObjectsAsync(
                bucket, query.Prefix, query.Delimiter, fromKey, query.MaxKeys, cancellationToken);

    /// <summary>
    /// The scan position a V1 marker resumes from. A marker naming a delimiter
    /// group resumes beyond the whole group, since the group was already
    /// reported as a common prefix.
    /// </summary>
    private static string ResumeAfterMarker(string marker, string? delimiter)
    {
        if (marker.Length == 0)
        {
            return "";
        }

        var afterMarker = marker + "\0";
        return delimiter is not null && marker.EndsWith(delimiter, StringComparison.Ordinal)
            ? KeyRange.PrefixSuccessor(marker) ?? afterMarker
            : afterMarker;
    }

    private static XElement ContentsElement(ListingQuery query, ObjectRecord record) =>
        new(S3Namespace + "Contents",
            new XElement(S3Namespace + "Key", query.Encode(record.Key)),
            new XElement(S3Namespace + "LastModified", FormatTimestamp(record.LastModified)),
            new XElement(S3Namespace + "ETag", $"\"{record.ETag}\""),
            new XElement(S3Namespace + "Size", record.Size),
            new XElement(S3Namespace + "StorageClass", "STANDARD"));

    /// <summary>The last entry a listing reported, in key order, across contents and common prefixes.</summary>
    private static string LastEntry(ObjectListing listing)
    {
        var lastKey = listing.Objects.Count > 0 ? listing.Objects[^1].Key : null;
        var lastPrefix = listing.CommonPrefixes.Count > 0 ? listing.CommonPrefixes[^1] : null;
        return lastKey is null ? lastPrefix ?? ""
            : lastPrefix is null ? lastKey
            : string.CompareOrdinal(lastKey, lastPrefix) > 0 ? lastKey : lastPrefix;
    }

    /// <summary>
    /// Every object is its own single, current version: S3Harp buckets are
    /// unversioned, which S3 reports as the "null" version.
    /// </summary>
    private async Task<IResult> ListObjectVersionsAsync(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var query = context.Request.Query;
        if (ListingQuery.TryParse(query) is not { } listingQuery)
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var keyMarker = query["key-marker"].ToString();
        var listing = await ListAsync(
            bucket, listingQuery, ResumeAfterMarker(keyMarker, listingQuery.Delimiter), cancellationToken)
            .ConfigureAwait(false);
        var root = new XElement(S3Namespace + "ListVersionsResult",
            new XElement(S3Namespace + "Name", bucket),
            new XElement(S3Namespace + "Prefix", listingQuery.Encode(listingQuery.Prefix)),
            new XElement(S3Namespace + "KeyMarker", listingQuery.Encode(keyMarker)),
            new XElement(S3Namespace + "VersionIdMarker", query["version-id-marker"].ToString()));
        if (listing.IsTruncated)
        {
            root.Add(
                new XElement(S3Namespace + "NextKeyMarker", listingQuery.Encode(LastEntry(listing))),
                new XElement(S3Namespace + "NextVersionIdMarker", NullVersionId));
        }

        root.Add(
            new XElement(S3Namespace + "MaxKeys", listingQuery.MaxKeys),
            new XElement(S3Namespace + "IsTruncated", listing.IsTruncated ? "true" : "false"));
        AppendListing(root, listingQuery, listing, record => new XElement(S3Namespace + "Version",
            new XElement(S3Namespace + "Key", listingQuery.Encode(record.Key)),
            new XElement(S3Namespace + "VersionId", NullVersionId),
            new XElement(S3Namespace + "IsLatest", "true"),
            new XElement(S3Namespace + "LastModified", FormatTimestamp(record.LastModified)),
            new XElement(S3Namespace + "ETag", $"\"{record.ETag}\""),
            new XElement(S3Namespace + "Size", record.Size),
            OwnerElement("Owner"),
            new XElement(S3Namespace + "StorageClass", "STANDARD")));
        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(new XDeclaration("1.0", "UTF-8", standalone: null), root));
    }

    private static void AppendListing(
        XElement root, ListingQuery query, ObjectListing listing, Func<ObjectRecord, XElement> entry)
    {
        if (query.EncodingType.Length > 0)
        {
            root.Add(new XElement(S3Namespace + "EncodingType", query.EncodingType));
        }

        if (query.Delimiter is not null)
        {
            root.Add(new XElement(S3Namespace + "Delimiter", query.Encode(query.Delimiter)));
        }

        root.Add(listing.Objects.Select(entry));
        root.Add(listing.CommonPrefixes.Select(commonPrefix =>
            new XElement(S3Namespace + "CommonPrefixes",
                new XElement(S3Namespace + "Prefix", query.Encode(commonPrefix)))));
    }

    /// <summary>The listing parameters both ListObjects versions share.</summary>
    private sealed record ListingQuery(
        string Prefix, string? Delimiter, int MaxKeys, string EncodingType)
    {
        private const int MaxKeysCeiling = 1000;

        public Func<string, string> Encode { get; } =
            EncodingType.Length > 0 ? UrlEncodeKey : value => value;

        /// <summary>Parses the shared parameters, or returns null when one is invalid.</summary>
        public static ListingQuery? TryParse(IQueryCollection query)
        {
            var encodingType = query["encoding-type"].ToString();
            if (encodingType is not ("" or "url"))
            {
                return null;
            }

            var maxKeys = MaxKeysCeiling;
            if (query.ContainsKey("max-keys")
                && (!int.TryParse(query["max-keys"], out maxKeys) || maxKeys < 0))
            {
                return null;
            }

            var delimiter = query["delimiter"].ToString();
            return new ListingQuery(
                query["prefix"].ToString(),
                delimiter.Length > 0 ? delimiter : null,
                Math.Min(maxKeys, MaxKeysCeiling),
                encodingType);
        }
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
                bucket, key, context.Request.Body, RequestAttributes.Read(context.Request),
                WriteConditionHeaders.Parse(context.Request.Headers),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PayloadVerificationException exception)
        {
            return new S3ErrorResult(exception.Error);
        }

        switch (outcome.Status)
        {
            case PutObjectStatus.BucketMissing:
                return new S3ErrorResult(S3Errors.NoSuchBucket);
            case PutObjectStatus.ObjectMissing:
                return new S3ErrorResult(S3Errors.NoSuchKey);
            case PutObjectStatus.PreconditionFailed:
                return new S3ErrorResult(S3Errors.PreconditionFailed);
            default:
                context.Response.Headers.ETag = $"\"{outcome.ETag}\"";
                return new S3StatusResult(StatusCodes.Status200OK);
        }
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

        var precondition = Preconditions.Evaluate(
            ConditionalHeaders.FromRequest(context.Request.Headers),
            download.Record.ETag, download.Record.LastModified);
        if (precondition != PreconditionOutcome.Proceed)
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            return precondition == PreconditionOutcome.NotModified
                ? new S3NotModifiedResult(download.Record)
                : new S3ErrorResult(S3Errors.PreconditionFailed);
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

    private async Task<IResult> DeleteObjectsAsync(
        HttpContext context, string bucket, CancellationToken cancellationToken)
    {
        const int maxKeysPerRequest = 1000;
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        List<string> keys;
        bool quiet;
        try
        {
            var document = await LoadRequestXmlAsync(context.Request, cancellationToken)
                .ConfigureAwait(false);
            keys = [.. document.Root!
                .Elements().Where(e => e.Name.LocalName == "Object")
                .Select(o => o.Elements().First(e => e.Name.LocalName == "Key").Value)];
            quiet = string.Equals(
                document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Quiet")?.Value,
                "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is System.Xml.XmlException or InvalidOperationException
                or NullReferenceException)
        {
            return new S3ErrorResult(S3Errors.MalformedXML);
        }

        if (keys.Count > maxKeysPerRequest)
        {
            return new S3ErrorResult(S3Errors.MalformedXML);
        }

        var result = new XElement(S3Namespace + "DeleteResult");
        foreach (var key in keys)
        {
            await engine.DeleteObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            if (!quiet)
            {
                result.Add(new XElement(S3Namespace + "Deleted",
                    new XElement(S3Namespace + "Key", key)));
            }
        }

        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(new XDeclaration("1.0", "UTF-8", standalone: null), result));
    }

    private async Task<IResult> ListPartsAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var upload = await index.FindUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (upload is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchUpload);
        }

        var parts = await index.ListPartsAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(
                new XDeclaration("1.0", "UTF-8", standalone: null),
                new XElement(S3Namespace + "ListPartsResult",
                    new XElement(S3Namespace + "Bucket", bucket),
                    new XElement(S3Namespace + "Key", key),
                    new XElement(S3Namespace + "UploadId", uploadId),
                    OwnerElement("Initiator"),
                    OwnerElement("Owner"),
                    new XElement(S3Namespace + "StorageClass", "STANDARD"),
                    new XElement(S3Namespace + "MaxParts", 1000),
                    new XElement(S3Namespace + "IsTruncated", "false"),
                    parts.Select(part => new XElement(S3Namespace + "Part",
                        new XElement(S3Namespace + "PartNumber", part.PartNumber),
                        new XElement(S3Namespace + "ETag", $"\"{part.ETag}\""),
                        new XElement(S3Namespace + "Size", part.Size))))));
    }

    private async Task<IResult> ListMultipartUploadsAsync(
        string bucket, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var uploads = await index.ListUploadsAsync(bucket, cancellationToken)
            .ConfigureAwait(false);
        return new S3XmlResult(
            StatusCodes.Status200OK,
            new XDocument(
                new XDeclaration("1.0", "UTF-8", standalone: null),
                new XElement(S3Namespace + "ListMultipartUploadsResult",
                    new XElement(S3Namespace + "Bucket", bucket),
                    new XElement(S3Namespace + "MaxUploads", 1000),
                    new XElement(S3Namespace + "IsTruncated", "false"),
                    uploads.Select(upload => new XElement(S3Namespace + "Upload",
                        new XElement(S3Namespace + "Key", upload.Key),
                        new XElement(S3Namespace + "UploadId", upload.UploadId),
                        OwnerElement("Initiator"),
                        OwnerElement("Owner"),
                        new XElement(S3Namespace + "StorageClass", "STANDARD"),
                        new XElement(S3Namespace + "Initiated", FormatTimestamp(upload.InitiatedAt)))))));
    }

    private XElement OwnerElement(string elementName) => new(S3Namespace + elementName,
        new XElement(S3Namespace + "ID", credentials.AccessKeyId),
        new XElement(S3Namespace + "DisplayName", credentials.AccessKeyId));

    private async Task<IResult> InitiateUploadAsync(
        HttpContext context, string bucket, string key, CancellationToken cancellationToken)
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var uploadId = await engine.InitiateUploadAsync(
            bucket, key, RequestAttributes.Read(context.Request), cancellationToken)
            .ConfigureAwait(false);
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
            var document = await LoadRequestXmlAsync(context.Request, cancellationToken)
                .ConfigureAwait(false);
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
            bucket, key, context.Request.Query["uploadId"].ToString(), parts,
            WriteConditionHeaders.Parse(context.Request.Headers), cancellationToken)
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            CompleteUploadStatus.NoSuchUpload => new S3ErrorResult(S3Errors.NoSuchUpload),
            CompleteUploadStatus.InvalidPart => new S3ErrorResult(S3Errors.InvalidPart),
            CompleteUploadStatus.InvalidPartOrder => new S3ErrorResult(S3Errors.InvalidPartOrder),
            CompleteUploadStatus.EntityTooSmall => new S3ErrorResult(S3Errors.EntityTooSmall),
            CompleteUploadStatus.ObjectMissing => new S3ErrorResult(S3Errors.NoSuchKey),
            CompleteUploadStatus.PreconditionFailed => new S3ErrorResult(S3Errors.PreconditionFailed),
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

        var sourceRecord = await index.FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (sourceRecord is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        // Every failed source condition is a failed precondition on a copy:
        // there is no cached copy for "not modified" to refer to.
        var sourceCondition = Preconditions.Evaluate(
            ConditionalHeaders.FromCopySource(context.Request.Headers),
            sourceRecord.ETag, sourceRecord.LastModified);
        if (sourceCondition != PreconditionOutcome.Proceed)
        {
            return new S3ErrorResult(S3Errors.PreconditionFailed);
        }

        var replace = string.Equals(
            context.Request.Headers["x-amz-metadata-directive"], "REPLACE",
            StringComparison.OrdinalIgnoreCase);
        var outcome = await engine.CopyObjectAsync(
            sourceBucket, sourceKey, bucket, key,
            replace ? RequestAttributes.Read(context.Request) : null,
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

    /// <summary>
    /// Parses an XML request body. Whitespace is preserved because element text
    /// carries object keys, and a key may consist of nothing but whitespace.
    /// </summary>
    private static Task<XDocument> LoadRequestXmlAsync(
        HttpRequest request, CancellationToken cancellationToken) =>
        XDocument.LoadAsync(request.Body, LoadOptions.PreserveWhitespace, cancellationToken);

    /// <summary>URL-encodes a key for <c>encoding-type=url</c>, keeping the slashes S3 leaves literal.</summary>
    private static string UrlEncodeKey(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
