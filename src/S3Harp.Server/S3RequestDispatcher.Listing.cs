using System.Xml.Linq;
using S3Harp.Core;

namespace S3Harp.Server;

/// <summary>The listing operations: ListObjects (V1 and V2) and ListObjectVersions, with their shared paging.</summary>
internal sealed partial class S3RequestDispatcher
{
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
}
