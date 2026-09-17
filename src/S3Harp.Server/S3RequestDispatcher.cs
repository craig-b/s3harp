using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Primitives;
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
    TimeProvider timeProvider,
    ServiceDomain domain
)
{
    /// <summary>The version id S3 assigns to objects in unversioned buckets.</summary>
    private const string NullVersionId = "null";

    /// <summary>S3 numbers the parts of a multipart upload 1 through 10,000.</summary>
    private const int MaxPartNumber = 10_000;

    /// <summary>
    /// Every S3 subresource query marker beyond the operations S3Harp serves.
    /// Each names a distinct operation, so a request carrying one is answered
    /// as NotImplemented rather than as the plain bucket or object operation.
    /// </summary>
    private static readonly string[] SubresourceMarkers =
    [
        "accelerate",
        "acl",
        "analytics",
        "cors",
        "encryption",
        "intelligent-tiering",
        "inventory",
        "legal-hold",
        "lifecycle",
        "location",
        "logging",
        "metrics",
        "notification",
        "object-lock",
        "ownershipControls",
        "policy",
        "policyStatus",
        "publicAccessBlock",
        "replication",
        "requestPayment",
        "restore",
        "retention",
        "select",
        "tagging",
        "torrent",
        "versioning",
        "website",
    ];

    public async Task<IResult> DispatchAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (SubresourceMarkers.Any(context.Request.Query.ContainsKey))
        {
            return new S3ErrorResult(S3Errors.NotImplemented);
        }

        var (bucket, key) = RequestTarget.Resolve(
            context.Request.Host.Host,
            context.Request.Path.Value ?? "/",
            domain.Name
        );
        var cancellationToken = context.RequestAborted;
        var query = context.Request.Query;
        var operation = (context.Request.Method, bucket, key) switch
        {
            ("GET", "", null) => ListBucketsAsync(cancellationToken),
            ("GET", not "", null) when query["list-type"] == "2" => ListObjectsV2Async(
                context,
                bucket,
                cancellationToken
            ),
            ("GET", not "", null) when query.ContainsKey("uploads") => ListMultipartUploadsAsync(
                bucket,
                cancellationToken
            ),
            ("GET", not "", null) when query.ContainsKey("versions") => ListObjectVersionsAsync(
                context,
                bucket,
                cancellationToken
            ),
            ("GET", not "", null) => ListObjectsAsync(context, bucket, cancellationToken),
            ("GET", not "", not null) when query.ContainsKey("uploadId") => ListPartsAsync(
                context,
                bucket,
                key,
                cancellationToken
            ),
            ("GET", not "", not null) when query.ContainsKey("attributes") =>
                GetObjectAttributesAsync(context, bucket, key, cancellationToken),
            ("PUT", not "", null) => CreateBucketAsync(context, bucket, cancellationToken),
            ("HEAD", not "", null) => HeadBucketAsync(bucket, cancellationToken),
            ("DELETE", not "", null) => DeleteBucketAsync(bucket, cancellationToken),
            ("POST", not "", null) when query.ContainsKey("delete") => DeleteObjectsAsync(
                context,
                bucket,
                cancellationToken
            ),
            ("POST", not "", not null) when query.ContainsKey("uploads") => InitiateUploadAsync(
                context,
                bucket,
                key,
                cancellationToken
            ),
            ("POST", not "", not null) when query.ContainsKey("uploadId") => CompleteUploadAsync(
                context,
                bucket,
                key,
                cancellationToken
            ),
            ("PUT", not "", not null) when query.ContainsKey("uploadId") => UploadPartAsync(
                context,
                bucket,
                key,
                cancellationToken
            ),
            ("DELETE", not "", not null) when query.ContainsKey("uploadId") => AbortUploadAsync(
                bucket,
                key,
                query["uploadId"].ToString(),
                cancellationToken
            ),
            ("PUT", not "", not null)
                when context.Request.Headers.ContainsKey("x-amz-copy-source") => CopyObjectAsync(
                context,
                bucket,
                key,
                cancellationToken
            ),
            ("PUT", not "", not null) => PutObjectAsync(context, bucket, key, cancellationToken),
            ("GET", not "", not null) => GetObjectAsync(
                context,
                bucket,
                key,
                includeContent: true,
                cancellationToken
            ),
            ("HEAD", not "", not null) => GetObjectAsync(
                context,
                bucket,
                key,
                includeContent: false,
                cancellationToken
            ),
            ("DELETE", not "", not null) => DeleteObjectAsync(
                context,
                bucket,
                key,
                cancellationToken
            ),
            _ => Task.FromResult<IResult>(new S3ErrorResult(S3Errors.NotImplemented)),
        };
        return await operation.ConfigureAwait(false);
    }

    private async Task<IResult> ListBucketsAsync(CancellationToken cancellationToken)
    {
        var buckets = await index.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        var document = S3Xml.Element(
            "ListAllMyBucketsResult",
            S3Xml.Element(
                "Owner",
                S3Xml.Element("ID", credentials.AccessKeyId),
                S3Xml.Element("DisplayName", credentials.AccessKeyId)
            ),
            S3Xml.Element(
                "Buckets",
                buckets.Select(bucket =>
                    S3Xml.Element(
                        "Bucket",
                        S3Xml.Element("Name", bucket.Name),
                        S3Xml.Element("CreationDate", FormatTimestamp(bucket.CreatedAt))
                    )
                )
            )
        );
        return new S3XmlResult(StatusCodes.Status200OK, document);
    }

    private async Task<IResult> CreateBucketAsync(
        HttpContext context,
        string bucket,
        CancellationToken cancellationToken
    )
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

    private async Task<IResult> HeadBucketAsync(
        string bucket,
        CancellationToken cancellationToken
    ) =>
        await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status200OK)
            : new S3ErrorResult(S3Errors.NoSuchBucket);

    private async Task<IResult> DeleteBucketAsync(
        string bucket,
        CancellationToken cancellationToken
    ) =>
        await engine.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false) switch
        {
            DeleteBucketResult.Deleted => new S3StatusResult(StatusCodes.Status204NoContent),
            DeleteBucketResult.NotEmpty => new S3ErrorResult(S3Errors.BucketNotEmpty),
            DeleteBucketResult.NotFound => new S3ErrorResult(S3Errors.NoSuchBucket),
            _ => throw new UnreachableException(),
        };

    private async Task<IResult> ListObjectsAsync(
        HttpContext context,
        string bucket,
        CancellationToken cancellationToken
    )
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
                bucket,
                listingQuery,
                ResumeAfterMarker(marker, listingQuery.Delimiter),
                cancellationToken
            )
            .ConfigureAwait(false);
        var root = S3Xml.Element(
            "ListBucketResult",
            S3Xml.Element("Name", bucket),
            S3Xml.Element("Prefix", listingQuery.Encode(listingQuery.Prefix)),
            S3Xml.Element("Marker", listingQuery.Encode(marker))
        );
        if (listing.IsTruncated && listingQuery.Delimiter is not null)
        {
            root.Add(S3Xml.Element("NextMarker", listingQuery.Encode(LastEntry(listing))));
        }

        root.Add(
            S3Xml.Element("MaxKeys", listingQuery.MaxKeys),
            S3Xml.Element("IsTruncated", listing.IsTruncated)
        );
        // The original listing always names each object's owner.
        AppendListing(
            root,
            listingQuery,
            listing,
            record => ContentsElement(listingQuery, record, includeOwner: true)
        );
        return new S3XmlResult(StatusCodes.Status200OK, root);
    }

    private async Task<IResult> ListObjectsV2Async(
        HttpContext context,
        string bucket,
        CancellationToken cancellationToken
    )
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
                    Convert.FromBase64String(continuationToken)
                );
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
        var root = S3Xml.Element(
            "ListBucketResult",
            S3Xml.Element("Name", bucket),
            S3Xml.Element("Prefix", listingQuery.Encode(listingQuery.Prefix)),
            S3Xml.Element("MaxKeys", listingQuery.MaxKeys),
            S3Xml.Element("KeyCount", listing.Objects.Count + listing.CommonPrefixes.Count),
            S3Xml.Element("IsTruncated", listing.IsTruncated)
        );
        if (startAfter.Length > 0)
        {
            root.Add(S3Xml.Element("StartAfter", listingQuery.Encode(startAfter)));
        }

        // The token is echoed whenever the client sent one, even empty.
        if (query.ContainsKey("continuation-token"))
        {
            root.Add(S3Xml.Element("ContinuationToken", continuationToken));
        }

        if (listing.NextFromKey is not null)
        {
            root.Add(
                S3Xml.Element(
                    "NextContinuationToken",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(listing.NextFromKey))
                )
            );
        }

        var fetchOwner = string.Equals(
            query["fetch-owner"],
            "true",
            StringComparison.OrdinalIgnoreCase
        );
        AppendListing(
            root,
            listingQuery,
            listing,
            record => ContentsElement(listingQuery, record, includeOwner: fetchOwner)
        );
        return new S3XmlResult(StatusCodes.Status200OK, root);
    }

    private Task<ObjectListing> ListAsync(
        string bucket,
        ListingQuery query,
        string fromKey,
        CancellationToken cancellationToken
    ) =>
        query.MaxKeys == 0
            ? Task.FromResult(new ObjectListing([], [], IsTruncated: false, null))
            : engine.ListObjectsAsync(
                bucket,
                query.Prefix,
                query.Delimiter,
                fromKey,
                query.MaxKeys,
                cancellationToken
            );

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

    private XElement ContentsElement(ListingQuery query, ObjectRecord record, bool includeOwner) =>
        S3Xml.Element(
            "Contents",
            S3Xml.Element("Key", query.Encode(record.Key)),
            S3Xml.Element("LastModified", FormatTimestamp(record.LastModified)),
            S3Xml.Element("ETag", $"\"{record.ETag}\""),
            S3Xml.Element("Size", record.Size),
            includeOwner ? OwnerElement("Owner") : null,
            S3Xml.Element("StorageClass", "STANDARD")
        );

    /// <summary>The last entry a listing reported, in key order, across contents and common prefixes.</summary>
    private static string LastEntry(ObjectListing listing)
    {
        var lastKey = listing.Objects.Count > 0 ? listing.Objects[^1].Key : null;
        var lastPrefix = listing.CommonPrefixes.Count > 0 ? listing.CommonPrefixes[^1] : null;
        return lastKey is null ? lastPrefix ?? ""
            : lastPrefix is null ? lastKey
            : string.CompareOrdinal(lastKey, lastPrefix) > 0 ? lastKey
            : lastPrefix;
    }

    /// <summary>
    /// Every object is its own single, current version: S3Harp buckets are
    /// unversioned, which S3 reports as the "null" version.
    /// </summary>
    private async Task<IResult> ListObjectVersionsAsync(
        HttpContext context,
        string bucket,
        CancellationToken cancellationToken
    )
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
                bucket,
                listingQuery,
                ResumeAfterMarker(keyMarker, listingQuery.Delimiter),
                cancellationToken
            )
            .ConfigureAwait(false);
        var root = S3Xml.Element(
            "ListVersionsResult",
            S3Xml.Element("Name", bucket),
            S3Xml.Element("Prefix", listingQuery.Encode(listingQuery.Prefix)),
            S3Xml.Element("KeyMarker", listingQuery.Encode(keyMarker)),
            S3Xml.Element("VersionIdMarker", query["version-id-marker"].ToString())
        );
        if (listing.IsTruncated)
        {
            root.Add(
                S3Xml.Element("NextKeyMarker", listingQuery.Encode(LastEntry(listing))),
                S3Xml.Element("NextVersionIdMarker", NullVersionId)
            );
        }

        root.Add(
            S3Xml.Element("MaxKeys", listingQuery.MaxKeys),
            S3Xml.Element("IsTruncated", listing.IsTruncated)
        );
        AppendListing(
            root,
            listingQuery,
            listing,
            record =>
                S3Xml.Element(
                    "Version",
                    S3Xml.Element("Key", listingQuery.Encode(record.Key)),
                    S3Xml.Element("VersionId", NullVersionId),
                    S3Xml.Element("IsLatest", "true"),
                    S3Xml.Element("LastModified", FormatTimestamp(record.LastModified)),
                    S3Xml.Element("ETag", $"\"{record.ETag}\""),
                    S3Xml.Element("Size", record.Size),
                    OwnerElement("Owner"),
                    S3Xml.Element("StorageClass", "STANDARD")
                )
        );
        return new S3XmlResult(StatusCodes.Status200OK, root);
    }

    private static void AppendListing(
        XElement root,
        ListingQuery query,
        ObjectListing listing,
        Func<ObjectRecord, XElement> entry
    )
    {
        if (query.EncodingType.Length > 0)
        {
            root.Add(S3Xml.Element("EncodingType", query.EncodingType));
        }

        if (query.Delimiter is not null)
        {
            root.Add(S3Xml.Element("Delimiter", query.Encode(query.Delimiter)));
        }

        root.Add(listing.Objects.Select(entry));
        root.Add(
            listing.CommonPrefixes.Select(commonPrefix =>
                S3Xml.Element("CommonPrefixes", S3Xml.Element("Prefix", query.Encode(commonPrefix)))
            )
        );
    }

    /// <summary>The listing parameters both ListObjects versions share.</summary>
    private sealed record ListingQuery(
        string Prefix,
        string? Delimiter,
        int MaxKeys,
        string EncodingType
    )
    {
        private const int MaxKeysCeiling = 1000;

        /// <summary>The value as the response carries it: URL-encoded when the client asked for it.</summary>
        public string Encode(string value) => EncodingType.Length > 0 ? UrlEncodeKey(value) : value;

        /// <summary>Parses the shared parameters, or returns null when one is invalid.</summary>
        public static ListingQuery? TryParse(IQueryCollection query)
        {
            var encodingType = query["encoding-type"].ToString();
            if (encodingType is not ("" or "url"))
            {
                return null;
            }

            var maxKeys = MaxKeysCeiling;
            if (
                query.ContainsKey("max-keys")
                && (!DecimalDigits.TryParseInt32(query["max-keys"], out maxKeys) || maxKeys < 0)
            )
            {
                return null;
            }

            var delimiter = query["delimiter"].ToString();
            return new ListingQuery(
                query["prefix"].ToString(),
                delimiter.Length > 0 ? delimiter : null,
                Math.Min(maxKeys, MaxKeysCeiling),
                encodingType
            );
        }
    }

    private async Task<IResult> PutObjectAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        PutObjectOutcome outcome;
        try
        {
            outcome = await engine
                .PutObjectAsync(
                    bucket,
                    key,
                    context.Request.Body,
                    RequestAttributes.Read(context.Request),
                    ChecksumHeaders.UploadAlgorithm(context.Request.Headers),
                    WriteConditionHeaders.Parse(context.Request.Headers),
                    cancellationToken
                )
                .ConfigureAwait(false);
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
            case PutObjectStatus.Stored:
                context.Response.Headers.ETag = $"\"{outcome.ETag}\"";
                ChecksumHeaders.Write(context.Response.Headers, outcome.Checksum!);
                return new S3StatusResult(StatusCodes.Status200OK);
            default:
                throw new UnreachableException();
        }
    }

    private async Task<IResult> GetObjectAsync(
        HttpContext context,
        string bucket,
        string key,
        bool includeContent,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var download = await engine
            .GetObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        if (download is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        var precondition = Preconditions.Evaluate(
            ConditionalHeaders.FromRequest(context.Request.Headers),
            download.Record.ETag,
            download.Record.LastModified
        );
        if (precondition != PreconditionOutcome.Proceed)
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            return precondition == PreconditionOutcome.NotModified
                ? new S3NotModifiedResult(download.Record)
                : new S3ErrorResult(S3Errors.PreconditionFailed);
        }

        var (range, partsCount, checksum, refusal) = SelectContent(
            context.Request,
            download.Record
        );
        if (refusal is not null)
        {
            await download.Content.DisposeAsync().ConfigureAwait(false);
            return new S3ErrorResult(refusal);
        }

        var served = ResponseHeaderOverrides.Apply(context.Request.Query, download.Record);
        if (includeContent)
        {
            return new S3ObjectResult(served, download.Content, range, partsCount, checksum);
        }

        await download.Content.DisposeAsync().ConfigureAwait(false);
        return new S3ObjectResult(served, content: null, range, partsCount, checksum);
    }

    /// <summary>
    /// The slice of the object a GET or HEAD serves: the part named by
    /// <c>partNumber</c>, else the <c>Range</c> header's bytes, else the whole object,
    /// with the checksum to announce when the read asks for one: the part's for a
    /// part, the object's for the whole object, none for a range of it. A refusal
    /// names the error to answer with instead.
    /// </summary>
    private static (
        RangeEvaluation Range,
        int? PartsCount,
        Checksum? Checksum,
        S3Error? Refusal
    ) SelectContent(HttpRequest request, ObjectRecord record)
    {
        var rangeHeader = request.Headers.Range.ToString();
        var announce = ChecksumHeaders.ModeEnabled(request.Headers);
        if (!request.Query.TryGetValue("partNumber", out var partNumberValue))
        {
            var range = RangeHeader.Evaluate(rangeHeader, record.Size);
            return (
                range,
                null,
                announce && range.Outcome == RangeOutcome.WholeObject ? record.Checksum : null,
                range.Outcome == RangeOutcome.Unsatisfiable ? S3Errors.InvalidRange : null
            );
        }

        if (
            !DecimalDigits.TryParseInt32(partNumberValue, out var partNumber)
            || partNumber is < 1 or > MaxPartNumber
        )
        {
            return (default, null, null, S3Errors.InvalidArgument);
        }

        if (rangeHeader.Length > 0)
        {
            return (default, null, null, S3Errors.RangeWithPartNumber);
        }

        if (ObjectPart.Select(partNumber, record) is not { } part)
        {
            return (default, null, null, S3Errors.InvalidPart);
        }

        return record.Parts.Count == 0
            ? (part, null, announce ? record.Checksum : null, null)
            : (part, record.Parts.Count, announce ? PartChecksum(record, partNumber) : null, null);
    }

    /// <summary>A part's checksum, in the object's algorithm and type.</summary>
    private static Checksum? PartChecksum(ObjectRecord record, int partNumber) =>
        record.Checksum is { } checksum && record.Parts[partNumber - 1].Checksum is { } value
            ? checksum with
            {
                Value = value,
            }
            : null;

    private async Task<IResult> DeleteObjectAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        if (!DeleteConditions.TryParse(context.Request.Headers, out var condition))
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var status = await engine
            .DeleteObjectAsync(bucket, key, condition, cancellationToken)
            .ConfigureAwait(false);
        return status == DeleteObjectStatus.PreconditionFailed
            ? new S3ErrorResult(S3Errors.PreconditionFailed)
            : new S3StatusResult(StatusCodes.Status204NoContent);
    }

    private async Task<IResult> DeleteObjectsAsync(
        HttpContext context,
        string bucket,
        CancellationToken cancellationToken
    )
    {
        const int maxKeysPerRequest = 1000;
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        if (
            await TryLoadRequestXmlAsync(context.Request, cancellationToken).ConfigureAwait(false)
            is not { } root
        )
        {
            return new S3ErrorResult(S3Errors.MalformedXML);
        }

        List<(string Key, DeleteCondition? Condition)> entries = [];
        foreach (var entry in root.Children("Object"))
        {
            if (
                !DeleteConditions.TryParse(entry, out var condition)
                || entry.Child("Key") is not { } key
            )
            {
                return new S3ErrorResult(S3Errors.MalformedXML);
            }

            entries.Add((key.Value, condition));
        }

        var quiet = string.Equals(
            root.Child("Quiet")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase
        );

        if (entries.Count > maxKeysPerRequest)
        {
            return new S3ErrorResult(S3Errors.MalformedXML);
        }

        var result = S3Xml.Element("DeleteResult");
        foreach (var (key, condition) in entries)
        {
            var status = await engine
                .DeleteObjectAsync(bucket, key, condition, cancellationToken)
                .ConfigureAwait(false);
            if (status == DeleteObjectStatus.PreconditionFailed)
            {
                result.Add(
                    S3Xml.Element(
                        "Error",
                        S3Xml.Element("Key", key),
                        S3Xml.Element("Code", S3Errors.PreconditionFailed.Code),
                        S3Xml.Element("Message", S3Errors.PreconditionFailed.Message)
                    )
                );
            }
            else if (!quiet)
            {
                result.Add(S3Xml.Element("Deleted", S3Xml.Element("Key", key)));
            }
        }

        return new S3XmlResult(StatusCodes.Status200OK, result);
    }

    private async Task<IResult> ListPartsAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        const int maxPartsPerPage = 1000;
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var query = context.Request.Query;
        var uploadId = query["uploadId"].ToString();
        var upload = await index
            .FindUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (upload is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchUpload);
        }

        if (
            !TryReadCount(query["max-parts"], maxPartsPerPage, out var maxParts)
            || !TryReadCount(query["part-number-marker"], 0, out var marker)
        )
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        maxParts = Math.Min(maxParts, maxPartsPerPage);
        var parts = await index
            .ListPartsAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        var (page, truncated) = PageOfParts(parts, part => part.PartNumber, marker, maxParts);

        return new S3XmlResult(
            StatusCodes.Status200OK,
            S3Xml.Element(
                "ListPartsResult",
                S3Xml.Element("Bucket", bucket),
                S3Xml.Element("Key", key),
                S3Xml.Element("UploadId", uploadId),
                OwnerElement("Initiator"),
                OwnerElement("Owner"),
                S3Xml.Element("StorageClass", "STANDARD"),
                S3Xml.Element(
                    "ChecksumAlgorithm",
                    ChecksumAlgorithms.Name(upload.ChecksumAlgorithm)
                ),
                S3Xml.Element("ChecksumType", ChecksumHeaders.TypeName(upload.ChecksumType)),
                S3Xml.Element("PartNumberMarker", marker),
                truncated ? S3Xml.Element("NextPartNumberMarker", page[^1].PartNumber) : null,
                S3Xml.Element("MaxParts", maxParts),
                S3Xml.Element("IsTruncated", truncated),
                page.Select(part =>
                    S3Xml.Element(
                        "Part",
                        S3Xml.Element("PartNumber", part.PartNumber),
                        S3Xml.Element("LastModified", FormatTimestamp(part.LastModified)),
                        S3Xml.Element("ETag", $"\"{part.ETag}\""),
                        S3Xml.Element("Size", part.Size),
                        part.Checksum is null
                            ? null
                            : S3Xml.Element(
                                ChecksumHeaders.ElementName(upload.ChecksumAlgorithm),
                                part.Checksum
                            )
                    )
                )
            )
        );
    }

    /// <summary>Reads a non-negative count from a query or header value, falling back when it is absent.</summary>
    private static bool TryReadCount(StringValues raw, int fallback, out int value)
    {
        value = fallback;
        return raw.Count == 0 || DecimalDigits.TryParseInt32(raw.ToString(), out value);
    }

    /// <summary>The parts numbered beyond the marker, up to the page size, and whether more follow.</summary>
    private static (List<T> Page, bool Truncated) PageOfParts<T>(
        IEnumerable<T> parts,
        Func<T, int> partNumber,
        int marker,
        int maxParts
    )
    {
        var page = parts.Where(part => partNumber(part) > marker).Take(maxParts + 1).ToList();
        var truncated = page.Count > maxParts;
        if (truncated)
        {
            page.RemoveAt(page.Count - 1);
        }

        return (page, truncated);
    }

    /// <summary>The attributes GetObjectAttributes can report, in the order S3 lists them.</summary>
    private static readonly string[] ObjectAttributeNames =
    [
        "ETag",
        "Checksum",
        "ObjectParts",
        "StorageClass",
        "ObjectSize",
    ];

    private async Task<IResult> GetObjectAttributesAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        const int maxPartsPerPage = 1000;
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var headers = context.Request.Headers;
        var requested = headers["x-amz-object-attributes"]
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (
            requested.Count == 0
            || !requested.IsSubsetOf(ObjectAttributeNames)
            || !TryReadCount(headers["x-amz-max-parts"], maxPartsPerPage, out var maxParts)
            || !TryReadCount(headers["x-amz-part-number-marker"], 0, out var marker)
        )
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var record = await index
            .FindObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        maxParts = Math.Min(maxParts, maxPartsPerPage);
        context.Response.Headers.LastModified = HttpDate.Format(record.LastModified);
        return new S3XmlResult(
            StatusCodes.Status200OK,
            S3Xml.Element(
                "GetObjectAttributesResponse",
                requested.Contains("ETag") ? S3Xml.Element("ETag", record.ETag) : null,
                requested.Contains("Checksum") && record.Checksum is not null
                    ? S3Xml.Element("Checksum", ChecksumElements(record.Checksum))
                    : null,
                requested.Contains("ObjectParts") && record.Parts.Count > 0
                    ? ObjectPartsElement(record, marker, maxParts)
                    : null,
                requested.Contains("StorageClass")
                    ? S3Xml.Element("StorageClass", "STANDARD")
                    : null,
                requested.Contains("ObjectSize") ? S3Xml.Element("ObjectSize", record.Size) : null
            )
        );
    }

    /// <summary>One page of a multipart object's parts, numbered from one in upload order.</summary>
    private static XElement ObjectPartsElement(ObjectRecord record, int marker, int maxParts)
    {
        var numbered = record.Parts.Select((part, i) => (Number: i + 1, Part: part));
        var (page, truncated) = PageOfParts(numbered, part => part.Number, marker, maxParts);
        return S3Xml.Element(
            "ObjectParts",
            S3Xml.Element("PartsCount", record.Parts.Count),
            S3Xml.Element("PartNumberMarker", marker),
            truncated ? S3Xml.Element("NextPartNumberMarker", page[^1].Number) : null,
            S3Xml.Element("MaxParts", maxParts),
            S3Xml.Element("IsTruncated", truncated),
            page.Select(entry =>
                S3Xml.Element(
                    "Part",
                    S3Xml.Element("PartNumber", entry.Number),
                    S3Xml.Element("Size", entry.Part.Size),
                    entry.Part.Checksum is not null && record.Checksum is not null
                        ? S3Xml.Element(
                            ChecksumHeaders.ElementName(record.Checksum.Algorithm),
                            entry.Part.Checksum
                        )
                        : null
                )
            )
        );
    }

    private async Task<IResult> ListMultipartUploadsAsync(
        string bucket,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var uploads = await index.ListUploadsAsync(bucket, cancellationToken).ConfigureAwait(false);
        return new S3XmlResult(
            StatusCodes.Status200OK,
            S3Xml.Element(
                "ListMultipartUploadsResult",
                S3Xml.Element("Bucket", bucket),
                S3Xml.Element("MaxUploads", 1000),
                S3Xml.Element("IsTruncated", "false"),
                uploads.Select(upload =>
                    S3Xml.Element(
                        "Upload",
                        S3Xml.Element("Key", upload.Key),
                        S3Xml.Element("UploadId", upload.UploadId),
                        OwnerElement("Initiator"),
                        OwnerElement("Owner"),
                        S3Xml.Element("StorageClass", "STANDARD"),
                        S3Xml.Element("Initiated", FormatTimestamp(upload.InitiatedAt))
                    )
                )
            )
        );
    }

    private XElement OwnerElement(string elementName) =>
        S3Xml.Element(
            elementName,
            S3Xml.Element("ID", credentials.AccessKeyId),
            S3Xml.Element("DisplayName", credentials.AccessKeyId)
        );

    private async Task<IResult> InitiateUploadAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var algorithm =
            ChecksumHeaders.RequestedAlgorithm(context.Request.Headers) ?? ChecksumHeaders.Default;
        if (!ChecksumHeaders.TryReadType(context.Request.Headers, out var requestedType))
        {
            return new S3ErrorResult(S3Errors.ChecksumTypeUnsupported);
        }

        var type = requestedType ?? ChecksumAlgorithms.DefaultType(algorithm);
        if (!ChecksumAlgorithms.Supports(algorithm, type))
        {
            return new S3ErrorResult(S3Errors.ChecksumTypeUnsupported);
        }

        var uploadId = await engine
            .InitiateUploadAsync(
                bucket,
                key,
                RequestAttributes.Read(context.Request),
                algorithm,
                type,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (uploadId is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        ChecksumHeaders.WriteAlgorithm(context.Response.Headers, algorithm, type);
        return new S3XmlResult(
            StatusCodes.Status200OK,
            S3Xml.Element(
                "InitiateMultipartUploadResult",
                S3Xml.Element("Bucket", bucket),
                S3Xml.Element("Key", key),
                S3Xml.Element("UploadId", uploadId)
            )
        );
    }

    private async Task<IResult> UploadPartAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (
            !DecimalDigits.TryParseInt32(context.Request.Query["partNumber"], out var partNumber)
            || partNumber is < 1 or > MaxPartNumber
        )
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        if (context.Request.Headers.ContainsKey("x-amz-copy-source"))
        {
            return await UploadPartCopyAsync(context, bucket, key, partNumber, cancellationToken)
                .ConfigureAwait(false);
        }

        UploadPartOutcome outcome;
        try
        {
            outcome = await engine
                .UploadPartAsync(
                    bucket,
                    key,
                    context.Request.Query["uploadId"].ToString(),
                    partNumber,
                    context.Request.Body,
                    cancellationToken
                )
                .ConfigureAwait(false);
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
        if (outcome.Checksum is { } checksum)
        {
            context.Response.Headers[ChecksumHeaders.HeaderName(checksum.Algorithm)] =
                checksum.Value;
        }

        return new S3StatusResult(StatusCodes.Status200OK);
    }

    private async Task<IResult> UploadPartCopyAsync(
        HttpContext context,
        string bucket,
        string key,
        int partNumber,
        CancellationToken cancellationToken
    )
    {
        if (!TryReadCopySource(context.Request.Headers, out var sourceBucket, out var sourceKey))
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        if (!await index.BucketExistsAsync(sourceBucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var source = await index
            .FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        if (
            Preconditions.Evaluate(
                ConditionalHeaders.FromCopySource(context.Request.Headers),
                source.ETag,
                source.LastModified
            ) != PreconditionOutcome.Proceed
        )
        {
            return new S3ErrorResult(S3Errors.PreconditionFailed);
        }

        if (
            !CopySourceRange.TryParse(
                context.Request.Headers["x-amz-copy-source-range"],
                out var range
            )
        )
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        var outcome = await engine
            .UploadPartCopyAsync(
                bucket,
                key,
                context.Request.Query["uploadId"].ToString(),
                partNumber,
                sourceBucket,
                sourceKey,
                range,
                cancellationToken
            )
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            UploadPartCopyStatus.NoSuchUpload => new S3ErrorResult(S3Errors.NoSuchUpload),
            UploadPartCopyStatus.SourceMissing => new S3ErrorResult(S3Errors.NoSuchKey),
            UploadPartCopyStatus.RangeBeyondSource => new S3ErrorResult(S3Errors.InvalidRange),
            UploadPartCopyStatus.Copied => new S3XmlResult(
                StatusCodes.Status200OK,
                S3Xml.Element(
                    "CopyPartResult",
                    S3Xml.Element("ETag", $"\"{outcome.ETag}\""),
                    S3Xml.Element("LastModified", FormatTimestamp(outcome.LastModified)),
                    outcome.Checksum is { } checksum
                        ? S3Xml.Element(
                            ChecksumHeaders.ElementName(checksum.Algorithm),
                            checksum.Value
                        )
                        : null
                )
            ),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>The bucket and key <c>x-amz-copy-source</c> names, percent-decoded.</summary>
    private static bool TryReadCopySource(
        IHeaderDictionary headers,
        out string sourceBucket,
        out string sourceKey
    )
    {
        var source = Uri.UnescapeDataString(headers["x-amz-copy-source"].ToString()).TrimStart('/');
        var separator = source.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == source.Length - 1)
        {
            sourceBucket = sourceKey = "";
            return false;
        }

        sourceBucket = source[..separator];
        sourceKey = source[(separator + 1)..];
        return true;
    }

    private async Task<IResult> CompleteUploadAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        if (
            await TryLoadRequestXmlAsync(context.Request, cancellationToken).ConfigureAwait(false)
            is not { } root
        )
        {
            return new S3ErrorResult(S3Errors.MalformedXML);
        }

        List<RequestedPart> parts = [];
        foreach (var part in root.Children("Part"))
        {
            if (
                part.Child("PartNumber") is not { } partNumber
                || !DecimalDigits.TryParseInt32(partNumber.Value, out var number)
                || part.Child("ETag") is not { } etag
            )
            {
                return new S3ErrorResult(S3Errors.MalformedXML);
            }

            parts.Add(new RequestedPart(number, etag.Value, DeclaredPartChecksum(part)));
        }

        var expected = ChecksumHeaders.TryFindDeclared(
            context.Request.Headers,
            out var algorithm,
            out var declared
        )
            ? new ChecksumValue(algorithm, declared)
            : null;
        var outcome = await engine
            .CompleteUploadAsync(
                bucket,
                key,
                context.Request.Query["uploadId"].ToString(),
                parts,
                expected,
                WriteConditionHeaders.Parse(context.Request.Headers),
                cancellationToken
            )
            .ConfigureAwait(false);
        return outcome.Status switch
        {
            CompleteUploadStatus.NoSuchUpload => new S3ErrorResult(S3Errors.NoSuchUpload),
            CompleteUploadStatus.InvalidPart => new S3ErrorResult(S3Errors.InvalidPart),
            CompleteUploadStatus.InvalidPartOrder => new S3ErrorResult(S3Errors.InvalidPartOrder),
            CompleteUploadStatus.EntityTooSmall => new S3ErrorResult(S3Errors.EntityTooSmall),
            CompleteUploadStatus.BadDigest => new S3ErrorResult(S3Errors.BadDigest),
            CompleteUploadStatus.ObjectMissing => new S3ErrorResult(S3Errors.NoSuchKey),
            CompleteUploadStatus.PreconditionFailed => new S3ErrorResult(
                S3Errors.PreconditionFailed
            ),
            CompleteUploadStatus.Completed => new S3XmlResult(
                StatusCodes.Status200OK,
                S3Xml.Element(
                    "CompleteMultipartUploadResult",
                    S3Xml.Element("Location", $"/{bucket}/{key}"),
                    S3Xml.Element("Bucket", bucket),
                    S3Xml.Element("Key", key),
                    S3Xml.Element("ETag", $"\"{outcome.ETag}\""),
                    ChecksumElements(outcome.Checksum)
                )
            ),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>The checksum a completion request declares for a part, in whichever <c>Checksum*</c> element.</summary>
    private static ChecksumValue? DeclaredPartChecksum(XElement part)
    {
        const string prefix = "Checksum";
        foreach (var element in part.Elements())
        {
            if (
                element.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal)
                && ChecksumAlgorithms.TryParseName(
                    element.Name.LocalName[prefix.Length..],
                    out var algorithm
                )
            )
            {
                return new ChecksumValue(algorithm, element.Value.Trim());
            }
        }

        return null;
    }

    private async Task<IResult> AbortUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        return await engine
            .AbortUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false)
            ? new S3StatusResult(StatusCodes.Status204NoContent)
            : new S3ErrorResult(S3Errors.NoSuchUpload);
    }

    private async Task<IResult> CopyObjectAsync(
        HttpContext context,
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (!await index.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        if (!TryReadCopySource(context.Request.Headers, out var sourceBucket, out var sourceKey))
        {
            return new S3ErrorResult(S3Errors.InvalidArgument);
        }

        if (!await index.BucketExistsAsync(sourceBucket, cancellationToken).ConfigureAwait(false))
        {
            return new S3ErrorResult(S3Errors.NoSuchBucket);
        }

        var sourceRecord = await index
            .FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (sourceRecord is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        // Every failed source condition is a failed precondition on a copy:
        // there is no cached copy for "not modified" to refer to.
        var sourceCondition = Preconditions.Evaluate(
            ConditionalHeaders.FromCopySource(context.Request.Headers),
            sourceRecord.ETag,
            sourceRecord.LastModified
        );
        if (sourceCondition != PreconditionOutcome.Proceed)
        {
            return new S3ErrorResult(S3Errors.PreconditionFailed);
        }

        var replace = string.Equals(
            context.Request.Headers["x-amz-metadata-directive"],
            "REPLACE",
            StringComparison.OrdinalIgnoreCase
        );

        // A copy onto itself only makes sense as a way to rewrite the object's attributes.
        if (
            !replace
            && string.Equals(sourceBucket, bucket, StringComparison.Ordinal)
            && string.Equals(sourceKey, key, StringComparison.Ordinal)
        )
        {
            return new S3ErrorResult(S3Errors.CopyToSelf);
        }

        var outcome = await engine
            .CopyObjectAsync(
                sourceBucket,
                sourceKey,
                bucket,
                key,
                replace ? RequestAttributes.Read(context.Request) : null,
                ChecksumHeaders.RequestedAlgorithm(context.Request.Headers),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (outcome is null)
        {
            return new S3ErrorResult(S3Errors.NoSuchKey);
        }

        return new S3XmlResult(
            StatusCodes.Status200OK,
            S3Xml.Element(
                "CopyObjectResult",
                S3Xml.Element("ETag", $"\"{outcome.ETag}\""),
                S3Xml.Element("LastModified", FormatTimestamp(outcome.LastModified)),
                ChecksumElements(outcome.Checksum)
            )
        );
    }

    /// <summary>A checksum's value and type elements; nothing for an object without one.</summary>
    private static IEnumerable<XElement> ChecksumElements(Checksum? checksum) =>
        checksum is null
            ? []
            :
            [
                S3Xml.Element(ChecksumHeaders.ElementName(checksum.Algorithm), checksum.Value),
                S3Xml.Element("ChecksumType", ChecksumHeaders.TypeName(checksum.Type)),
            ];

    /// <summary>
    /// Parses an XML request body. Whitespace is preserved because element text
    /// carries object keys, and a key may consist of nothing but whitespace.
    /// </summary>
    /// <summary>The root element of the request body, or null when the body is not well-formed XML.</summary>
    private static async Task<XElement?> TryLoadRequestXmlAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var document = await XDocument
                .LoadAsync(request.Body, LoadOptions.PreserveWhitespace, cancellationToken)
                .ConfigureAwait(false);
            return document.Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>URL-encodes a key for <c>encoding-type=url</c>, keeping the slashes S3 leaves literal.</summary>
    private static string UrlEncodeKey(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );
}
