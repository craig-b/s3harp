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

/// <summary>The outcome of storing an object.</summary>
public sealed record PutObjectOutcome(PutObjectStatus Status, string? ETag);

/// <summary>The caller-supplied attributes of an object: content type, content headers, and user metadata.</summary>
public sealed record ObjectAttributes(
    string? ContentType, ContentHeaders ContentHeaders, IReadOnlyDictionary<string, string> Metadata);

/// <summary>The outcome of uploading a part.</summary>
public sealed record UploadPartOutcome(bool UploadExists, string? ETag);

/// <summary>The outcome of completing a multipart upload.</summary>
public sealed record CompleteUploadOutcome(CompleteUploadStatus Status, string? ETag);

/// <summary>The outcome of a server-side object copy.</summary>
public sealed record CopyObjectOutcome(string ETag, DateTimeOffset LastModified);

/// <summary>
/// The storage engine: coordinates the blob store and the metadata index so the
/// pair always agree, including reclaiming blob files their records release.
/// </summary>
public sealed class StorageEngine(IMetadataIndex index, BlobStore blobs, TimeProvider timeProvider)
{
    public async Task<PutObjectOutcome> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        ObjectAttributes attributes,
        WriteCondition? condition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var write = await blobs.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        var record = new ObjectRecord(
            key, write.BlobId, write.Size, write.ContentMd5Hex, attributes.ContentType,
            attributes.ContentHeaders, attributes.Metadata, timeProvider.GetUtcNow());
        var stored = await index.PutObjectAsync(bucket, record, condition, cancellationToken)
            .ConfigureAwait(false);
        if (stored.Status != PutObjectStatus.Stored)
        {
            blobs.Delete(write.BlobId);
            return new PutObjectOutcome(stored.Status, null);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new PutObjectOutcome(PutObjectStatus.Stored, write.ContentMd5Hex);
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

    public async Task DeleteObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        var blobId = await index.DeleteObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        if (blobId is not null)
        {
            blobs.Delete(blobId);
        }
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
        var write = await blobs.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        var stored = await index.PutPartAsync(
            bucket, key, uploadId,
            new PartRecord(partNumber, write.BlobId, write.Size, write.ContentMd5Hex),
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
            return new CompleteUploadOutcome(CompleteUploadStatus.NoSuchUpload, null);
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

        var concatenated = await blobs.ConcatenateAsync(
            [.. assembled.Select(p => p.BlobId)], cancellationToken).ConfigureAwait(false);
        var record = new ObjectRecord(
            key, concatenated.BlobId, concatenated.Size, MultipartETag(assembled),
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

    public async Task<CopyObjectOutcome?> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        ObjectAttributes? replacement,
        CancellationToken cancellationToken)
    {
        var source = await index.FindObjectAsync(sourceBucket, sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var blobId = await blobs.CopyAsync(source.BlobId, cancellationToken).ConfigureAwait(false);
        var record = source with
        {
            Key = destinationKey,
            BlobId = blobId,
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

        return new CopyObjectOutcome(record.ETag, record.LastModified);
    }

    private static string MultipartETag(List<PartRecord> parts)
    {
        var combined = new byte[parts.Count * 16];
        for (var i = 0; i < parts.Count; i++)
        {
            Convert.FromHexString(parts[i].ETag).CopyTo(combined, i * 16);
        }

        // The multipart ETag is S3's MD5-of-part-MD5s format; a protocol artifact,
        // carrying no security claim.
#pragma warning disable CA5351
        var hash = System.Security.Cryptography.MD5.HashData(combined);
#pragma warning restore CA5351
        return $"{Convert.ToHexStringLower(hash)}-{parts.Count}";
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
