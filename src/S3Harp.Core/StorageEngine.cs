using System.Buffers;
using System.Security.Cryptography;

namespace S3Harp.Core;

/// <summary>An object ready to serve: its metadata and an open content stream.</summary>
public sealed record ObjectDownload(ObjectRecord Record, Stream Content);

/// <summary>
/// One page of a bucket listing: objects, delimiter-grouped common prefixes, and,
/// when truncated, the key position the next page resumes from.
/// </summary>
public sealed record ObjectListing(
    IReadOnlyList<ObjectRecord> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextFromKey
);

/// <summary>The outcome of storing an object: either <see cref="Stored"/> with its ETag and checksum, or <see cref="Refused"/> with the reason.</summary>
public abstract record PutObjectOutcome(PutObjectStatus Status)
{
    /// <summary>The object is stored; its ETag is the content MD5.</summary>
    public sealed record Stored(string ETag, Checksum Checksum)
        : PutObjectOutcome(PutObjectStatus.Stored);

    /// <summary>The write was refused for the given reason and nothing changed.</summary>
    public sealed record Refused(PutObjectStatus Status) : PutObjectOutcome(Status);
}

/// <summary>The caller-supplied attributes of an object: content type, content headers, and user metadata.</summary>
public sealed record ObjectAttributes(
    string? ContentType,
    ContentHeaders ContentHeaders,
    IReadOnlyDictionary<string, string> Metadata
);

/// <summary>The outcome of uploading a part: its ETag and its checksum in the upload's algorithm.</summary>
public sealed record UploadPartOutcome(bool UploadExists, string? ETag, ChecksumValue? Checksum);

public enum UploadPartCopyStatus
{
    Copied,
    NoSuchUpload,
    SourceMissing,
    RangeBeyondSource,
}

/// <summary>The outcome of filling a part from another object: the part's ETag, checksum and time.</summary>
public sealed record UploadPartCopyOutcome(
    UploadPartCopyStatus Status,
    string? ETag,
    ChecksumValue? Checksum,
    DateTimeOffset LastModified
);

/// <summary>A part named in a completion request, with the checksum the client declares for it.</summary>
public sealed record RequestedPart(int PartNumber, string ETag, ChecksumValue? Checksum = null);

/// <summary>The outcome of completing a multipart upload: the object's ETag and checksum.</summary>
public sealed record CompleteUploadOutcome(
    CompleteUploadStatus Status,
    string? ETag,
    Checksum? Checksum
);

/// <summary>The outcome of a server-side object copy.</summary>
public sealed record CopyObjectOutcome(
    string ETag,
    DateTimeOffset LastModified,
    Checksum? Checksum
);

/// <summary>The size limits the engine enforces, with S3's values as the default.</summary>
public sealed record StorageLimits(long MinimumPartSize)
{
    /// <summary>S3's limits: every part but the last must be at least 5 MiB.</summary>
    public static StorageLimits S3 { get; } = new(MinimumPartSize: 5 * 1024 * 1024);
}

/// <summary>
/// The storage engine: coordinates the blob store and the metadata index so the
/// pair always agree, including reclaiming blob files their records release.
/// </summary>
public sealed class StorageEngine(
    IMetadataIndex index,
    BlobStore blobs,
    TimeProvider timeProvider,
    StorageLimits limits
)
{
    /// <summary>Stores the content as an object, with a full-object checksum of the given algorithm.</summary>
    public async Task<PutObjectOutcome> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        ObjectAttributes attributes,
        ChecksumAlgorithm checksumAlgorithm,
        WriteCondition? condition,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var write = await blobs
            .WriteAsync(content, checksumAlgorithm, cancellationToken)
            .ConfigureAwait(false);
        var checksum = new Checksum(checksumAlgorithm, write.Checksum, ChecksumType.FullObject);
        var record = new ObjectRecord(
            key,
            write.BlobId,
            write.Size,
            write.ContentMd5Hex,
            Parts: [],
            checksum,
            attributes.ContentType,
            attributes.ContentHeaders,
            attributes.Metadata,
            timeProvider.GetUtcNow()
        );
        var stored = await index
            .PutObjectAsync(bucket, record, condition, cancellationToken)
            .ConfigureAwait(false);
        if (stored.Status != PutObjectStatus.Stored)
        {
            blobs.Delete(write.BlobId);
            return new PutObjectOutcome.Refused(stored.Status);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new PutObjectOutcome.Stored(write.ContentMd5Hex, checksum);
    }

    public async Task<ObjectDownload?> GetObjectAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken
    )
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var record = await index
                .FindObjectAsync(bucket, key, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return null;
            }

            try
            {
                return new ObjectDownload(record, blobs.OpenRead(record.BlobId));
            }
            catch (FileNotFoundException) when (attempt < maxAttempts)
            {
                // A concurrent overwrite or delete reclaimed this blob between the
                // index read and the open; the fresh lookup sees the outcome.
            }
        }
    }

    public async Task<ObjectListing> ListObjectsAsync(
        string bucket,
        string prefix,
        string? delimiter,
        string fromKey,
        int maxKeys,
        CancellationToken cancellationToken
    )
    {
        const int batchSize = 1000;
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(fromKey);

        var objects = new List<ObjectRecord>();
        var commonPrefixes = new List<string>();
        var from = string.CompareOrdinal(fromKey, prefix) > 0 ? fromKey : prefix;
        var truncated = false;
        var exhausted = false;

        while (!exhausted && !truncated)
        {
            var batch = await index
                .ScanObjectsAsync(bucket, prefix, from, batchSize, cancellationToken)
                .ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            var rescan = false;
            foreach (var record in batch)
            {
                if (objects.Count + commonPrefixes.Count == maxKeys)
                {
                    truncated = true;
                    break;
                }

                var groupPrefix = FindGroupPrefix(record.Key, prefix, delimiter);
                if (groupPrefix is not null)
                {
                    commonPrefixes.Add(groupPrefix);

                    // Jump past every key inside the group so the scan resumes
                    // at the first entry beyond it.
                    var successor = KeyRange.PrefixSuccessor(groupPrefix);
                    if (successor is null)
                    {
                        exhausted = true;
                    }
                    else
                    {
                        from = successor;
                    }

                    rescan = true;
                    break;
                }

                objects.Add(record);
                from = record.Key + "\0";
            }

            if (!rescan && !truncated && batch.Count < batchSize)
            {
                exhausted = true;
            }
        }

        return new ObjectListing(objects, commonPrefixes, truncated, truncated ? from : null);
    }

    /// <summary>
    /// Deletes an empty bucket. In-progress uploads never hold a bucket open:
    /// they are aborted with it and their parts reclaimed, matching S3.
    /// </summary>
    public async Task<DeleteBucketResult> DeleteBucketAsync(
        string bucket,
        CancellationToken cancellationToken
    )
    {
        var outcome = await index
            .DeleteBucketAsync(bucket, cancellationToken)
            .ConfigureAwait(false);
        foreach (var blobId in outcome.ReleasedBlobIds)
        {
            blobs.Delete(blobId);
        }

        return outcome.Status;
    }

    /// <summary>Deletes the object when the condition, if any, holds against it.</summary>
    public async Task<DeleteObjectStatus> DeleteObjectAsync(
        string bucket,
        string key,
        DeleteCondition? condition,
        CancellationToken cancellationToken
    )
    {
        var deleted = await index
            .DeleteObjectAsync(bucket, key, condition, cancellationToken)
            .ConfigureAwait(false);
        if (deleted.BlobId is not null)
        {
            blobs.Delete(deleted.BlobId);
        }

        return deleted.Status;
    }

    public async Task<string?> InitiateUploadAsync(
        string bucket,
        string key,
        ObjectAttributes attributes,
        ChecksumAlgorithm checksumAlgorithm,
        ChecksumType checksumType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var upload = new MultipartUpload(
            Guid.NewGuid().ToString("N"),
            key,
            attributes.ContentType,
            attributes.ContentHeaders,
            attributes.Metadata,
            checksumAlgorithm,
            checksumType,
            timeProvider.GetUtcNow()
        );
        return await index
            .TryCreateUploadAsync(bucket, upload, cancellationToken)
            .ConfigureAwait(false)
            ? upload.UploadId
            : null;
    }

    public async Task<UploadPartOutcome> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken
    )
    {
        var upload = await index
            .FindUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (upload is null)
        {
            return new UploadPartOutcome(UploadExists: false, null, null);
        }

        var write = await blobs
            .WriteAsync(content, upload.ChecksumAlgorithm, cancellationToken)
            .ConfigureAwait(false);
        var stored = await index
            .PutPartAsync(
                bucket,
                key,
                uploadId,
                new PartRecord(
                    partNumber,
                    write.BlobId,
                    write.Size,
                    write.ContentMd5Hex,
                    write.Checksum,
                    timeProvider.GetUtcNow()
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!stored.UploadExists)
        {
            blobs.Delete(write.BlobId);
            return new UploadPartOutcome(UploadExists: false, null, null);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new UploadPartOutcome(
            UploadExists: true,
            write.ContentMd5Hex,
            new ChecksumValue(upload.ChecksumAlgorithm, write.Checksum)
        );
    }

    /// <summary>
    /// Fills a part with a byte range of another object, or the whole of it when no
    /// range is given. The range must lie within the source.
    /// </summary>
    public async Task<UploadPartCopyOutcome> UploadPartCopyAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        string sourceBucket,
        string sourceKey,
        ByteRange? range,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow();
        var upload = await index
            .FindUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (upload is null)
        {
            return new UploadPartCopyOutcome(UploadPartCopyStatus.NoSuchUpload, null, null, now);
        }

        var source = await index
            .FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return new UploadPartCopyOutcome(UploadPartCopyStatus.SourceMissing, null, null, now);
        }

        if (range is { } requested && requested.To >= source.Size)
        {
            return new UploadPartCopyOutcome(
                UploadPartCopyStatus.RangeBeyondSource,
                null,
                null,
                now
            );
        }

        var copy = await blobs
            .CopyRangeAsync(
                source.BlobId,
                range ?? new ByteRange(0, source.Size - 1),
                upload.ChecksumAlgorithm,
                cancellationToken
            )
            .ConfigureAwait(false);
        var stored = await index
            .PutPartAsync(
                bucket,
                key,
                uploadId,
                new PartRecord(
                    partNumber,
                    copy.BlobId,
                    copy.Size,
                    copy.ContentMd5Hex,
                    copy.Checksum,
                    now
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!stored.UploadExists)
        {
            blobs.Delete(copy.BlobId);
            return new UploadPartCopyOutcome(UploadPartCopyStatus.NoSuchUpload, null, null, now);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new UploadPartCopyOutcome(
            UploadPartCopyStatus.Copied,
            copy.ContentMd5Hex,
            new ChecksumValue(upload.ChecksumAlgorithm, copy.Checksum),
            now
        );
    }

    /// <summary>
    /// Assembles the requested parts into the object. A checksum a part is declared
    /// with must be the one recorded for it, and the checksum the client expects of
    /// the object, if any, must be the one computed for it.
    /// </summary>
    public async Task<CompleteUploadOutcome> CompleteUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<RequestedPart> requestedParts,
        ChecksumValue? expectedChecksum,
        WriteCondition? condition,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(requestedParts);

        var upload = await index
            .FindUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (upload is null)
        {
            return await RepeatedCompletionAsync(bucket, key, requestedParts, cancellationToken)
                .ConfigureAwait(false);
        }

        for (var i = 1; i < requestedParts.Count; i++)
        {
            if (requestedParts[i].PartNumber <= requestedParts[i - 1].PartNumber)
            {
                return new CompleteUploadOutcome(CompleteUploadStatus.InvalidPartOrder, null, null);
            }
        }

        var storedParts = (
            await index
                .ListPartsAsync(bucket, key, uploadId, cancellationToken)
                .ConfigureAwait(false)
        ).ToDictionary(p => p.PartNumber);
        var assembled = new List<PartRecord>(requestedParts.Count);
        foreach (var requested in requestedParts)
        {
            if (
                !storedParts.TryGetValue(requested.PartNumber, out var part)
                || !string.Equals(
                    part.ETag,
                    requested.ETag.Trim('"'),
                    StringComparison.OrdinalIgnoreCase
                )
                || !Matches(requested.Checksum, upload.ChecksumAlgorithm, part.Checksum)
            )
            {
                return new CompleteUploadOutcome(CompleteUploadStatus.InvalidPart, null, null);
            }

            assembled.Add(part);
        }

        if (assembled.Count == 0)
        {
            return new CompleteUploadOutcome(CompleteUploadStatus.InvalidPart, null, null);
        }

        // Only the final part may fall below the minimum part size.
        if (assembled.Take(assembled.Count - 1).Any(part => part.Size < limits.MinimumPartSize))
        {
            return new CompleteUploadOutcome(CompleteUploadStatus.EntityTooSmall, null, null);
        }

        var concatenated = await blobs
            .ConcatenateAsync([.. assembled.Select(p => p.BlobId)], cancellationToken)
            .ConfigureAwait(false);
        var checksum = await ObjectChecksumAsync(
                upload,
                assembled,
                concatenated.BlobId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!Matches(expectedChecksum, checksum?.Algorithm, checksum?.Value))
        {
            blobs.Delete(concatenated.BlobId);
            return new CompleteUploadOutcome(CompleteUploadStatus.BadDigest, null, null);
        }

        var record = new ObjectRecord(
            key,
            concatenated.BlobId,
            concatenated.Size,
            MultipartETag(assembled),
            [.. assembled.Select(part => new CompletedPart(part.Size, part.Checksum))],
            checksum,
            upload.ContentType,
            upload.ContentHeaders,
            upload.Metadata,
            timeProvider.GetUtcNow()
        );
        var completed = await index
            .CompleteUploadAsync(bucket, uploadId, record, condition, cancellationToken)
            .ConfigureAwait(false);
        if (completed.Status != CompleteUploadStatus.Completed)
        {
            blobs.Delete(concatenated.BlobId);
            return new CompleteUploadOutcome(completed.Status, null, null);
        }

        foreach (var blobId in completed.PartBlobIds)
        {
            blobs.Delete(blobId);
        }

        if (completed.ReplacedBlobId is not null)
        {
            blobs.Delete(completed.ReplacedBlobId);
        }

        return new CompleteUploadOutcome(CompleteUploadStatus.Completed, record.ETag, checksum);
    }

    /// <summary>True when nothing was declared, or the declared checksum is the recorded one.</summary>
    private static bool Matches(
        ChecksumValue? declared,
        ChecksumAlgorithm? algorithm,
        string? value
    ) =>
        declared is null
        || (
            declared.Algorithm == algorithm
            && string.Equals(declared.Value, value, StringComparison.Ordinal)
        );

    /// <summary>
    /// The completed object's checksum: over its assembled bytes for a full-object
    /// upload, else composed from the parts' checksums. Parts recorded before
    /// checksums were kept leave a composite undefined.
    /// </summary>
    private async Task<Checksum?> ObjectChecksumAsync(
        MultipartUpload upload,
        List<PartRecord> parts,
        string blobId,
        CancellationToken cancellationToken
    )
    {
        if (upload.ChecksumType == ChecksumType.FullObject)
        {
            var value = await blobs
                .ComputeChecksumAsync(blobId, upload.ChecksumAlgorithm, cancellationToken)
                .ConfigureAwait(false);
            return new Checksum(upload.ChecksumAlgorithm, value, ChecksumType.FullObject);
        }

        var partChecksums = new List<string>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Checksum is not { } partChecksum)
            {
                return null;
            }

            partChecksums.Add(partChecksum);
        }

        return new Checksum(
            upload.ChecksumAlgorithm,
            ChecksumAlgorithms.Composite(upload.ChecksumAlgorithm, partChecksums),
            ChecksumType.Composite
        );
    }

    public async Task<bool> AbortUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken
    )
    {
        var partBlobs = await index
            .DeleteUploadAsync(bucket, key, uploadId, cancellationToken)
            .ConfigureAwait(false);
        if (partBlobs is null)
        {
            return false;
        }

        foreach (var blobId in partBlobs)
        {
            blobs.Delete(blobId);
        }

        return true;
    }

    /// <summary>
    /// Copies an object. The copy keeps the source's checksum, its bytes being the
    /// same, unless a different algorithm is asked for, in which case a full-object
    /// checksum of the copy is computed.
    /// </summary>
    public async Task<CopyObjectOutcome?> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        ObjectAttributes? replacement,
        ChecksumAlgorithm? checksumAlgorithm,
        CancellationToken cancellationToken
    )
    {
        var source = await index
            .FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var blobId = await blobs.CopyAsync(source.BlobId, cancellationToken).ConfigureAwait(false);
        var checksum =
            checksumAlgorithm is { } algorithm && algorithm != source.Checksum?.Algorithm
                ? new Checksum(
                    algorithm,
                    await blobs
                        .ComputeChecksumAsync(blobId, algorithm, cancellationToken)
                        .ConfigureAwait(false),
                    ChecksumType.FullObject
                )
                : source.Checksum;
        var record = source with
        {
            Key = destinationKey,
            BlobId = blobId,
            Checksum = checksum,
            ContentType = replacement is null ? source.ContentType : replacement.ContentType,
            ContentHeaders = replacement?.ContentHeaders ?? source.ContentHeaders,
            Metadata = replacement?.Metadata ?? source.Metadata,
            LastModified = timeProvider.GetUtcNow(),
        };
        var stored = await index
            .PutObjectAsync(destinationBucket, record, null, cancellationToken)
            .ConfigureAwait(false);
        if (stored.Status != PutObjectStatus.Stored)
        {
            blobs.Delete(blobId);
            return null;
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new CopyObjectOutcome(record.ETag, record.LastModified, record.Checksum);
    }

    /// <summary>
    /// Completing an upload that no longer exists succeeds again when the object at
    /// the key is the one those exact parts produced: a retried completion is
    /// idempotent, as it is on S3.
    /// </summary>
    private async Task<CompleteUploadOutcome> RepeatedCompletionAsync(
        string bucket,
        string key,
        IReadOnlyList<RequestedPart> requestedParts,
        CancellationToken cancellationToken
    )
    {
        var expectedETag = MultipartETag(requestedParts.Select(part => part.ETag.Trim('"')));
        var existing = await index
            .FindObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        return
            expectedETag is not null
            && existing is not null
            && string.Equals(existing.ETag, expectedETag, StringComparison.OrdinalIgnoreCase)
            ? new CompleteUploadOutcome(
                CompleteUploadStatus.Completed,
                existing.ETag,
                existing.Checksum
            )
            : new CompleteUploadOutcome(CompleteUploadStatus.NoSuchUpload, null, null);
    }

    /// <summary>The multipart ETag of stored parts, whose ETags are always MD5s.</summary>
    private static string MultipartETag(List<PartRecord> parts) =>
        MultipartETag(parts.Select(part => part.ETag))
        ?? throw new InvalidDataException("A stored part's ETag is not an MD5.");

    /// <summary>S3's multipart ETag: the MD5 of the concatenated part MD5s, suffixed with the part count; null when a part ETag is not an MD5.</summary>
    private static string? MultipartETag(IEnumerable<string> partETags)
    {
        // The multipart ETag is a protocol artifact carrying no security claim.
#pragma warning disable CA5351
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
        Span<byte> digest = stackalloc byte[16];
        var count = 0;
        foreach (var partETag in partETags)
        {
            if (
                partETag.Length != 32
                || Convert.FromHexString(partETag, digest, out _, out var written)
                    != OperationStatus.Done
                || written != digest.Length
            )
            {
                return null;
            }

            md5.AppendData(digest);
            count++;
        }

        return count == 0 ? null : $"{Convert.ToHexStringLower(md5.GetHashAndReset())}-{count}";
    }

    private static string? FindGroupPrefix(string key, string prefix, string? delimiter)
    {
        if (string.IsNullOrEmpty(delimiter))
        {
            return null;
        }

        var separator = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
        return separator < 0 ? null : key[..(separator + delimiter.Length)];
    }
}
