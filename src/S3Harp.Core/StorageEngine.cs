namespace S3Harp.Core;

/// <summary>An object ready to serve: its metadata and an open content stream.</summary>
public sealed record ObjectDownload(ObjectRecord Record, Stream Content);

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
}
