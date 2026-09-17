using System.Collections.Frozen;
using System.Diagnostics;
using System.Xml.Linq;
using S3Harp.Core;
using S3Harp.Server.Authentication;

namespace S3Harp.Server;

/// <summary>The object operations: put, get, head, delete, batch delete, copy and attributes.</summary>
public sealed partial class S3RequestDispatcher
{
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

        if (outcome is PutObjectOutcome.Stored stored)
        {
            context.Response.Headers.ETag = $"\"{stored.ETag}\"";
            ChecksumHeaders.Write(context.Response.Headers, stored.Checksum);
            return new S3StatusResult(StatusCodes.Status200OK);
        }

        return outcome.Status switch
        {
            PutObjectStatus.BucketMissing => new S3ErrorResult(S3Errors.NoSuchBucket),
            PutObjectStatus.ObjectMissing => new S3ErrorResult(S3Errors.NoSuchKey),
            PutObjectStatus.PreconditionFailed => new S3ErrorResult(S3Errors.PreconditionFailed),
            PutObjectStatus.Stored => throw new UnreachableException(),
            _ => throw new UnreachableException(),
        };
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

    /// <summary>The attributes GetObjectAttributes can report, in the order S3 lists them.</summary>
    private static readonly FrozenSet<string> ObjectAttributeNames = FrozenSet.ToFrozenSet(
        ["ETag", "Checksum", "ObjectParts", "StorageClass", "ObjectSize"],
        StringComparer.Ordinal
    );

    private static readonly FrozenSet<string>.AlternateLookup<
        ReadOnlySpan<char>
    > ObjectAttributeLookup = ObjectAttributeNames.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The attribute names a request lists, or null when the list is empty or names an unknown one.</summary>
    private static HashSet<string>? RequestedAttributes(string header)
    {
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var list = header.AsSpan();
        foreach (var range in list.Split(','))
        {
            var name = list[range].Trim();
            if (name.IsEmpty)
            {
                continue;
            }

            if (!ObjectAttributeLookup.TryGetValue(name, out var known))
            {
                return null;
            }

            requested.Add(known);
        }

        return requested.Count == 0 ? null : requested;
    }

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
        if (
            RequestedAttributes(headers["x-amz-object-attributes"].ToString()) is not { } requested
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
}
