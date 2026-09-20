using System.Diagnostics;
using System.Xml.Linq;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>The multipart upload operations: initiate, upload and copy parts, list, complete and abort.</summary>
internal sealed partial class S3RequestDispatcher
{
    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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

    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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

    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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

    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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

    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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

    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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

    /// <exception cref="System.Data.Common.DbException">The index's database failed the operation.</exception>
    /// <exception cref="IOException">The file system refused or failed the operation.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not access the path.</exception>
    /// <exception cref="System.Text.Json.JsonException">A stored record is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A stored record is malformed.</exception>
    /// <exception cref="FormatException">A stored value is malformed.</exception>
    /// <exception cref="IOException">The response could not be written.</exception>
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
}
