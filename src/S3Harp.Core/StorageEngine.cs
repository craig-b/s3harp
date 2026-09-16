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
public sealed record PutObjectOutcome(bool BucketExists, string? ETag);

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
        string? contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var write = await blobs.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        var record = new ObjectRecord(
            key, write.BlobId, write.Size, write.ContentMd5Hex, contentType, metadata,
            timeProvider.GetUtcNow());
        var stored = await index.PutObjectAsync(bucket, record, cancellationToken)
            .ConfigureAwait(false);
        if (!stored.BucketExists)
        {
            blobs.Delete(write.BlobId);
            return new PutObjectOutcome(BucketExists: false, null);
        }

        if (stored.ReplacedBlobId is not null)
        {
            blobs.Delete(stored.ReplacedBlobId);
        }

        return new PutObjectOutcome(BucketExists: true, write.ContentMd5Hex);
    }

    public async Task<ObjectDownload?> GetObjectAsync(
        string bucket, string key, CancellationToken cancellationToken)
    {
        var record = await index.FindObjectAsync(bucket, key, cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : new ObjectDownload(record, blobs.OpenRead(record.BlobId));
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
