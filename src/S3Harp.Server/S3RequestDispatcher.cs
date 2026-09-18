using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
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
internal sealed partial class S3RequestDispatcher(
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
    private static readonly FrozenSet<string> SubresourceMarkers = FrozenSet.ToFrozenSet(
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
        ],
        StringComparer.OrdinalIgnoreCase
    );

    public async Task<IResult> DispatchAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var parameter in context.Request.Query.Keys)
        {
            if (SubresourceMarkers.Contains(parameter))
            {
                return new S3ErrorResult(S3Errors.NotImplemented);
            }
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

    /// <summary>Reads a non-negative count from a query or header value, falling back when it is absent.</summary>
    private static bool TryReadCount(StringValues raw, int fallback, out int value)
    {
        value = fallback;
        return raw.Count == 0 || DecimalDigits.TryParseInt32(raw.ToString(), out value);
    }

    private XElement OwnerElement(string elementName) =>
        S3Xml.Element(
            elementName,
            S3Xml.Element("ID", credentials.AccessKeyId),
            S3Xml.Element("DisplayName", credentials.AccessKeyId)
        );

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

    /// <summary>The characters URL encoding leaves as they are, plus the slashes S3 leaves literal in keys.</summary>
    private static readonly SearchValues<char> UnencodedKeyCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~/"
    );

    /// <summary>URL-encodes a key for <c>encoding-type=url</c>, keeping the slashes S3 leaves literal.</summary>
    private static string UrlEncodeKey(string value)
    {
        var key = value.AsSpan();
        if (!key.ContainsAnyExcept(UnencodedKeyCharacters))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var range in key.Split('/'))
        {
            if (range.Start.Value > 0)
            {
                builder.Append('/');
            }

            builder.Append(Uri.EscapeDataString(key[range]));
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );
}
