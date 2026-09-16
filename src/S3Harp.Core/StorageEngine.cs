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
    string? NextFromKey);

/// <summary>The outcome of storing an object: on success, its ETag and stored checksum.</summary>
public sealed record PutObjectOutcome(PutObjectStatus Status, string? ETag, Checksum? Checksum);

/// <summary>The caller-supplied attributes of an object: content type, content headers, and user metadata.</summary>
public sealed record ObjectAttributes(
    string? ContentType, ContentHeaders ContentHeaders, IReadOnlyDictionary<string, string> Metadata);

/// <summary>The outcome of uploading a part.</summary>
public sealed record UploadPartOutcome(bool UploadExists, string? ETag);

/// <summary>The outcome of completing a multipart upload.</summary>
public sealed record CompleteUploadOutcome(CompleteUploadStatus Status, string? ETag);

/// <summary>The outcome of a server-side object copy.</summary>
public sealed record CopyObjectOutcome(string ETag, DateTimeOffset LastModified, Checksum? Checksum);

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
    IMetadataIndex index, BlobStore blobs, TimeProvider timeProvider, StorageLimits limits)
{
    /// <summary>Stores the content as an object, with a full-object checksum of the given algorithm.</summary>
    public async Task<PutObjectOutcome> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        ObjectAttributes attributes,
        ChecksumAlgorithm checksumAlgorithm,
        WriteCondition? condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var write = await blobs.WriteAsync(content, checksumAlgorithm, cancellationToken)
            .ConfigureAwait(false);
        var checksum = new Checksum(checksumAlgorithm, write.Checksum!, ChecksumType.FullObject);
        var record = new ObjectRecord(
            key, write.BlobId, write.Size, write.ContentMd5Hex, PartSizes: [], checksum,
            attributes.ContentType, attributes.ContentHeaders, attributes.Metadata,
            timeProvider.GetUtcNow());
        var stored = await index.PutObjectAsync(bucket, record, condition, cancellationToken)
            .ConfigureAwait(false);
        if (stored.Status != PutObjectStatus.Stored)
        {
            blobs.Delete(write.BlobId);
            return new PutObjectOutcome(stored.Status, null, null);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new PutObjectOutcome(PutObjectStatus.Stored, write.ContentMd5Hex, checksum);
    }

    public async Task<ObjectDownload?> GetObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var record = await index.FindObjectAsync(bucket, key, cancellationToken)
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
        CancellationToken cancellationToken)
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
            var batch = await index.ScanObjectsAsync(bucket, prefix, from, batchSize, cancellationToken)
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
        string bucket, CancellationToken cancellationToken)
    {
        var outcome = await index.DeleteBucketAsync(bucket, cancellationToken).ConfigureAwait(false);
        foreach (var blobId in outcome.ReleasedBlobIds)
        {
            blobs.Delete(blobId);
        }

        return outcome.Status;
    }

    /// <summary>Deletes the object when the condition, if any, holds against it.</summary>
    public async Task<DeleteObjectStatus> DeleteObjectAsync(
        string bucket, string key, DeleteCondition? condition, CancellationToken cancellationToken)
    {
        var deleted = await index.DeleteObjectAsync(bucket, key, condition, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var upload = new MultipartUpload(
            Guid.NewGuid().ToString("N"), key, attributes.ContentType, attributes.ContentHeaders,
            attributes.Metadata, timeProvider.GetUtcNow());
        return await index.TryCreateUploadAsync(bucket, upload, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var write = await blobs.WriteAsync(content, checksum: null, cancellationToken)
            .ConfigureAwait(false);
        var stored = await index.PutPartAsync(
            bucket, key, uploadId,
            new PartRecord(
                partNumber, write.BlobId, write.Size, write.ContentMd5Hex, timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        if (!stored.UploadExists)
        {
            blobs.Delete(write.BlobId);
            return new UploadPartOutcome(UploadExists: false, null);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new UploadPartOutcome(UploadExists: true, write.ContentMd5Hex);
    }

    public async Task<CompleteUploadOutcome> CompleteUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<(int PartNumber, string ETag)> requestedParts,
        WriteCondition? condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedParts);

        var upload = await index.FindUploadAsync(bucket, key, uploadId, cancellationToken)
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
                return new CompleteUploadOutcome(CompleteUploadStatus.InvalidPartOrder, null);
            }
        }

        var storedParts = (await index
            .ListPartsAsync(bucket, key, uploadId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.PartNumber);
        var assembled = new List<PartRecord>(requestedParts.Count);
        foreach (var (partNumber, requestedETag) in requestedParts)
        {
            if (!storedParts.TryGetValue(partNumber, out var part)
                || !string.Equals(
                    part.ETag, requestedETag.Trim('"'), StringComparison.OrdinalIgnoreCase))
            {
                return new CompleteUploadOutcome(CompleteUploadStatus.InvalidPart, null);
            }

            assembled.Add(part);
        }

        if (assembled.Count == 0)
        {
            return new CompleteUploadOutcome(CompleteUploadStatus.InvalidPart, null);
        }

        // Only the final part may fall below the minimum part size.
        if (assembled.Take(assembled.Count - 1).Any(part => part.Size < limits.MinimumPartSize))
        {
            return new CompleteUploadOutcome(CompleteUploadStatus.EntityTooSmall, null);
        }

        var concatenated = await blobs.ConcatenateAsync(
            [.. assembled.Select(p => p.BlobId)], cancellationToken).ConfigureAwait(false);
        var record = new ObjectRecord(
            key, concatenated.BlobId, concatenated.Size, MultipartETag(assembled),
            [.. assembled.Select(part => part.Size)], Checksum: null,
            upload.ContentType, upload.ContentHeaders, upload.Metadata, timeProvider.GetUtcNow());
        var completed = await index
            .CompleteUploadAsync(bucket, uploadId, record, condition, cancellationToken)
            .ConfigureAwait(false);
        if (completed.Status != CompleteUploadStatus.Completed)
        {
            blobs.Delete(concatenated.BlobId);
            return new CompleteUploadOutcome(completed.Status, null);
        }

        foreach (var blobId in completed.PartBlobIds)
        {
            blobs.Delete(blobId);
        }

        if (completed.ReplacedBlobId is not null)
        {
            blobs.Delete(completed.ReplacedBlobId);
        }

        return new CompleteUploadOutcome(CompleteUploadStatus.Completed, record.ETag);
    }

    public async Task<bool> AbortUploadAsync(
        string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        var partBlobs = await index.DeleteUploadAsync(bucket, key, uploadId, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var source = await index.FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var blobId = await blobs.CopyAsync(source.BlobId, cancellationToken).ConfigureAwait(false);
        var checksum = checksumAlgorithm is { } algorithm && algorithm != source.Checksum?.Algorithm
            ? new Checksum(
                algorithm,
                await blobs.ComputeChecksumAsync(blobId, algorithm, cancellationToken).ConfigureAwait(false),
                ChecksumType.FullObject)
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
        var stored = await index.PutObjectAsync(destinationBucket, record, null, cancellationToken)
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
        IReadOnlyList<(int PartNumber, string ETag)> requestedParts,
        CancellationToken cancellationToken)
    {
        var expectedETag = MultipartETag(requestedParts.Select(part => part.ETag.Trim('"')));
        var existing = await index.FindObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        return expectedETag is not null
            && existing is not null
            && string.Equals(existing.ETag, expectedETag, StringComparison.OrdinalIgnoreCase)
            ? new CompleteUploadOutcome(CompleteUploadStatus.Completed, existing.ETag)
            : new CompleteUploadOutcome(CompleteUploadStatus.NoSuchUpload, null);
    }

    private static string MultipartETag(List<PartRecord> parts) =>
        MultipartETag(parts.Select(part => part.ETag))!;

    /// <summary>S3's multipart ETag: the MD5 of the concatenated part MD5s, suffixed with the part count; null when a part ETag is not an MD5.</summary>
    private static string? MultipartETag(IEnumerable<string> partETags)
    {
        var combined = new List<byte>();
        var count = 0;
        foreach (var partETag in partETags)
        {
            if (partETag.Length != 32 || !partETag.All(char.IsAsciiHexDigit))
            {
                return null;
            }

            combined.AddRange(Convert.FromHexString(partETag));
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        // The multipart ETag is a protocol artifact carrying no security claim.
#pragma warning disable CA5351
        var hash = System.Security.Cryptography.MD5.HashData(combined.ToArray());
#pragma warning restore CA5351
        return $"{Convert.ToHexStringLower(hash)}-{count}";
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
